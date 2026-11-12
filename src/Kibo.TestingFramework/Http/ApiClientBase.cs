using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kibo.TestingFramework.Diagnostics;

namespace Kibo.TestingFramework.Http;

/// <summary>
/// Base class for typed API clients. Handles request sending, JSON (de)serialization and
/// response wrapping so that concrete clients only describe endpoints.
/// </summary>
/// <remarks>
/// The <see cref="System.Net.Http.HttpClient"/> is injected and never disposed here — its lifetime
/// belongs to the owner (see <see cref="KiboApiSession"/>), so many lightweight clients can share
/// one connection pool. Per-request headers are added in <see cref="ApplyDefaultHeaders"/> rather
/// than on <c>HttpClient.DefaultRequestHeaders</c>, which keeps the shared client immutable and
/// thread-safe.
/// </remarks>
public abstract class ApiClientBase
{
    /// <summary>
    /// JSON options matching ASP.NET Core defaults (camelCase, case-insensitive),
    /// omitting <c>null</c> properties so tests can send genuinely missing fields.
    /// </summary>
    public static readonly JsonSerializerOptions DefaultJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Creates a client over a shared, externally owned <see cref="System.Net.Http.HttpClient"/>.</summary>
    /// <param name="httpClient">Client with <c>BaseAddress</c> already configured.</param>
    /// <param name="jsonOptions">Serializer options; defaults to <see cref="DefaultJsonOptions"/>.</param>
    /// <param name="diagnostics">Correlation/logging settings attached to every request; defaults to <see cref="RequestDiagnostics.None"/>.</param>
    protected ApiClientBase(HttpClient httpClient, JsonSerializerOptions? jsonOptions = null, RequestDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        HttpClient = httpClient;
        JsonOptions = jsonOptions ?? DefaultJsonOptions;
        Diagnostics = diagnostics ?? RequestDiagnostics.None;
    }

    /// <summary>Correlation/logging settings attached to every request from this client.</summary>
    public RequestDiagnostics Diagnostics { get; }

    /// <summary>The shared transport.</summary>
    protected HttpClient HttpClient { get; }

    /// <summary>Serializer options used for request and response bodies.</summary>
    protected JsonSerializerOptions JsonOptions { get; }

    /// <summary>Hook for headers every request from this client must carry (auth, tenant, ...).</summary>
    /// <param name="request">The outgoing request.</param>
    protected virtual void ApplyDefaultHeaders(HttpRequestMessage request)
    {
    }

    /// <summary>Sends a <c>GET</c> request.</summary>
    /// <param name="path">Path relative to the base URL (no leading slash).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected Task<ApiResponse<T>> GetAsync<T>(string path, CancellationToken cancellationToken = default) =>
        SendAsync<T>(new HttpRequestMessage(HttpMethod.Get, path), cancellationToken);

    /// <summary>Sends a <c>POST</c> request with <paramref name="body"/> serialized as JSON.</summary>
    /// <param name="path">Path relative to the base URL (no leading slash).</param>
    /// <param name="body">Object to serialize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected Task<ApiResponse<T>> PostAsync<T>(string path, object body, CancellationToken cancellationToken = default) =>
        SendAsync<T>(CreateJsonRequest(HttpMethod.Post, path, body), cancellationToken);

    /// <summary>Creates a request with <paramref name="body"/> serialized as JSON, for callers that need to add headers before sending.</summary>
    /// <param name="method">HTTP method.</param>
    /// <param name="path">Path relative to the base URL (no leading slash).</param>
    /// <param name="body">Object to serialize.</param>
    protected HttpRequestMessage CreateJsonRequest(HttpMethod method, string path, object body) =>
        CreateRequest(method, path, JsonSerializer.Serialize(body, JsonOptions));

    /// <summary>
    /// Sends a <c>POST</c> request with a pre-built JSON string — for negative tests that need
    /// payloads the typed models cannot express (malformed JSON, wrong types, ...).
    /// </summary>
    /// <param name="path">Path relative to the base URL (no leading slash).</param>
    /// <param name="json">Request body, sent verbatim as <c>application/json</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected Task<ApiResponse<T>> PostRawAsync<T>(string path, string json, CancellationToken cancellationToken = default) =>
        SendAsync<T>(CreateRequest(HttpMethod.Post, path, json), cancellationToken);

    /// <summary>Bodies at or above this size are sent with <c>Expect: 100-continue</c>.</summary>
    public const int ExpectContinueThresholdBytes = 1024 * 1024;

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string json)
    {
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(method, path) { Content = content };

        // For large bodies, ask the server to accept the headers before uploading (as curl does).
        // Without this, a server that rejects early (e.g. Kestrel's 413 for > 30 MB) closes the
        // connection mid-upload and the client sees a connection reset instead of the real status.
        if (Encoding.UTF8.GetByteCount(json) >= ExpectContinueThresholdBytes)
        {
            request.Headers.ExpectContinue = true;
        }

        return request;
    }

    /// <summary>Sends <paramref name="request"/> and wraps the result in an <see cref="ApiResponse{T}"/>.</summary>
    /// <param name="request">Request to send; disposed by this method.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected async Task<ApiResponse<T>> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using (request)
        {
            ApplyDefaultHeaders(request);
            request.Options.Set(RequestDiagnostics.OptionKey, Diagnostics);

            // Fallback timing for clients built without DiagnosticsHandler; the handler's figure is
            // preferred because it excludes this method's own overhead.
            var stopwatch = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                request.Options.TryGetValue(HttpExchange.OptionKey, out var failed);
                throw new HttpRequestException(
                    $"{request.Method} {request.RequestUri} failed after {stopwatch.ElapsedMilliseconds} ms "
                    + $"(correlation id {failed?.CorrelationId ?? "n/a"}). Is the API running at {HttpClient.BaseAddress}? {ex.Message}",
                    ex,
                    ex.StatusCode);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();

                request.Options.TryGetValue(HttpExchange.OptionKey, out var exchange);

                var data = response.IsSuccessStatusCode && body.Length > 0
                    ? Deserialize<T>(body, request, exchange)
                    : default;

                return new ApiResponse<T>(response.StatusCode, response.Headers, body, data, exchange?.Elapsed ?? stopwatch.Elapsed, exchange);
            }
        }
    }

    private T? Deserialize<T>(string body, HttpRequestMessage request, HttpExchange? exchange)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"{request.Method} {request.RequestUri} returned a body that is not a valid {typeof(T).Name}."
                + Environment.NewLine + (exchange?.ToString() ?? body),
                ex);
        }
    }
}
