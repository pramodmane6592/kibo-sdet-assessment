# Kibo.TestingFramework

A reusable testing SDK for the Kibo Orders API. It provides a shared HTTP client, typed endpoints, fluent test data, polling, and built-in diagnostics. It doesn't depend on any test runner: xUnit is referenced only by the test project.

## Components

| Area | Types | Purpose |
|---|---|---|
| **Configuration** | `KiboApiSettings` | Base URL, tenant, timeout and logging. Defaults suit a local run; environment variables override them. |
| **Session** | `KiboApiSession` | Owns the single shared `HttpClient` and its handler pipeline. Use it as an xUnit collection fixture. |
| **HTTP** | `ApiClientBase`, `ApiResponse<T>`, `ApiResponseException` | Sending, JSON handling, typed responses, and `EnsureStatus` / `EnsureData`, which fail with the full exchange. |
| **Clients** | `OrdersClient` | `CreateOrderAsync`, `CreateOrderRawAsync`, `GetOrderAsync`, `WaitForOrderStatusAsync`. Immutable: `ForTenant`, `WithoutTenant`, `WithCorrelationId`, `WithLogging`, `WithLogOutput`. |
| **Builders** | `OrderBuilder`, `LineItemBuilder`, `TestData` | One-line valid orders with seeded random defaults, and invalid orders on purpose for negative tests. |
| **Polling** | `Poller`, `PollingTimeoutException` | `WaitUntilAsync`: returns as soon as the condition holds, with a hard deadline and the last observed value on timeout. |
| **Diagnostics** | `CorrelationHandler`, `DiagnosticsHandler`, `HttpExchange`, `RequestDiagnostics` | Correlation and request IDs, timing, request/response capture, and logging you can switch on. |

Handler pipeline (outermost first): `CorrelationHandler` → `DiagnosticsHandler` → `SocketsHttpHandler`.

## Configuration

| Variable | Default | Meaning |
|---|---|---|
| `KIBO_API_BASE_URL` | `http://localhost:5000/` | API root |
| `KIBO_TENANT_ID` | `tenant-abc-123` | Default `x-kibo-tenant` value |
| `KIBO_API_TIMEOUT_SECONDS` | `30` | Per-request HTTP timeout |
| `KIBO_HTTP_LOGGING` | `false` | Write the full request/response for every call (`true`/`1`/`yes`/`on`) |

An invalid value fails at startup with a message that names the variable.

## Usage

```csharp
[Collection(KiboApiCollection.Name)]          // shares one KiboApiSession (and HttpClient)
public sealed class MyTests
{
    private readonly OrdersClient _orders;

    public MyTests(KiboApiSession session, ITestOutputHelper output)
    {
        // One correlation id per test; logs go to this test's output when enabled.
        _orders = session.CreateOrdersClient(output.WriteLine);
    }

    [Fact]
    public async Task Order_BecomesReadyForFulfillment()
    {
        var order = (await _orders.CreateOrderAsync(
                OrderBuilder.Default().WithItems(2).ForTenant("tenant-xyz").Build()))
            .EnsureStatus(HttpStatusCode.Created)
            .EnsureData();

        var ready = await _orders.WaitForOrderStatusAsync(order.Id, OrderStatus.ReadyForFulfillment);

        Assert.Equal(order.Id, ready.Id);
    }
}
```

More examples:

```csharp
// Builders
OrderBuilder.Default().Build();                                  // valid order, zero config
OrderBuilder.Default().WithItem("SKU-001", 2, 29.99m).Build();    // explicit item
OrderBuilder.Default().WithItem(i => i.WithUnitPrice(-1m)).Build(); // only the price is set; the rest stays random
OrderBuilder.Default().WithNoItems().Build();                    // "lineItems": []
OrderBuilder.Default().WithoutLineItems().Build();               // field omitted
OrderBuilder.Default().WithSeed(42).Build();                     // reproducible data

// Generic polling
var result = await Poller.WaitUntilAsync(
    action:    ct => _orders.GetOrderAsync(id, ct),
    condition: r => r.Data?.Status == OrderStatus.ReadyForFulfillment,
    interval:  TimeSpan.FromMilliseconds(250),
    timeout:   TimeSpan.FromSeconds(10));

// Diagnostics
Assert.True(response.Elapsed < TimeSpan.FromMilliseconds(500), response.Describe());
Console.WriteLine(response.RequestLog);   // POST http://localhost:5000/v1/orders + headers + body
Console.WriteLine(response.ResponseLog);  // 201 Created (12 ms) + headers + body
_orders.WithLogging().GetOrderAsync(id);  // turn on logging for one client
```

## Design notes

- **One `HttpClient` per run**, with `PooledConnectionLifetime`. This avoids running out of sockets and keeps DNS lookups fresh.
- **Immutable clients.** Tenant, correlation and logging settings travel with each request (on `HttpRequestMessage.Options` and its headers), never on the shared `HttpClient`, so tests can run in parallel.
- **Timing and capture are always on.** Only writing the log is switched off by default. Sensitive headers are hidden, and logged bodies are capped at 16 KB.
- **Large uploads** (1 MB or more) send `Expect: 100-continue`, so a server that rejects them early, such as Kestrel's 413 above 30 MB, produces that status instead of a connection reset.
- **The builders don't validate**, so the same API can produce both valid and invalid payloads.
