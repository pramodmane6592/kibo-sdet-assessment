using System.Net;
using System.Net.Http.Headers;
using Kibo.TestingFramework.Diagnostics;

namespace Kibo.TestingFramework.Http;

/// <summary>
/// The outcome of an API call: status, headers, the raw body, the body deserialized to
/// <typeparamref name="T"/> (for successful responses), timing, and the captured exchange.
/// </summary>
/// <typeparam name="T">Expected type of a successful response body.</typeparam>
public sealed class ApiResponse<T>
{
    internal ApiResponse(HttpStatusCode statusCode, HttpResponseHeaders headers, string body, T? data, TimeSpan elapsed, HttpExchange? exchange)
    {
        StatusCode = statusCode;
        Headers = headers;
        Body = body;
        Data = data;
        Elapsed = elapsed;
        Exchange = exchange;
    }

    /// <summary>HTTP status code returned by the server.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary><c>true</c> for 2xx status codes.</summary>
    public bool IsSuccessStatusCode => (int)StatusCode is >= 200 and <= 299;

    /// <summary>Response headers (e.g. <c>Location</c>).</summary>
    public HttpResponseHeaders Headers { get; }

    /// <summary>Raw response body, always captured — including error payloads.</summary>
    public string Body { get; }

    /// <summary>Deserialized body for 2xx responses; <c>default</c> otherwise.</summary>
    public T? Data { get; }

    /// <summary>Time from sending the request until the full response was received. Always captured.</summary>
    public TimeSpan Elapsed { get; }

    /// <summary><see cref="Elapsed"/> in whole milliseconds.</summary>
    public long ElapsedMs => (long)Elapsed.TotalMilliseconds;

    /// <summary>Full captured exchange; <c>null</c> only if the client was built without <see cref="DiagnosticsHandler"/>.</summary>
    public HttpExchange? Exchange { get; }

    /// <summary>Correlation id the request was sent with (<c>x-correlation-id</c>).</summary>
    public string? CorrelationId => Exchange?.CorrelationId;

    /// <summary>Formatted request: method, URL, headers, body.</summary>
    public string RequestLog => Exchange?.RequestLog ?? "<request not captured: DiagnosticsHandler not installed>";

    /// <summary>Formatted response: status, timing, headers, body.</summary>
    public string ResponseLog => Exchange?.ResponseLog ?? ToString();

    /// <summary>
    /// Returns this response if its status is <paramref name="expected"/>; otherwise throws with the
    /// full exchange. Prefer it over <c>Assert.Equal(expected, response.StatusCode)</c>, whose
    /// "Expected: 201, Actual: 500" message lacks the context needed to debug a CI failure.
    /// </summary>
    /// <param name="expected">Required status code.</param>
    /// <exception cref="ApiResponseException">The status differs.</exception>
    public ApiResponse<T> EnsureStatus(HttpStatusCode expected)
    {
        if (StatusCode != expected)
        {
            throw Failure($"Expected {(int)expected} {expected} but got {(int)StatusCode} {StatusCode}.");
        }

        return this;
    }

    /// <summary>
    /// Returns <see cref="Data"/>, or throws with the full exchange when there is none —
    /// so a test fails with the server's actual answer instead of a <see cref="NullReferenceException"/>.
    /// </summary>
    /// <exception cref="ApiResponseException">The response has no deserialized body.</exception>
    public T EnsureData() =>
        Data ?? throw Failure($"Expected a {typeof(T).Name} body but got {(int)StatusCode} {StatusCode}.");

    /// <summary>Full diagnostic dump: correlation id, request and response.</summary>
    public string Describe() =>
        Exchange is null
            ? ToString()
            : $"Correlation id: {CorrelationId}{Environment.NewLine}{Exchange}";

    /// <inheritdoc />
    public override string ToString() =>
        $"{(int)StatusCode} {StatusCode} in {ElapsedMs} ms: {(Body.Length == 0 ? "<empty body>" : Body)}";

    private ApiResponseException Failure(string reason) =>
        new($"{reason}{Environment.NewLine}{Describe()}", StatusCode, Exchange);
}
