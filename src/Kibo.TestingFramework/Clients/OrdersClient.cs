using System.Net;
using System.Text.Json;
using Kibo.TestingFramework.Diagnostics;
using Kibo.TestingFramework.Http;
using Kibo.TestingFramework.Models;
using Kibo.TestingFramework.Polling;

namespace Kibo.TestingFramework.Clients;

/// <summary>
/// Typed client for the Kibo Orders API (<c>/v1/orders</c>).
/// Instances are immutable: <see cref="ForTenant"/> and <see cref="WithoutTenant"/> return new
/// clients that share the same underlying connection pool.
/// </summary>
public sealed class OrdersClient : ApiClientBase
{
    /// <summary>Header carrying the tenant identifier; required by <c>POST /v1/orders</c>.</summary>
    public const string TenantHeader = "x-kibo-tenant";

    private const string OrdersPath = "v1/orders";

    /// <summary>Creates a client over a shared <see cref="System.Net.Http.HttpClient"/>.</summary>
    /// <param name="httpClient">Client with <c>BaseAddress</c> configured.</param>
    /// <param name="tenantId">Tenant sent with each request; <c>null</c> or empty sends no tenant header.</param>
    /// <param name="jsonOptions">Optional serializer options.</param>
    /// <param name="diagnostics">Optional correlation/logging settings.</param>
    public OrdersClient(
        HttpClient httpClient,
        string? tenantId,
        JsonSerializerOptions? jsonOptions = null,
        RequestDiagnostics? diagnostics = null)
        : base(httpClient, jsonOptions, diagnostics)
    {
        TenantId = tenantId;
    }

    /// <summary>Tenant sent in <see cref="TenantHeader"/>, or <c>null</c> when the header is omitted.</summary>
    public string? TenantId { get; }

    /// <summary>Returns a client that sends <paramref name="tenantId"/> verbatim (no validation, so edge cases can be tested).</summary>
    /// <param name="tenantId">Tenant identifier.</param>
    public OrdersClient ForTenant(string? tenantId) => new(HttpClient, tenantId, JsonOptions, Diagnostics);

    /// <summary>Returns a client that sends no tenant header (unauthenticated).</summary>
    public OrdersClient WithoutTenant() => ForTenant(null);

    /// <summary>Returns a client whose requests all carry <paramref name="correlationId"/> in <c>x-correlation-id</c>.</summary>
    /// <param name="correlationId">Identifier shared by every call of a test.</param>
    public OrdersClient WithCorrelationId(string correlationId) =>
        WithDiagnostics(Diagnostics with { CorrelationId = correlationId });

    /// <summary>Returns a client that writes request/response logs (when enabled) to <paramref name="log"/>.</summary>
    /// <param name="log">Log destination, e.g. xUnit's <c>ITestOutputHelper.WriteLine</c>.</param>
    public OrdersClient WithLogOutput(Action<string> log) =>
        WithDiagnostics(Diagnostics with { Log = log });

    /// <summary>Returns a client with request/response logging forced on (or off), overriding <c>KIBO_HTTP_LOGGING</c>.</summary>
    /// <param name="enabled">Whether to log.</param>
    public OrdersClient WithLogging(bool enabled = true) =>
        WithDiagnostics(Diagnostics with { LoggingEnabled = enabled });

    private OrdersClient WithDiagnostics(RequestDiagnostics diagnostics) =>
        new(HttpClient, TenantId, JsonOptions, diagnostics);

    /// <summary><c>POST /v1/orders</c> — creates an order.</summary>
    /// <param name="order">
    /// Order payload. If <see cref="CreateOrderRequest.TenantId"/> is set it overrides this client's tenant.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ApiResponse<Order>> CreateOrderAsync(CreateOrderRequest order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        var request = CreateJsonRequest(HttpMethod.Post, OrdersPath, order);
        if (order.TenantId is not null)
        {
            request.Headers.TryAddWithoutValidation(TenantHeader, order.TenantId);
        }

        return SendAsync<Order>(request, cancellationToken);
    }

    /// <summary><c>POST /v1/orders</c> with a raw JSON body — for malformed or schema-violating payloads.</summary>
    /// <param name="json">Request body sent verbatim.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ApiResponse<Order>> CreateOrderRawAsync(string json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        return PostRawAsync<Order>(OrdersPath, json, cancellationToken);
    }

    /// <summary><c>GET /v1/orders/{id}</c> — fetches the current state of an order.</summary>
    /// <param name="orderId">Order identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<ApiResponse<Order>> GetOrderAsync(Guid orderId, CancellationToken cancellationToken = default) =>
        GetAsync<Order>($"{OrdersPath}/{orderId}", cancellationToken);

    /// <summary>
    /// Polls <c>GET /v1/orders/{id}</c> until the order reaches <paramref name="expectedStatus"/>.
    /// </summary>
    /// <param name="orderId">Order identifier.</param>
    /// <param name="expectedStatus">Status to wait for; see <see cref="OrderStatus"/>.</param>
    /// <param name="timeout">Overall deadline; defaults to <see cref="Poller.DefaultTimeout"/>.</param>
    /// <param name="interval">Delay between polls; defaults to <see cref="Poller.DefaultInterval"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The order as soon as it has the expected status.</returns>
    /// <exception cref="PollingTimeoutException">The status was not reached in time; the message includes the last response.</exception>
    public async Task<Order> WaitForOrderStatusAsync(
        Guid orderId,
        string expectedStatus,
        TimeSpan? timeout = null,
        TimeSpan? interval = null,
        CancellationToken cancellationToken = default)
    {
        var response = await Poller.WaitUntilAsync(
            token => GetOrderAsync(orderId, token),
            r => r.StatusCode == HttpStatusCode.OK && r.Data?.Status == expectedStatus,
            interval,
            timeout,
            cancellationToken,
            conditionDescription: $"order {orderId} to have status '{expectedStatus}'").ConfigureAwait(false);

        return response.EnsureData();
    }

    /// <inheritdoc />
    protected override void ApplyDefaultHeaders(HttpRequestMessage request)
    {
        // A tenant already on the request (per-order override) wins over the client default.
        if (!string.IsNullOrEmpty(TenantId) && !request.Headers.Contains(TenantHeader))
        {
            // Without validation so tests can deliberately send unusual header values.
            request.Headers.TryAddWithoutValidation(TenantHeader, TenantId);
        }
    }
}
