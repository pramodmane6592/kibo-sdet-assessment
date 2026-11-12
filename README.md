# Kibo SDET Challenge: Submission

This repository contains my solution to the Kibo SDET take-home assessment. All six tasks in [CANDIDATE_INSTRUCTIONS.md](CANDIDATE_INSTRUCTIONS.md) are done. `Kibo.MockApi` has not been changed.

| | |
|---|---|
| **Framework** | [`src/Kibo.TestingFramework`](src/Kibo.TestingFramework): a reusable SDK for the Orders API, with its own [README](src/Kibo.TestingFramework/README.md) |
| **Tests** | [`tests/Kibo.LegacyTests`](tests/Kibo.LegacyTests): 91 tests (65 methods), all passing; stable over 5 consecutive full runs |
| **Build** | 0 warnings, 0 errors; XML docs on all public framework types |
| **AI usage** | [PROMPT_LOG.md](PROMPT_LOG.md) |
| **Bugs found** | 5 API defects (BUG-01 to BUG-05), documented in [OrderEdgeCaseTests.cs](tests/Kibo.LegacyTests/OrderEdgeCaseTests.cs) |

---

## How to run

You need the .NET 10 SDK. [global.json](global.json) pins 10.0.101 and allows newer feature bands.

```bash
# Terminal 1: start the mock API on http://localhost:5000
dotnet run --project src/Kibo.MockApi

# Terminal 2: build and run all tests
dotnet build Kibo.SDET.Challenge.sln
dotnet test
```

### Useful filters

```bash
dotnet test --filter "Category=KnownBug"                         # only the tests that pin API defects
dotnet test --filter "Category=EdgeCase"                         # all Task 4 edge-case tests
dotnet test --filter "FullyQualifiedName~DiagnosticsTests"       # offline tests: no API needed
dotnet test --logger "console;verbosity=detailed"                # show per-test output (logs, timings)
```

### Configuration (all optional)

| Variable | Default | Purpose |
|---|---|---|
| `KIBO_API_BASE_URL` | `http://localhost:5000/` | API root, so the same suite can run against another environment in CI |
| `KIBO_TENANT_ID` | `tenant-abc-123` | Default `x-kibo-tenant` header value |
| `KIBO_API_TIMEOUT_SECONDS` | `30` | Per-request HTTP timeout |
| `KIBO_HTTP_LOGGING` | `false` | Log every request and response in full to the test output |

An invalid value stops the run at startup with a message that names the variable.

```bash
KIBO_HTTP_LOGGING=true dotnet test --logger "console;verbosity=detailed"
```

> If startup reports *"address already in use"* on port 5000, an earlier `Kibo.MockApi` process is still running. Stop it first.

---

## Repository layout

```
├── README.md                    ← this file
├── PROMPT_LOG.md                ← Task 5: AI usage log
├── CANDIDATE_INSTRUCTIONS.md    ← the assignment (unchanged)
├── src/
│   ├── Kibo.MockApi/            ← API under test (unchanged)
│   └── Kibo.TestingFramework/
│       ├── Configuration/       ← KiboApiSettings (env-var driven)
│       ├── Http/                ← ApiClientBase, ApiResponse<T>, ApiResponseException
│       ├── Clients/             ← OrdersClient
│       ├── Models/              ← Order, LineItem, CreateOrderRequest, OrderStatus
│       ├── Builders/            ← OrderBuilder, LineItemBuilder, TestData
│       ├── Polling/             ← Poller, PollingTimeoutException
│       ├── Diagnostics/         ← CorrelationHandler, DiagnosticsHandler, HttpExchange
│       └── KiboApiSession.cs    ← owns the single shared HttpClient
└── tests/Kibo.LegacyTests/
    ├── OrderTests.cs            ← Task 1/3: the refactored legacy tests
    ├── OrderBuilderTests.cs     ← Task 2
    ├── PollerTests.cs           ← Task 3
    ├── OrderEdgeCaseTests.cs    ← Task 4 (+ bug reports)
    ├── KnownBug.cs              ← assertion helper for documented defects
    ├── ObservabilityTests.cs    ← Task 6 (live API: timing, correlation)
    ├── DiagnosticsTests.cs      ← Task 6 (offline, stub transport)
    ├── ReviewFindingsTests.cs   ← regression tests from a hostile self-review
    ├── KiboApiSettingsTests.cs  ← configuration parsing
    └── KiboApiCollection.cs     ← xUnit collection fixture
```

---

## Task by task

### Task 1: The platform shift

**Anti-patterns in the original `OrderTests.cs`:**

| # | Anti-pattern | Why it matters | Fix |
|---|---|---|---|
| 1 | `new HttpClient()` in every test | Socket exhaustion under load, DNS changes ignored, nothing disposed consistently | One `HttpClient` per run, owned by `KiboApiSession` (xUnit collection fixture), on a `SocketsHttpHandler` with `PooledConnectionLifetime` |
| 2 | `http://localhost:5000` hard-coded everywhere | Can't run against another environment, such as CI or staging | `KiboApiSettings` with defaults that environment variables override |
| 3 | `x-kibo-tenant` header copy-pasted | Duplication; easy to forget or misspell | `OrdersClient` applies it per request; `ForTenant(...)` / `WithoutTenant()` for exceptions |
| 4 | Raw JSON strings built inline | Brittle, hides intent, no compile-time checking | Typed models + `System.Text.Json` (web defaults) in `ApiClientBase` |
| 5 | No shared setup/teardown | Each test re-creates infrastructure; no single place to configure it | `KiboApiSession` fixture created once and disposed once |
| 6 | `Thread.Sleep(6000)` | Slow, flaky, blocks a thread | `Poller` (see Task 3) |
| 7 | Assertions on status code only | `Expected 201, Actual 500` with no context | `EnsureStatus` / `EnsureData` fail with the full request and response (see Task 6) |

**Design decisions:**
- **Headers are set on each request, never on `DefaultRequestHeaders`.** The `HttpClient` is shared, so mutating it would cause races between tests running in parallel.
- **Clients are immutable.** `ForTenant`, `WithoutTenant`, `WithCorrelationId` and `WithLogging` each return a new client, so one test can't leak its settings into another.
- **`ApiResponse<T>`** carries the status, headers, raw body, typed data, elapsed time and the captured exchange. Tests assert on the outcome without parsing anything by hand.
- **The framework doesn't reference xUnit.** Only the test project does, so the SDK could be used with another test runner.

**Result:** [OrderTests.cs](tests/Kibo.LegacyTests/OrderTests.cs) has the same 6 scenarios, each a few lines long:

```csharp
var order = (await _orders.CreateOrderAsync(OrderBuilder.Default().Build()))
    .EnsureStatus(HttpStatusCode.Created)
    .EnsureData();
```

### Task 2: Fluent `OrderBuilder`

[OrderBuilder.cs](src/Kibo.TestingFramework/Builders/OrderBuilder.cs), with [LineItemBuilder.cs](src/Kibo.TestingFramework/Builders/LineItemBuilder.cs) and [TestData.cs](src/Kibo.TestingFramework/Builders/TestData.cs).

```csharp
OrderBuilder.Default().Build();                                     // valid order, zero config
OrderBuilder.Default().WithCustomerEmail("test@kibo.com").WithItems(2).ForTenant("tenant-xyz").Build();
OrderBuilder.Default().WithItem("SKU-001", 2, 29.99m).Build();       // explicit item
OrderBuilder.Default().WithItem(i => i.WithUnitPrice(-1m)).Build();  // override one field, rest random
OrderBuilder.Default().WithNoItems().Build();                       // "lineItems": []
OrderBuilder.Default().WithoutLineItems().Build();                  // field omitted entirely
OrderBuilder.Default().WithSeed(42).Build();                        // reproducible data
```

**Design decisions:**
- **Seeded randomization.** Each builder has a seed. If a test fails on random data, the seed lets you reproduce exactly the same data.
- **Values are randomized at `Build()` time**, so an explicit override is never overwritten and calls can be chained in any order.
- **The builder doesn't validate.** It can deliberately build invalid orders (negative price, no items), which the Task 4 negative tests need.
- **`WithNoItems()` and `WithoutLineItems()` are separate** because `[]` and a missing field are different payloads, and the API treats them differently.
- **Random data is obviously fake** (`@kibo-test.example`, `SKU-#####`), so it can't be mistaken for real data in logs.

Tests: [OrderBuilderTests.cs](tests/Kibo.LegacyTests/OrderBuilderTests.cs) (9).

### Task 3: Resiliency and polling

[Poller.cs](src/Kibo.TestingFramework/Polling/Poller.cs) is a generic poller, plus `OrdersClient.WaitForOrderStatusAsync` as a convenience method for orders.

```csharp
var ready = await Poller.WaitUntilAsync(
    action:    ct => _orders.GetOrderAsync(orderId, ct),
    condition: r => r.Data?.Status == OrderStatus.ReadyForFulfillment,
    interval:  TimeSpan.FromMilliseconds(500),   // default
    timeout:   TimeSpan.FromSeconds(15));        // default
```

| Requirement | How it's met |
|---|---|
| Configurable interval/timeout (500 ms / 15 s) | `interval` and `timeout` parameters; non-positive values are rejected |
| Return as soon as the condition holds | The first attempt runs immediately; no sleep after success |
| Clear timeout exception with the last state | `PollingTimeoutException` gives the condition's source text (via `[CallerArgumentExpression]`), the attempt count, the elapsed time and the last observed value (its `ToString()` or JSON) |
| Generic | `Func<Task<T>>` or `Func<CancellationToken, Task<T>>` and `Func<T, bool>` |

**Beyond the requirements:**
- **The timeout is a hard deadline.** A hung attempt is cancelled through its token. If the attempt ignores the token, it is abandoned with `WaitAsync`, so the poller never runs past its timeout.
- **Real errors aren't swallowed.** An exception thrown by the action means something is broken, not "not ready yet", so it propagates immediately.
- **Caller cancellation is reported as `OperationCanceledException`,** not as a timeout.

The status test now finishes in about 5 s instead of the 6 s fixed sleep, and it doesn't fail when the server is slow. Tests: [PollerTests.cs](tests/Kibo.LegacyTests/PollerTests.cs) (8).

### Task 4: AI-driven edge cases

I used an LLM to brainstorm destructive scenarios, then **probed each one against the running API** before writing a test. The verified ones are in [OrderEdgeCaseTests.cs](tests/Kibo.LegacyTests/OrderEdgeCaseTests.cs). Each bug has a full report in a code comment: steps, expected behaviour, actual behaviour and root cause.

| ID | Severity | Finding | Expected | Actual |
|---|---|---|---|---|
| **BUG-01** | High (security) | `GET` ignores the tenant: any caller can read any tenant's order, including the customer's email (PII) | 404 for another tenant, 401 with no header | 200 with the full order |
| **BUG-02** | Medium (security) | The tenant header value isn't validated (SQL/XSS/path-traversal strings, 20 KB values) | 400 | 201, stored verbatim |
| **BUG-03** | High (data integrity) | Line items aren't validated: negative price, zero or negative quantity, `int.MinValue`, empty SKU | 400 | 201 |
| **BUG-04** | Medium | Orders with `[]`, with `lineItems` missing, or with `[null]` are accepted | 400 | 201 (`[null]` is persisted) |
| **BUG-05** | Medium | `customerEmail` isn't validated: missing, empty, malformed, or 1 MB | 400 | 201 |

**Edge cases the API handles correctly**, which are asserted directly: a whitespace-only tenant returns 401; a quantity above `int.MaxValue` returns 400 naming the field; an explicit `"lineItems": null` returns 400; malformed JSON returns 400; a body over 30 MB returns 413; Unicode and emoji round-trip intact; client-supplied `id` and `status` are ignored.

**How known bugs are tested ([KnownBug.cs](tests/Kibo.LegacyTests/KnownBug.cs)):** a test for a defect pins today's buggy status, so the suite stays green and the bug stays documented. As soon as the API returns the correct status, the test fails with *"BUG-0X appears FIXED, update this test"*. Any other status fails with the full exchange. This is better than `Skip`, because the test keeps running and notices when the bug is fixed. Filter: `Category=KnownBug`.

### Task 5: AI prompt log

See [PROMPT_LOG.md](PROMPT_LOG.md). It records the prompts across all tasks, what I kept, changed and rejected, and why. It includes an adversarial review I ran against the AI-generated framework code (see below).

### Task 6: Observability and diagnostics

The handler pipeline, outermost first: `CorrelationHandler` → `DiagnosticsHandler` → `SocketsHttpHandler`.

| Requirement | Implementation |
|---|---|
| Request/response logging | `DiagnosticsHandler` captures the method, URL, headers and body of both the request and the response in an `HttpExchange`, exposed as `response.RequestLog` / `response.ResponseLog` |
| Off by default, easy to enable | `KIBO_HTTP_LOGGING=true` for the run, or `client.WithLogging()` for one client. Logs go to that test's `ITestOutputHelper` |
| Timing always on | `response.Elapsed` / `ElapsedMs`, measured over the full body |
| Correlation | One `x-correlation-id` per test (shared by all its calls), plus a unique `x-request-id` per call |
| Timing assertion | `CreateAndGetOrder_RespondWithinPerformanceBudget`: POST < 500 ms, GET < 300 ms, after a warm-up call |

**Self-diagnosing failures.** A failed `EnsureStatus` prints something like this:

```
Expected 201 Created but got 401 Unauthorized.
Correlation id: 3f2a…
POST http://localhost:5000/v1/orders
  x-correlation-id: 3f2a…, x-request-id: 9c1d…, …
  { "customerEmail": "…", "lineItems": [ … ] }
401 Unauthorized (4 ms)
  …response headers and body…
```
*(Abridged.)*

A connection failure says which URL failed, gives the correlation ID, and asks *"Is the API running at …?"*

**Safety:** `Authorization`, `Cookie`, `x-api-key` and similar headers are redacted. Captured bodies are capped at 16 KB, and bodies over 1 MB aren't read. A log sink that throws can't change a request's outcome.

Tests: [ObservabilityTests.cs](tests/Kibo.LegacyTests/ObservabilityTests.cs) (5, live API) and [DiagnosticsTests.cs](tests/Kibo.LegacyTests/DiagnosticsTests.cs) (7, offline with a stub transport).

---

## Hardening: adversarial self-review

After the tasks were finished, I had the AI act as a hostile reviewer of `Poller`, `DiagnosticsHandler` and `KiboApiSession`. The rule was **prove each finding with a failing test, or drop it.** Six findings were confirmed by failing tests and fixed. The tests are kept as regression tests in [ReviewFindingsTests.cs](tests/Kibo.LegacyTests/ReviewFindingsTests.cs):

| ID | Finding | Fix |
|---|---|---|
| F1 (×2) | The poller could overrun its timeout when an attempt hung or ignored its token | Deadline enforced with `WaitAsync`; abandoned attempts are observed in the background |
| F2 | A response leaked its connection when reading the body failed | The response is disposed on the failure path |
| F3a/b | A throwing log sink failed a successful request, or hid the real transport error | The log write is isolated in try/catch |
| F3c | An unknown response charset failed the request | Body capture is best-effort |

Two suspected issues turned out not to be real: non-seekable request bodies, and concurrent requests on the shared client. They're kept as `Verified_*` tests to prove the code is correct.

A separate flakiness fix: the >30 MB test was sometimes reset by Kestrel instead of returning 413. The framework now sends `Expect: 100-continue` for bodies of 1 MB or more, so the server can reject the request before the body is uploaded.

---

## Known limitations and next steps

- **Data isn't cleaned up.** The API has no `DELETE`, and its store is in memory and resets on restart. Tests use unique IDs and tenants, so they don't interfere with each other.
- **Timing budgets are tuned for a local run.** In CI they should come from configuration, or the test should be tagged and run separately.
- **Possible additions:** JSON schema validation of responses, retry with exponential backoff for transient 5xx errors, and test categories wired into a CI pipeline (unit/offline vs. integration).
