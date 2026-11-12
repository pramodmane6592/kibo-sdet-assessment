using System.Net;
using Kibo.TestingFramework;
using Kibo.TestingFramework.Builders;
using Kibo.TestingFramework.Clients;
using Kibo.TestingFramework.Models;
using Xunit.Abstractions;

namespace Kibo.LegacyTests;

/// <summary>
/// Destructive and edge-case scenarios for <c>POST /v1/orders</c> and <c>GET /v1/orders/{id}</c>,
/// generated with an LLM and verified against the running API (see PROMPT_LOG.md).
/// </summary>
/// <remarks>
/// <para>
/// Tests for <b>correct</b> behavior assert it directly. Tests for <b>defects</b> pin the current
/// behavior via <see cref="KnownBug.AssertStatus{T}"/>: the suite stays green, the expected behavior is
/// documented next to the actual one, and the test fails loudly once the defect is fixed.
/// Run only the defects with <c>dotnet test --filter "Category=KnownBug"</c>.
/// </para>
/// <para><b>Bug summary</b></para>
/// <list type="table">
/// <listheader><term>ID</term><description>Severity — summary</description></listheader>
/// <item><term>BUG-01</term><description>High (security) — GET ignores tenant: any caller can read any tenant's order.</description></item>
/// <item><term>BUG-02</term><description>Medium (security) — tenant header value is not validated.</description></item>
/// <item><term>BUG-03</term><description>High (data integrity) — line item quantity, price and SKU are not validated.</description></item>
/// <item><term>BUG-04</term><description>Medium — orders without line items, or with null line items, are accepted.</description></item>
/// <item><term>BUG-05</term><description>Medium — customerEmail is not validated (missing, empty, malformed, 1 MB).</description></item>
/// </list>
/// </remarks>
[Collection(KiboApiCollection.Name)]
[Trait("Category", "EdgeCase")]
public sealed class OrderEdgeCaseTests
{
    private readonly OrdersClient _orders;

    public OrderEdgeCaseTests(KiboApiSession session, ITestOutputHelper output)
    {
        _orders = session.CreateOrdersClient(output.WriteLine);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Scenario 1 — Tenant isolation
    //
    // BUG-01 · High · Security (broken object-level authorization / cross-tenant data leak)
    //   Steps:    1. POST /v1/orders with x-kibo-tenant: tenant-A  → 201, note id
    //             2. GET  /v1/orders/{id} with x-kibo-tenant: tenant-B (or with no header)
    //   Expected: 404 Not Found for another tenant (404 rather than 403, so order ids cannot be
    //             probed for existence); 401 Unauthorized without the header.
    //   Actual:   200 OK with the full order, including the other tenant's customerEmail (PII).
    //   Cause:    OrdersController.GetOrder never reads x-kibo-tenant and OrderStore is keyed by id only.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    [Trait("Category", "KnownBug")]
    public async Task GetOrder_AsDifferentTenant_LeaksOrder_Bug01()
    {
        var created = await CreateOrderAsync(OrderBuilder.Default().ForTenant($"tenant-a-{Guid.NewGuid():N}"));

        var response = await _orders.ForTenant($"tenant-b-{Guid.NewGuid():N}").GetOrderAsync(created.Id);

        KnownBug.AssertStatus(response, "BUG-01", expected: HttpStatusCode.NotFound, actual: HttpStatusCode.OK);
        Assert.Equal(created.CustomerEmail, response.EnsureData().CustomerEmail); // the leaked PII
    }

    [Fact]
    [Trait("Category", "KnownBug")]
    public async Task GetOrder_WithoutTenant_LeaksOrder_Bug01()
    {
        var created = await CreateOrderAsync(OrderBuilder.Default());

        var response = await _orders.WithoutTenant().GetOrderAsync(created.Id);

        KnownBug.AssertStatus(response, "BUG-01", expected: HttpStatusCode.Unauthorized, actual: HttpStatusCode.OK);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Scenario 2 — Hostile values in the x-kibo-tenant header
    //
    // BUG-02 · Medium · Security (missing input validation)
    //   Expected: 400 Bad Request (or 401/403 for an unknown tenant) when the tenant id does not
    //             match the tenant-id format, e.g. ^[a-z0-9-]{1,64}$.
    //   Actual:   201 Created; the raw value is persisted as tenantId and echoed back. There is no SQL
    //             backend today so nothing executes, but the value flows unescaped into storage, logs
    //             and any downstream system (second-order SQL/log/HTML injection risk), and a 20 KB
    //             tenant id is stored per order (storage abuse).
    //   Note:     Whitespace-only values are correctly rejected with 401 (see the test below).
    // ─────────────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [Trait("Category", "KnownBug")]
    [InlineData("' OR 1=1; DROP TABLE orders;--")]
    [InlineData("tenant-abc-123' UNION SELECT * FROM tenants--")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("../../etc/passwd")]
    public async Task CreateOrder_WithInjectionInTenantHeader_IsAcceptedVerbatim_Bug02(string hostileTenant)
    {
        var response = await _orders.CreateOrderAsync(OrderBuilder.Default().ForTenant(hostileTenant).Build());

        KnownBug.AssertStatus(response, "BUG-02", expected: HttpStatusCode.BadRequest, actual: HttpStatusCode.Created);
        Assert.Equal(hostileTenant, response.EnsureData().TenantId);
    }

    [Fact]
    [Trait("Category", "KnownBug")]
    public async Task CreateOrder_WithOversizedTenantHeader_IsAccepted_Bug02()
    {
        var hugeTenant = new string('t', 20_000);

        var response = await _orders.CreateOrderAsync(OrderBuilder.Default().ForTenant(hugeTenant).Build());

        KnownBug.AssertStatus(response, "BUG-02", expected: HttpStatusCode.BadRequest, actual: HttpStatusCode.Created);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task CreateOrder_WithWhitespaceTenant_Returns401(string blankTenant)
    {
        var response = await _orders.CreateOrderAsync(OrderBuilder.Default().ForTenant(blankTenant).Build());

        response.EnsureStatus(HttpStatusCode.Unauthorized);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Scenario 3 — Invalid line item values
    //
    // BUG-03 · High · Data integrity / financial
    //   Expected: 400 Bad Request (ValidationProblemDetails naming the field) for quantity <= 0,
    //             unitPrice < 0, or an empty productCode.
    //   Actual:   201 Created and the order proceeds to ReadyForFulfillment. A negative price yields a
    //             negative order total (a refund/credit vector); quantity 0 or -5 is not fulfillable.
    //   Cause:    LineItem has no [Range]/[Required] annotations and the controller performs no checks.
    //   Open question for product: is unitPrice 0 (free item) valid? Not asserted here.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [Trait("Category", "KnownBug")]
    [InlineData("SKU-001", 1, -10.00, "negative price")]
    [InlineData("SKU-001", 1, -0.01, "price just below zero")]
    [InlineData("SKU-001", 0, 9.99, "zero quantity")]
    [InlineData("SKU-001", -5, 9.99, "negative quantity")]
    [InlineData("SKU-001", int.MinValue, 9.99, "int.MinValue quantity")]
    [InlineData("", 1, 9.99, "empty product code")]
    public async Task CreateOrder_WithInvalidLineItem_IsAccepted_Bug03(string productCode, int quantity, double unitPrice, string scenario)
    {
        var request = OrderBuilder.Default().WithItem(productCode, quantity, (decimal)unitPrice).Build();

        var response = await _orders.CreateOrderAsync(request);

        KnownBug.AssertStatus(response, $"BUG-03 ({scenario})", expected: HttpStatusCode.BadRequest, actual: HttpStatusCode.Created);
        Assert.Equivalent(request.LineItems, response.EnsureData().LineItems, strict: true);
    }

    [Fact]
    public async Task CreateOrder_WithQuantityAboveInt32_Returns400NamingTheField()
    {
        const string json = """{"customerEmail":"a@kibo-test.example","lineItems":[{"productCode":"SKU-1","quantity":2147483648,"unitPrice":1}]}""";

        var response = await _orders.CreateOrderRawAsync(json);

        response.EnsureStatus(HttpStatusCode.BadRequest);
        Assert.Contains("lineItems[0].quantity", response.Body);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Scenario 4 — Empty, missing and null line items
    //
    // BUG-04 · Medium · Data integrity
    //   Expected: 400 Bad Request — an order must contain at least one non-null line item.
    //   Actual:   "lineItems": []      → 201 (empty order created and sent to fulfillment)
    //             field omitted        → 201 (defaults to [])
    //             "lineItems": [null]  → 201 and a null element is PERSISTED; any downstream code
    //                                    iterating line items will throw NullReferenceException.
    //   Inconsistency: an explicit "lineItems": null IS rejected with 400 (implicit [Required] from the
    //             non-nullable property), so "missing" and "null" behave differently.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    [Trait("Category", "KnownBug")]
    public async Task CreateOrder_WithEmptyLineItems_IsAccepted_Bug04()
    {
        var response = await _orders.CreateOrderAsync(OrderBuilder.Default().WithNoItems().Build());

        KnownBug.AssertStatus(response, "BUG-04", expected: HttpStatusCode.BadRequest, actual: HttpStatusCode.Created);
        Assert.Empty(response.EnsureData().LineItems);
    }

    [Fact]
    [Trait("Category", "KnownBug")]
    public async Task CreateOrder_WithLineItemsOmitted_IsAccepted_Bug04()
    {
        var response = await _orders.CreateOrderAsync(OrderBuilder.Default().WithoutLineItems().Build());

        KnownBug.AssertStatus(response, "BUG-04", expected: HttpStatusCode.BadRequest, actual: HttpStatusCode.Created);
    }

    [Fact]
    [Trait("Category", "KnownBug")]
    public async Task CreateOrder_WithNullLineItemElement_PersistsNull_Bug04()
    {
        var response = await _orders.CreateOrderRawAsync("""{"customerEmail":"a@kibo-test.example","lineItems":[null]}""");

        KnownBug.AssertStatus(response, "BUG-04", expected: HttpStatusCode.BadRequest, actual: HttpStatusCode.Created);
        var stored = (await _orders.GetOrderAsync(response.EnsureData().Id)).EnsureData();
        Assert.Null(Assert.Single(stored.LineItems));
    }

    [Fact]
    public async Task CreateOrder_WithExplicitNullLineItems_Returns400()
    {
        var response = await _orders.CreateOrderRawAsync("""{"customerEmail":"a@kibo-test.example","lineItems":null}""");

        response.EnsureStatus(HttpStatusCode.BadRequest);
        Assert.Contains("LineItems", response.Body);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Scenario 5 — customerEmail validation, size limits and encoding
    //
    // BUG-05 · Medium · Data quality / storage abuse
    //   Expected: 400 Bad Request for a missing, empty, malformed or unreasonably long (> 254 chars,
    //             RFC 5321) customerEmail.
    //   Actual:   omitted → 201 with customerEmail ""; "" → 201; "not-an-email" → 201;
    //             a 1,000,006-character address → 201 and stored in full (memory/storage abuse —
    //             ~30 MB per request is possible up to Kestrel's body limit).
    //   Inconsistency: explicit "customerEmail": null IS rejected with 400, but omitting it is not.
    //   Working as expected: Unicode round-trips intact; bodies over 30 MB get 413; malformed JSON gets 400.
    // ─────────────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [Trait("Category", "KnownBug")]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("missing-domain@")]
    [InlineData("@missing-local.com")]
    [InlineData("<script>alert(1)</script>")]
    public async Task CreateOrder_WithInvalidEmail_IsAccepted_Bug05(string email)
    {
        var response = await _orders.CreateOrderAsync(OrderBuilder.Default().WithCustomerEmail(email).Build());

        KnownBug.AssertStatus(response, "BUG-05", expected: HttpStatusCode.BadRequest, actual: HttpStatusCode.Created);
        Assert.Equal(email, response.EnsureData().CustomerEmail);
    }

    [Fact]
    [Trait("Category", "KnownBug")]
    public async Task CreateOrder_WithEmailOmitted_IsAcceptedWithEmptyEmail_Bug05()
    {
        var response = await _orders.CreateOrderAsync(OrderBuilder.Default().WithoutCustomerEmail().Build());

        KnownBug.AssertStatus(response, "BUG-05", expected: HttpStatusCode.BadRequest, actual: HttpStatusCode.Created);
        Assert.Equal(string.Empty, response.EnsureData().CustomerEmail);
    }

    [Fact]
    [Trait("Category", "KnownBug")]
    public async Task CreateOrder_WithOneMegabyteEmail_IsAcceptedAndStored_Bug05()
    {
        var hugeEmail = new string('a', 1_000_000) + "@b.com";

        var response = await _orders.CreateOrderAsync(OrderBuilder.Default().WithCustomerEmail(hugeEmail).Build());

        KnownBug.AssertStatus(response, "BUG-05", expected: HttpStatusCode.BadRequest, actual: HttpStatusCode.Created);
        Assert.Equal(hugeEmail.Length, response.EnsureData().CustomerEmail.Length);
    }

    [Fact]
    public async Task CreateOrder_WithBodyOverKestrelLimit_Returns413()
    {
        // Kestrel's default MaxRequestBodySize is 30,000,000 bytes.
        var request = OrderBuilder.Default().WithCustomerEmail(new string('a', 31_000_000)).Build();

        var response = await _orders.CreateOrderAsync(request);

        response.EnsureStatus(HttpStatusCode.RequestEntityTooLarge);
    }

    [Theory]
    [InlineData("""{"customerEmail":""")]               // truncated
    [InlineData("""{"customerEmail":"a@b.com",}""")]    // trailing comma
    [InlineData("""["not","an","object"]""")]           // wrong root type
    [InlineData("")]                                     // empty body
    public async Task CreateOrder_WithMalformedJson_Returns400(string json)
    {
        var response = await _orders.CreateOrderRawAsync(json);

        response.EnsureStatus(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateOrder_WithUnicodeAndEmoji_RoundTripsIntact()
    {
        var request = OrderBuilder.Default()
            .WithCustomerEmail("jörg.müller+测试@exämple.com")
            .WithItem("SKU-🚀-Ω-ü", 1, 19.99m)
            .Build();

        var created = await CreateOrderAsync(request);
        var fetched = (await _orders.GetOrderAsync(created.Id)).EnsureData();

        Assert.Equal(request.CustomerEmail, fetched.CustomerEmail);
        Assert.Equal("SKU-🚀-Ω-ü", Assert.Single(fetched.LineItems).ProductCode);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────
    // Bonus — Mass assignment: server-controlled fields in the request body must be ignored.
    //   Expected and actual: id, tenantId and status sent by the client are overwritten. ✅
    // ─────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CreateOrder_WithServerControlledFieldsInBody_IgnoresThem()
    {
        var spoofedId = Guid.NewGuid();
        var json = $$"""
            {
              "id": "{{spoofedId}}",
              "tenantId": "someone-elses-tenant",
              "status": "{{OrderStatus.ReadyForFulfillment}}",
              "customerEmail": "spoof@kibo-test.example",
              "lineItems": [{ "productCode": "SKU-1", "quantity": 1, "unitPrice": 1 }]
            }
            """;

        var response = await _orders.CreateOrderRawAsync(json);

        response.EnsureStatus(HttpStatusCode.Created);
        var order = response.EnsureData();
        Assert.NotEqual(spoofedId, order.Id);
        Assert.Equal(_orders.TenantId, order.TenantId);
        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    private Task<Order> CreateOrderAsync(OrderBuilder builder) => CreateOrderAsync(builder.Build());

    private async Task<Order> CreateOrderAsync(CreateOrderRequest request)
    {
        var response = await _orders.CreateOrderAsync(request);
        response.EnsureStatus(HttpStatusCode.Created);
        return response.EnsureData();
    }
}
