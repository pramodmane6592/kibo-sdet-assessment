using System.Net;
using Kibo.TestingFramework;
using Kibo.TestingFramework.Builders;
using Kibo.TestingFramework.Clients;
using Kibo.TestingFramework.Models;
using Xunit.Abstractions;

namespace Kibo.LegacyTests;

/// <summary>
/// Order lifecycle tests, refactored onto <c>Kibo.TestingFramework</c>:
/// the HttpClient, base URL, tenant header, JSON handling and test data all live in the framework.
/// </summary>
[Collection(KiboApiCollection.Name)]
public sealed class OrderTests
{
    private readonly OrdersClient _orders;

    public OrderTests(KiboApiSession session, ITestOutputHelper output)
    {
        _orders = session.CreateOrdersClient(output.WriteLine);
    }

    [Fact]
    public async Task CreateOrder_ReturnsCreatedWithPendingStatus()
    {
        var request = OrderBuilder.Default().WithItems(2).Build();

        var response = await _orders.CreateOrderAsync(request);

        response.EnsureStatus(HttpStatusCode.Created);
        var order = response.EnsureData();
        Assert.NotEqual(Guid.Empty, order.Id);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal(_orders.TenantId, order.TenantId);
        Assert.Equal(request.CustomerEmail, order.CustomerEmail);
        Assert.Equivalent(request.LineItems, order.LineItems, strict: true);
    }

    [Fact]
    public async Task CreateOrder_ForSpecificTenant_AssignsOrderToThatTenant()
    {
        var request = OrderBuilder.Default().ForTenant("tenant-xyz").Build();

        var response = await _orders.CreateOrderAsync(request);

        response.EnsureStatus(HttpStatusCode.Created);
        Assert.Equal("tenant-xyz", response.EnsureData().TenantId);
    }

    [Fact]
    public async Task CreateOrder_WithoutTenantHeader_Returns401()
    {
        var response = await _orders.WithoutTenant().CreateOrderAsync(OrderBuilder.Default().Build());

        response.EnsureStatus(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetOrder_AfterCreation_StatusBecomesReadyForFulfillment()
    {
        var created = (await _orders.CreateOrderAsync(OrderBuilder.Default().Build())).EnsureData();
        Assert.Equal(OrderStatus.Pending, created.Status);

        // Polls every 500ms and returns as soon as the server flips the status (~5s),
        // instead of a fixed 6s sleep; fails with the last response after 15s.
        var ready = await _orders.WaitForOrderStatusAsync(created.Id, OrderStatus.ReadyForFulfillment);

        Assert.Equal(created.Id, ready.Id);
    }

    [Fact]
    public async Task GetOrder_ImmediatelyAfterCreation_IsStillPending()
    {
        var created = (await _orders.CreateOrderAsync(OrderBuilder.Default().Build())).EnsureData();

        var response = await _orders.GetOrderAsync(created.Id);

        response.EnsureStatus(HttpStatusCode.OK);
        Assert.Equal(OrderStatus.Pending, response.EnsureData().Status);
    }

    [Fact]
    public async Task GetOrder_WithUnknownId_Returns404()
    {
        var response = await _orders.GetOrderAsync(Guid.NewGuid());

        response.EnsureStatus(HttpStatusCode.NotFound);
    }
}
