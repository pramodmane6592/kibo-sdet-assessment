using System.Net;
using System.Text;
using Kibo.TestingFramework;
using Kibo.TestingFramework.Builders;
using Kibo.TestingFramework.Clients;
using Kibo.TestingFramework.Configuration;
using Kibo.TestingFramework.Diagnostics;

namespace Kibo.LegacyTests;

/// <summary>
/// Offline tests for the correlation and diagnostics handlers, using a stubbed transport
/// (no running API required).
/// </summary>
public sealed class DiagnosticsTests
{
    [Fact]
    public async Task EveryRequest_GetsCorrelationAndRequestIds()
    {
        var transport = new StubTransport();
        var client = CreateClient(transport);

        await client.GetOrderAsync(Guid.NewGuid());
        await client.GetOrderAsync(Guid.NewGuid());

        var (first, second) = (transport.Requests[0], transport.Requests[1]);
        Assert.NotEqual(Header(first, CorrelationHandler.CorrelationIdHeader), Header(second, CorrelationHandler.CorrelationIdHeader));
        Assert.NotEqual(Header(first, CorrelationHandler.RequestIdHeader), Header(second, CorrelationHandler.RequestIdHeader));
    }

    [Fact]
    public async Task ClientCorrelationId_IsSentOnEveryRequest()
    {
        var transport = new StubTransport();
        var client = CreateClient(transport).WithCorrelationId("test-42");

        await client.GetOrderAsync(Guid.NewGuid());
        await client.ForTenant("other").GetOrderAsync(Guid.NewGuid()); // derived clients keep it

        Assert.All(transport.Requests, r => Assert.Equal("test-42", Header(r, CorrelationHandler.CorrelationIdHeader)));
    }

    [Fact]
    public async Task Exchange_CapturesRequestResponseAndTiming()
    {
        var transport = new StubTransport(HttpStatusCode.Created, """{"status":"Pending"}""", delay: TimeSpan.FromMilliseconds(50));
        var client = CreateClient(transport);

        var response = await client.CreateOrderAsync(OrderBuilder.Default().WithCustomerEmail("x@y.z").Build());

        var exchange = Assert.IsType<HttpExchange>(response.Exchange);
        Assert.Equal(HttpMethod.Post, exchange.Method);
        Assert.Equal("http://stub.local/v1/orders", exchange.Url!.ToString());
        Assert.Contains("x@y.z", exchange.RequestBody);
        Assert.Equal(HttpStatusCode.Created, exchange.StatusCode);
        Assert.Equal("""{"status":"Pending"}""", exchange.ResponseBody);
        Assert.True(response.Elapsed >= TimeSpan.FromMilliseconds(40), $"Elapsed was {response.Elapsed}");
        Assert.Equal(exchange.Elapsed, response.Elapsed);
    }

    [Fact]
    public async Task SensitiveHeaders_AreRedacted()
    {
        var transport = new StubTransport();
        var httpClient = KiboApiSession.CreateHttpClient(StubSettings(), transport);
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/orders");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret-token");

        using var _ = await httpClient.SendAsync(request);

        request.Options.TryGetValue(HttpExchange.OptionKey, out var exchange);
        Assert.Contains(exchange!.RequestHeaders, h => h.Key == "Authorization" && h.Value == "***");
        Assert.DoesNotContain("secret-token", exchange.ToString());
    }

    [Fact]
    public async Task LargeBodies_AreTruncatedInTheCapture()
    {
        var hugeBody = new string('x', DiagnosticsHandler.MaxCapturedBodyChars + 500);
        var client = CreateClient(new StubTransport(HttpStatusCode.NotFound, hugeBody));

        var response = await client.GetOrderAsync(Guid.NewGuid());

        Assert.Equal(hugeBody, response.Body);                                  // the test still sees everything
        Assert.EndsWith("500 more chars truncated>", response.Exchange!.ResponseBody); // logs stay bounded
    }

    [Fact]
    public async Task Logging_FollowsSettings_AndClientOverrideWins()
    {
        var lines = new List<string>();
        var enabledBySettings = CreateClient(new StubTransport(), StubSettings() with { HttpLoggingEnabled = true })
            .WithLogOutput(lines.Add);

        await enabledBySettings.GetOrderAsync(Guid.NewGuid());
        Assert.Single(lines);

        await enabledBySettings.WithLogging(false).GetOrderAsync(Guid.NewGuid());
        Assert.Single(lines);
    }

    [Fact]
    public async Task TransportFailure_IsReportedWithUrlCorrelationIdAndHint()
    {
        var lines = new List<string>();
        var client = CreateClient(new StubTransport(failWith: new HttpRequestException("Connection refused")))
            .WithCorrelationId("corr-77")
            .WithLogOutput(lines.Add)
            .WithLogging();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetOrderAsync(Guid.NewGuid()));

        Assert.Contains("http://stub.local/v1/orders/", ex.Message);
        Assert.Contains("corr-77", ex.Message);
        Assert.Contains("Is the API running", ex.Message);
        Assert.Contains("NO RESPONSE", Assert.Single(lines));
    }

    private static KiboApiSettings StubSettings() => new() { BaseUrl = new Uri("http://stub.local") };

    private static OrdersClient CreateClient(StubTransport transport, KiboApiSettings? settings = null)
    {
        settings ??= StubSettings();
        return new OrdersClient(KiboApiSession.CreateHttpClient(settings, transport), settings.TenantId);
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.Single() : null;

    /// <summary>Innermost handler that records requests and returns a canned response (or throws).</summary>
    private sealed class StubTransport(
        HttpStatusCode status = HttpStatusCode.NotFound,
        string body = "",
        TimeSpan? delay = null,
        Exception? failWith = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);

            if (delay is { } d)
            {
                await Task.Delay(d, cancellationToken);
            }

            if (failWith is not null)
            {
                throw failWith;
            }

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }
    }
}
