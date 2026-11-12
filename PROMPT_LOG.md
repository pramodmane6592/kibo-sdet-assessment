# AI Prompt Log

**Tool used throughout:** Claude Code (Claude Opus 5.5), running as an autonomous coding agent in VS Code. It had access to the repository, the shell (`dotnet`, `curl`, `netstat`) and the running Mock API.


## My approach to working with AI

**1. Let the repository supply the context, not the prompt.**
With a chat assistant you paste code and specs into the prompt. With an agent that can read the repo, re-describing the codebase is redundant and risks drifting from the real source. My first prompt had the agent read the README, the candidate instructions *and the source* to build its own understanding. It found that the README's documented header and status code (`X-Tenant-Id` / `400`) disagree with the code (`x-kibo-tenant` / `401`). Later task requests could stay focused because the repository context had already been established and kept up to date.

**2. Approve the plan before any code is written.**
I asked the agent to explain the task and proposed approach before editing. That put a review point before code existed. I checked the interpretation and design (for example, how `ForTenant()` should work when the tenant is a header, not a body field) before implementation, which is cheaper than reviewing a finished implementation.

**3. Keep each change small and reviewable.**
One task per prompt, and no work on later tasks. Task 1 deliberately left an interim `Task.Delay` with a `TODO(Task 3)` rather than fixing polling early. I also set constraints along the way. I also changed the order of the tasks, doing Task 6 before Task 5 so this log could cover the whole project.

**4. Accept only verified results.**
"It compiles" never counted as done. Each step finished with a build (0 warnings, with XML docs required on the framework), a full test run against the live API, and for anything timing- or network-related, repeated runs. Edge-case assertions were written only *after* the behavior had been reproduced with curl. This loop caught three confident AI mistakes that reading the code would have missed (Prompts 4 and 5). A final adversarial review, in which the AI attacked its own code and every finding had to be proven with a failing test, found four more (Prompt 8).

**5. Observe the real system before guessing.**
When I reported an error from my terminal, I asked the agent to diagnose it rather than speculate. It reproduced the failure, identified the process holding port 5000, and confirmed it was still serving before recommending a fix.

**6. Require the reasoning, not just the code.**
The agent had to state its trade-offs and the alternatives it rejected, so I could review the reasoning behind the code, not only the code. Examples: a poller that swallows exceptions was rejected because it would hide outages; pinned-behavior bug tests were chosen over skipped ones; "SQL injection" was reframed as missing input validation because there is no database. Each entry below records what was generated, what was checked, and what was changed or rejected.

---

## Prompt 1: Understanding the assignment
**Task:** Setup / all
**Prompt summary:** Review the candidate instructions and setup guide, inspect the solution structure and relevant source, then summarize each required task, setup prerequisite, API behavior, and submission requirement. Cross-check documented behavior against the implementation and call out discrepancies before proposing code changes.

**Outcome:** I got a summary of the 6 tasks. More usefully, the AI cross-checked the docs against the source and found three inconsistencies I would otherwise have hit later:
- The README says the tenant header is `X-Tenant-Id` and returns `400`. The code (`OrdersController`) uses `x-kibo-tenant` and returns `401`. **I took the code as the source of truth.**
- Task 2 has no heading in the instructions, so it reads as part of Task 1.
- `global.json` pins .NET 10, but only SDKs 6 and 8 were installed. I installed .NET 10 before writing any code.

It also pointed out early that orders are stored in memory and change status on a timer, which affects test isolation.

**Judgment:** reading the source rather than trusting the docs set the pattern for the rest of the work: check behavior, don't assume it.

---

## Prompt 2: Task 1, the platform shift (base client)
**Task:** 1
**Prompt summary:** Explain Task 1 and inspect the legacy tests before editing. Propose a reusable client design that externalizes configuration, centralizes tenant-header and JSON handling, manages `HttpClient` lifetime safely, and keeps the tests readable. Identify design trade-offs first; make only Task 1 changes and preserve later tasks for separate review.

**Generated:** `KiboApiSettings` (settings from environment variables, which fail clearly on bad values), `ApiClientBase`, `ApiResponse<T>`, `OrdersClient`, `KiboApiSession` (one shared `HttpClient`), an xUnit collection fixture, and the refactored tests.

**Decisions I reviewed and kept:**
- **Tenant header per request.** The tenant is added to each request instead of `HttpClient.DefaultRequestHeaders`. Changing the default headers of a shared client isn't safe when tests run in parallel. `WithoutTenant()` returns a new client, so the 401 test can't affect other tests.
- **Single public constructor.** xUnit requires fixtures to have exactly one public constructor. A `KiboApiSession(settings)` overload would break fixture creation, so explicit settings go through a static `Create(settings)` factory instead.
- **Null fields omitted from JSON** (`JsonIgnoreCondition.WhenWritingNull`). This was chosen to support Task 4's "missing field" tests, not just for tidier output.
- **Stronger assertions.** Checks like `Assert.Contains("Pending", body)` became typed comparisons such as `Assert.Equal(OrderStatus.Pending, order.Status)`.

**Rejected:** fixing `Thread.Sleep` inside Task 1. It became `await Task.Delay(6s)` with a `TODO(Task 3)`, so each task's change stays reviewable on its own.

**Follow-up debugging:** *"I got the error for first command, can you check the error from terminal."* The AI couldn't see my terminal, so it reproduced the problem instead of guessing. `netstat` showed port 5000 was held by a leftover `Kibo.MockApi.exe` from an earlier background run, and a request to it confirmed it was still serving. The fix was to stop that process. No code change was needed.

---

## Prompt 3: Task 2, the fluent `OrderBuilder`
**Task:** 2
**Prompt summary:** Explain Task 2, then design a fluent order builder with valid defaults, custom line items, and reproducible generated data. Check how the example `ForTenant()` fits the API contract, keep edge-case inputs representable for negative tests, and describe the proposed API before implementation.

**Generated:** `OrderBuilder`, `LineItemBuilder`, `TestData` (random value generators) and 9 builder unit tests.

**Design calls:**
- **`ForTenant()` and the header.** The instructions' example puts `ForTenant()` on the builder, but the tenant is an HTTP header, not part of the body. Rather than leave it off the builder, `CreateOrderRequest` got a `[JsonIgnore] TenantId` that `OrdersClient` sends as the header, overriding the client's default tenant.
- **Seeded, reproducible random data.** Random values are generated at `Build()` time from a seed, and the builder exposes `Seed`. A failing test's data can be reproduced exactly with `WithSeed(n)`.
- **Empty list vs missing field.** `WithNoItems()` sends `[]`, while `WithoutLineItems()` leaves the field out. These are different edge cases, and Task 4 later showed the API treats them differently.
- **No validation in the builder.** Negative prices, zero quantities and odd emails are allowed on purpose; that's what the negative tests need.

**Changed:** the first draft of `WithLineItems` copied each item twice for no reason. It was simplified to capture the values once.

---

## Prompt 4: Task 3, the polling utility
**Task:** 3
**Prompt summary:** Explain Task 3 and propose a generic async wait-until helper with configurable interval and timeout, prompt return on success, clear timeout diagnostics including the last observed result, and correct cancellation behavior. Consider what should happen if the polled action throws or ignores cancellation; implement and test this task only.

**Generated:** `Poller.WaitUntilAsync` (generic, 500ms / 15s defaults), `PollingTimeoutException`, `OrdersClient.WaitForOrderStatusAsync`, and 10 poller unit tests.

**Design calls:**
- **Readable timeout messages.** `[CallerArgumentExpression]` puts the condition's source text in the error, so a timeout reads *"waiting for: r => r.Data?.Status == …"* plus the last value seen.
- **Hard deadline.** The polled action gets a cancellation token that fires at the timeout, so a hung HTTP call can't push the wait past 15s.
- **Rejected: swallowing exceptions from the action and retrying.** An unexpected error, such as the API being down, is a real failure, not "not ready yet". Retrying would hide it for 15 seconds.
- **Caller cancellation stays a cancellation.** It isn't reported as a timeout.

**AI mistake caught by running the tests:** the AI wrote a test assuming anonymous types *don't* override `ToString()`, so the poller would fall back to JSON. They do override it, and the test failed on the first run. The poller was fine; the test's assumption was wrong. It was fixed by using a plain class. This is the kind of confident wrong assumption that only running the code reveals.

**Result:** the status test now takes about 5.0s instead of a fixed 6s, and fails with the last response if the status never changes.

---

## Prompt 5: Task 4, AI-driven edge cases
**Task:** 4
**Prompt summary:** For Task 4, propose more than five candidate edge cases, then probe the running API before choosing five tests. Record expected versus actual behavior, distinguish tooling failures from API behavior, and make test assertions reflect verified current behavior while documenting confirmed defects. Avoid claiming SQL injection where this in-memory API has no database; explain design choices before changing the framework.

**Process:** the AI listed candidate attacks (injection in the tenant header, negative or zero values, empty, missing or null collections, very long or malformed emails, Unicode, oversized bodies, overflow, clients setting `id`/`status`). Then, **before writing any assertions, it probed the live API with curl** to record actual behavior.

**Two moments where results were challenged instead of trusted:**
1. **Bogus probe results.** Several probes first returned `400 "A non-empty request body is required"`. Those were a tooling problem, not API behavior: the payloads were generated with `python`, which isn't installed, so empty bodies were sent. The probes were redone with shell tools and the real results were 201 for the 1 MB email, 201 for 20k line items, and 413 above 30 MB.
2. **A flaky test.** The 31 MB → 413 test passed in the full suite, so the AI ran it on its own 5 times. **It failed all 5.** Kestrel replies 413 and closes the connection while the client is still uploading, so the client sees "connection aborted". curl didn't hit this because it sends `Expect: 100-continue` for large bodies. **The fix went into the framework, not the test:** `ApiClientBase` now sends `Expect: 100-continue` for bodies of 1 MB or more. After the fix it passed 8 out of 8 runs.

**Findings:** 5 bug reports, written up in `OrderEdgeCaseTests.cs`.
- **BUG-01 (High):** a GET ignores the tenant, so any tenant, or no tenant, can read another tenant's order and its PII.
- **BUG-02:** the tenant header isn't validated.
- **BUG-03 (High):** negative or zero quantity, negative price and empty SKU are all accepted.
- **BUG-04:** `[]`, a missing field and `[null]` are accepted, and `[null]` *stores a null element*. An explicit `null` returns 400, which is inconsistent.
- **BUG-05:** emails that are missing, empty, malformed or 1 MB long are accepted.

**Judgment calls:**
- **Reframed "SQL injection".** There's no database here, so the test can't detect injection. It's framed as a **missing input validation** bug with a second-order risk: the raw value flows into storage, logs and downstream systems.
- **Known-bug tests.** Bug tests pin the *current* behavior through a `KnownBug.AssertStatus` helper, so `dotnet test` stays green for submission. When a bug is fixed, the test fails with *"BUG-03 appears FIXED — update this test"*. I chose this over `[Fact(Skip=…)]` because skipped tests are never run and so can't notice a fix.
- **Open product question:** whether a price of `0` (a free item) is valid. It's flagged, not asserted.

---

## Prompt 6: Task 6, observability and diagnostics
**Task:** 6
**Prompt summary:** Proceed with Task 6 after agreeing on a design for opt-in request/response diagnostics, always-on elapsed-time capture, and per-test correlation. Keep request-specific settings isolated when sharing `HttpClient`, redact sensitive headers, cap captured body size, and include tests for successful timing, failure diagnostics, and handler behavior. (I chose Task 6 before Task 5 so this log could cover every task.)

**Generated:** `CorrelationHandler`, `DiagnosticsHandler`, `HttpExchange`, `RequestDiagnostics`, `ApiResponseException`, `ApiResponse.EnsureStatus`/`Elapsed`/`RequestLog`/`ResponseLog`, the `KIBO_HTTP_LOGGING` switch, and 18 tests (5 against the live API including the **response-time assertion**, 7 offline handler tests using a fake transport, and 6 config tests).

**Design calls:**
- **Handler order:** correlation runs first, then diagnostics, so captured logs include the tracing headers.
- **Capture always, write the log only when enabled.** Capturing is cheap and gives every failure message its context. `EnsureStatus` now replaces `Assert.Equal(201, response.StatusCode)` across the suite, so a failure includes the full request, response, timing and correlation ID.
- **One correlation ID per test.** xUnit creates a new test class instance per test, so `session.CreateOrdersClient(output.WriteLine)` in the constructor gives exactly one correlation ID per test. **Rejected: an `AsyncLocal` correlation scope.** A value set in the test constructor doesn't reliably carry over into the test method.
- **Settings travel with each request.** Per-client settings go on `HttpRequestMessage.Options` rather than changing the shared `HttpClient`, so one connection pool serves every test with its own settings.
- **Timing covers the full response body.** The diagnostics handler buffers the body before stopping the clock; otherwise `HttpClient` would only time the headers.
- **Safety:** sensitive headers (`Authorization`, `Cookie`, `x-api-key`) are hidden, and logged bodies are capped at 16 KB so the 1 MB and 30 MB edge-case tests don't flood the logs.
- **Warm-up call:** the timing test makes one warm-up call first, so connection setup isn't measured. Without it the test would be flaky on a cold CI agent.

**Changed:** the AI's first draft of the large-body test was convoluted. It was rewritten before running to assert two clear facts: the test still gets the full body, and the logged copy is truncated.

**Verification:** the full suite (83 tests) passed 5 runs in a row, and the build has 0 warnings with XML docs required on the framework's public API.

---

## Prompt 7: Task 5, this log
**Task:** 5
**Prompt summary:** Accepted the agent offer to draft the prompt log and refresh the framework README, using the actual interaction history and test results. Review the draft against what happened; do not invent prompts, outcomes, or verification claims.

**Outcome:** the AI drafted this log from the actual session, covering prompts, outputs, test failures and fixes. I then reviewed it against what happened.

---

## Prompt 8: Adversarial review of the AI's own framework code
**Task:** Hardening (Tasks 3 and 6)
**Prompt (verbatim):** *"Act as a hostile code reviewer. Find race conditions, flaky-test risks and resource leaks in Poller, DiagnosticsHandler and KiboApiSession. Prove each one with a failing test or drop it."*

**Why this prompt:** the code had passed every test, but those tests were written by the same AI that wrote the code, so they only covered failures it had already thought of. I asked the AI to switch roles from author to attacker. The rule "prove it with a failing test or drop it" was there to stop it listing plausible but imaginary issues, which is a common failure in AI code reviews.

**Method:** each suspicion was written as a test and run against the **unchanged** code before any fix (`ReviewFindingsTests.cs`). Fixes were applied only once a test had failed.

| ID | Component | Defect proven by a failing test | Fix |
|---|---|---|---|
| F1 | `Poller` | The timeout only held if the action honored its cancellation token. With the simple `Func<Task<T>>` overload (the exact form shown in the task instructions, which never receives a token) or an action that ignores the token, a 200ms poll ran for 3s. It then **returned success with the late result**. A "ready within 15s" check could silently pass at 30s. | `attempt.WaitAsync(deadline)`. Abandoned attempts are observed in the background so a later failure can't surface as an unobserved task exception. |
| F2 | `DiagnosticsHandler` | When buffering a response body failed (a connection reset, or the poller's deadline cancelling a slow read), the `HttpResponseMessage` was never disposed, so its pooled connection was held until garbage collection. | Dispose the response on the failure path. |
| F3a | `DiagnosticsHandler` | A log sink that throws turned a **successful** request into an exception. xUnit's `ITestOutputHelper` does this when written to after its test has ended. F1's fix made that more likely, since abandoned attempts can finish late. | The log write can't throw; sink errors go to `Trace` instead. |
| F3b | `DiagnosticsHandler` | The same throw, raised from `finally`, **replaced** the real transport error. "Connection refused" became "There is no currently active test", hiding the root cause. | Same fix. |
| F3c | `DiagnosticsHandler` | A response with an unknown `charset` made capturing fail, which failed a request the server had answered successfully. | Body capture is best-effort and records a placeholder on error. |

**Dropped after testing:**
- *"Capturing the request body consumes a non-seekable stream."* It doesn't: `HttpContent` buffers the body on first read and sends from that buffer. Kept as a guard test.
- *"The shared client mixes up exchanges or correlation IDs under concurrency."* 200 parallel requests each kept their own data, because all per-request state lives on `HttpRequestMessage.Options`. Kept as a guard test.
- **`KiboApiSession`: no defect could be proven.** Its lifetime is owned by the xUnit fixture and its pipeline holds no shared mutable state.

**Not provable deterministically, so left out of the code but noted:**
- The 300/500ms timing budgets can fail on an overloaded CI agent. This is mitigated by the warm-up call; a next step would be making budgets configurable per environment.
- Chunked responses under 1 MB are held in memory twice during capture: a memory cost, not a correctness problem.
- The Mock API's in-memory store grows with every test run, because it has no delete endpoint. It can't be fixed from the test side.

**Judgment:** this pass found **4 real defects in code that had passed 83 tests**. The most serious, F1, broke the poller's main promise and only appeared with the exact usage shown in the task instructions. **Using AI to attack its own output, with a rule against unproven claims, was more valuable than any of the original generation prompts.**

**Result:** 91 tests, 0 warnings, and the full suite passed 5 consecutive runs after the fixes.

---

## Takeaways on AI usage
- **The AI was fast at code that follows standard patterns** (fixtures, builders, handlers) and at listing edge cases. **It was wrong in confident ways at least three times:** the `ToString` assumption, the Python-dependent probes, and a test that passed in the suite but failed every time on its own. **Every one of them was caught by running the code**, not by reading it.
- **Better prompting:** asking for a brief task interpretation and proposed approach before implementation kept me in charge of scope and let me review design choices before code was written.
- **Bugs:** treat AI-generated bug claims as hypotheses until they've been reproduced against the running system. All 5 bug reports here were reproduced with curl first and then encoded as tests.








