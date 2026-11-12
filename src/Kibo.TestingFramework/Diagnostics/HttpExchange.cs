using System.Net;
using System.Text;

namespace Kibo.TestingFramework.Diagnostics;

/// <summary>
/// A captured HTTP request/response pair with timing — everything needed to diagnose a failure
/// without re-running the test. Produced by <see cref="DiagnosticsHandler"/>.
/// </summary>
public sealed class HttpExchange
{
    /// <summary>Key under which the exchange is stored on <see cref="HttpRequestMessage.Options"/>.</summary>
    public static readonly HttpRequestOptionsKey<HttpExchange> OptionKey = new("Kibo.HttpExchange");

    internal HttpExchange(HttpMethod method, Uri? url, DateTimeOffset startedAt)
    {
        Method = method;
        Url = url;
        StartedAt = startedAt;
    }

    /// <summary>HTTP method.</summary>
    public HttpMethod Method { get; }

    /// <summary>Absolute request URL.</summary>
    public Uri? Url { get; }

    /// <summary>When the request was sent (UTC).</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Value of the <c>x-correlation-id</c> header.</summary>
    public string? CorrelationId { get; internal init; }

    /// <summary>Value of the <c>x-request-id</c> header.</summary>
    public string? RequestId { get; internal init; }

    /// <summary>Request headers (sensitive values redacted).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> RequestHeaders { get; internal init; } = [];

    /// <summary>Request body (possibly truncated); <c>null</c> when there was none.</summary>
    public string? RequestBody { get; internal init; }

    /// <summary>Response status code; <c>null</c> if no response was received.</summary>
    public HttpStatusCode? StatusCode { get; private set; }

    /// <summary>Response headers (sensitive values redacted).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders { get; private set; } = [];

    /// <summary>Response body (possibly truncated).</summary>
    public string? ResponseBody { get; private set; }

    /// <summary>Time from sending the request until the full response body was received.</summary>
    public TimeSpan Elapsed { get; private set; }

    /// <summary>Transport error, if the request failed without a response.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Request line, headers and body, formatted for humans.</summary>
    public string RequestLog
    {
        get
        {
            var sb = new StringBuilder().Append(Method).Append(' ').Append(Url).AppendLine();
            AppendHeadersAndBody(sb, RequestHeaders, RequestBody);
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>Status line (with timing), headers and body, formatted for humans.</summary>
    public string ResponseLog
    {
        get
        {
            var sb = new StringBuilder();
            if (StatusCode is { } status)
            {
                sb.Append((int)status).Append(' ').Append(status);
            }
            else
            {
                sb.Append("NO RESPONSE — ").Append(Error?.GetType().Name).Append(": ").Append(Error?.Message);
            }

            sb.Append(" (").Append(Elapsed.TotalMilliseconds.ToString("0")).Append(" ms)").AppendLine();
            AppendHeadersAndBody(sb, ResponseHeaders, ResponseBody);
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>Full exchange: request, then response.</summary>
    public override string ToString() =>
        $"──▶ {RequestLog}{Environment.NewLine}◀── {ResponseLog}";

    internal void Complete(HttpStatusCode statusCode, IReadOnlyList<KeyValuePair<string, string>> headers, string? body, TimeSpan elapsed)
    {
        StatusCode = statusCode;
        ResponseHeaders = headers;
        ResponseBody = body;
        Elapsed = elapsed;
    }

    internal void Fail(Exception error, TimeSpan elapsed)
    {
        Error = error;
        Elapsed = elapsed;
    }

    private static void AppendHeadersAndBody(StringBuilder sb, IReadOnlyList<KeyValuePair<string, string>> headers, string? body)
    {
        foreach (var (name, value) in headers)
        {
            sb.Append("    ").Append(name).Append(": ").Append(value).AppendLine();
        }

        if (!string.IsNullOrEmpty(body))
        {
            sb.Append("    ").Append(body).AppendLine();
        }
    }
}
