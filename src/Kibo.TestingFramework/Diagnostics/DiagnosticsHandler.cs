using System.Diagnostics;
using System.Net.Http.Headers;

namespace Kibo.TestingFramework.Diagnostics;

/// <summary>
/// HTTP middleware that times every call and captures it as an <see cref="HttpExchange"/>
/// (stored on <see cref="HttpRequestMessage.Options"/> under <see cref="HttpExchange.OptionKey"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Timing and capture are always on — they are cheap and feed failure messages.</item>
/// <item>Writing to the log is off by default; enable it per handler (<c>KIBO_HTTP_LOGGING=true</c>)
/// or per client (<see cref="RequestDiagnostics.LoggingEnabled"/>).</item>
/// <item>Timing covers the full response body, not just headers, because the body is buffered here.</item>
/// <item>Credentials-like headers are redacted; very large bodies are not copied.</item>
/// </list>
/// Place it inside <see cref="CorrelationHandler"/> so captured headers include the correlation ids.
/// </remarks>
public sealed class DiagnosticsHandler : DelegatingHandler
{
    /// <summary>Bodies longer than this are truncated in the capture.</summary>
    public const int MaxCapturedBodyChars = 16 * 1024;

    /// <summary>Bodies whose declared length exceeds this are not read into the capture at all.</summary>
    public const long MaxReadableBodyBytes = 1024 * 1024;

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "x-api-key",
    };

    private readonly bool _loggingEnabled;
    private readonly Action<string> _log;

    /// <summary>Creates the handler.</summary>
    /// <param name="loggingEnabled">Default logging state for clients that do not override it.</param>
    /// <param name="log">Default log destination; <see cref="Console.WriteLine(string)"/> when <c>null</c>.</param>
    public DiagnosticsHandler(bool loggingEnabled = false, Action<string>? log = null)
    {
        _loggingEnabled = loggingEnabled;
        _log = log ?? Console.WriteLine;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Options.TryGetValue(RequestDiagnostics.OptionKey, out var diagnostics);

        var exchange = new HttpExchange(request.Method, request.RequestUri, DateTimeOffset.UtcNow)
        {
            CorrelationId = FirstValue(request.Headers, CorrelationHandler.CorrelationIdHeader),
            RequestId = FirstValue(request.Headers, CorrelationHandler.RequestIdHeader),
            RequestHeaders = Capture(request.Headers, request.Content?.Headers),
            RequestBody = await ReadBodyAsync(request.Content, cancellationToken).ConfigureAwait(false),
        };
        request.Options.Set(HttpExchange.OptionKey, exchange);

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            exchange.Complete(
                response.StatusCode,
                Capture(response.Headers, response.Content.Headers),
                await ReadBodyAsync(response.Content, cancellationToken).ConfigureAwait(false),
                stopwatch.Elapsed);

            return response;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            exchange.Fail(ex, stopwatch.Elapsed);

            // The response never reaches the caller, so nobody else can release its connection.
            response?.Dispose();
            throw;
        }
        finally
        {
            if (diagnostics?.LoggingEnabled ?? _loggingEnabled)
            {
                WriteLog(diagnostics?.Log ?? _log, exchange);
            }
        }
    }

    // Diagnostics must never change the outcome of a request: a failing sink (e.g. xUnit's
    // ITestOutputHelper after its test has finished) must neither fail a successful call nor
    // replace the real exception that is propagating out of the finally block.
    private static void WriteLog(Action<string> log, HttpExchange exchange)
    {
        try
        {
            log(exchange.ToString());
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Kibo DiagnosticsHandler: log sink threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<string?> ReadBodyAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        if (content is null)
        {
            return null;
        }

        if (content.Headers.ContentLength > MaxReadableBodyBytes)
        {
            return $"<{content.Headers.ContentLength:N0} bytes — not captured>";
        }

        string body;
        try
        {
            body = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // e.g. an unknown charset: capture is best-effort and must not fail the request.
            return $"<body not captured: {ex.Message}>";
        }

        return body.Length <= MaxCapturedBodyChars
            ? body
            : $"{body[..MaxCapturedBodyChars]}… <{body.Length - MaxCapturedBodyChars:N0} more chars truncated>";
    }

    private static List<KeyValuePair<string, string>> Capture(HttpHeaders headers, HttpHeaders? contentHeaders)
    {
        var captured = new List<KeyValuePair<string, string>>();
        foreach (var source in contentHeaders is null ? [headers] : new[] { headers, contentHeaders })
        {
            foreach (var (name, values) in source.NonValidated)
            {
                captured.Add(new(name, SensitiveHeaders.Contains(name) ? "***" : values.ToString()));
            }
        }

        return captured;
    }

    private static string? FirstValue(HttpRequestHeaders headers, string name) =>
        headers.NonValidated.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
