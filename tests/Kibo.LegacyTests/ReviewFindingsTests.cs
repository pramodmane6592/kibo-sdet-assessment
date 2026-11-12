using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Kibo.TestingFramework;
using Kibo.TestingFramework.Clients;
using Kibo.TestingFramework.Configuration;
using Kibo.TestingFramework.Polling;

namespace Kibo.LegacyTests;

/// <summary>
/// Regression tests from an adversarial review of <see cref="Poller"/>, the diagnostics handlers and
/// <see cref="KiboApiSession"/>. Each F-numbered test was written first and seen to FAIL against the
/// original code, proving the defect, before the fix was applied (see PROMPT_LOG.md, Prompt 8).
/// The "Verified" tests were suspected issues that passed — kept as guards.
/// </summary>
public sealed class ReviewFindingsTests
{
    private static readonly KiboApiSettings StubSettings = new() { BaseUrl = new Uri("http://stub.local") };

    // ── F1 · Poller · the "hard deadline" is only as hard as the action is cooperative ──────────
    // The timeout is enforced by cancelling the token passed to the action. An action that ignores
    // the token, and EVERY call through the Func<Task<T>> overload (which never sees the token),
    // keeps the poller waiting past its timeout — e.g. a hung GET waits for HttpClient's 30s timeout
    // instead of the poller's 15s.

    [Fact]
    public async Task F1_Poller_SimpleOverload_HonorsTimeout_WhenAttemptHangs()
    {
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<PollingTimeoutException>(() => Poller.WaitUntilAsync(
            () => HangAsync(TimeSpan.FromSeconds(3)),   // no token available in this overload
            _ => true,
            timeout: TimeSpan.FromMilliseconds(200)));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1.5), $"Poller overran its 200ms timeout: {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task F1_Poller_TokenOverload_HonorsTimeout_WhenActionIgnoresToken()
    {
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<PollingTimeoutException>(() => Poller.WaitUntilAsync(
            _ => HangAsync(TimeSpan.FromSeconds(3)),    // receives the token but ignores it
            _ => true,
            timeout: TimeSpan.FromMilliseconds(200)));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1.5), $"Poller overran its 200ms timeout: {stopwatch.Elapsed}.");
    }

    // ── F2 · DiagnosticsHandler · response leaked when buffering the body fails ─────────────────
    // If LoadIntoBufferAsync throws (connection reset mid-body, or the Poller's deadline cancelling a
    // slow body read), the HttpResponseMessage is never returned to anyone and never disposed, so its
    // pooled connection is held until the GC finalizes it.

    [Fact]
    public async Task F2_DiagnosticsHandler_DisposesResponse_WhenBodyReadFails()
    {
        var content = new FailingBodyContent();
        using var httpClient = KiboApiSession.CreateHttpClient(StubSettings, new CannedTransport(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

        await Assert.ThrowsAnyAsync<Exception>(() => httpClient.GetAsync("v1/orders"));

        Assert.True(content.Disposed, "The response whose body failed to buffer was leaked (never disposed).");
    }

    // ── F3 · DiagnosticsHandler · observability can change the outcome of a request ─────────────
    // Diagnostics must be side-effect free, but:
    //  (a) a throwing log sink — e.g. xUnit's ITestOutputHelper after its test has finished, which a
    //      late/abandoned call can hit — turns a successful request into an exception (and leaks the
    //      response, which was already produced);
    //  (b) the same throw from `finally` REPLACES the real transport error, hiding the root cause;
    //  (c) capturing a body with an unknown charset throws, failing a request the server answered fine.

    [Fact]
    public async Task F3a_ThrowingLogSink_DoesNotFailSuccessfulRequest()
    {
        var client = CreateOrdersClient(new CannedTransport(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))
            .WithLogOutput(_ => throw new InvalidOperationException("There is no currently active test."))
            .WithLogging();

        var response = await client.GetOrderAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task F3b_ThrowingLogSink_DoesNotMaskTransportError()
    {
        var client = CreateOrdersClient(new CannedTransport(_ => throw new HttpRequestException("Connection refused")))
            .WithLogOutput(_ => throw new InvalidOperationException("There is no currently active test."))
            .WithLogging();

        var ex = await Record.ExceptionAsync(() => client.GetOrderAsync(Guid.NewGuid()));

        Assert.IsType<HttpRequestException>(ex);
        Assert.Contains("Connection refused", ex.Message);
    }

    [Fact]
    public async Task F3c_UnreadableResponseCharset_DoesNotFailRawRequest()
    {
        using var httpClient = KiboApiSession.CreateHttpClient(StubSettings, new CannedTransport(_ =>
        {
            var content = new ByteArrayContent("{}"u8.ToArray());
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json; charset=no-such-charset");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));

        using var response = await httpClient.GetAsync("v1/orders");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ── Verified (suspected, disproven) ─────────────────────────────────────────────────────────

    // Suspicion: capturing the request body consumes a non-seekable stream, so the server gets an empty body.
    // Result: passes — HttpContent buffers on first read and sends from the buffer.
    [Fact]
    public async Task Verified_RequestCapture_DoesNotConsumeNonSeekableStreamBody()
    {
        string? bodySeenByServer = null;
        using var httpClient = KiboApiSession.CreateHttpClient(StubSettings, new CannedTransport(request =>
        {
            bodySeenByServer = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));

        using var response = await httpClient.PostAsync("v1/orders", new StreamContent(new NonSeekableStream("payload"u8.ToArray())));

        Assert.Equal("payload", bodySeenByServer);
    }

    // Suspicion: one shared client/pipeline under concurrency mixes up exchanges or correlation ids.
    // Result: passes — all per-request state lives on HttpRequestMessage.Options and headers.
    [Fact]
    public async Task Verified_ConcurrentRequestsOnSharedClient_KeepTheirOwnExchanges()
    {
        var client = CreateOrdersClient(new CannedTransport(request => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent(request.Headers.GetValues("x-request-id").Single()),
        }));

        var responses = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => client.GetOrderAsync(Guid.NewGuid()))));

        Assert.All(responses, r => Assert.Equal(r.Body, r.Exchange!.RequestId));
        Assert.Equal(200, responses.Select(r => r.Exchange!.RequestId).Distinct().Count());
    }

    private static OrdersClient CreateOrdersClient(HttpMessageHandler transport) =>
        new(KiboApiSession.CreateHttpClient(StubSettings, transport), StubSettings.TenantId);

    private static async Task<int> HangAsync(TimeSpan duration)
    {
        await Task.Delay(duration); // deliberately ignores cancellation
        return 0;
    }

    private sealed class CannedTransport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class FailingBodyContent : HttpContent
    {
        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new IOException("Connection reset while reading the response body.");

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
