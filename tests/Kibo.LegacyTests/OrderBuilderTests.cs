using Kibo.TestingFramework.Builders;
using Kibo.TestingFramework.Models;

namespace Kibo.LegacyTests;

/// <summary>Unit tests for the fluent builders — no running API required.</summary>
public sealed class OrderBuilderTests
{
    [Fact]
    public void Build_WithNoConfiguration_ProducesValidOrder()
    {
        var order = new OrderBuilder().Build();

        Assert.EndsWith("@" + TestData.EmailDomain, order.CustomerEmail);
        var item = Assert.Single(order.LineItems!);
        Assert.StartsWith("SKU-", item.ProductCode);
        Assert.InRange(item.Quantity, 1, 10);
        Assert.InRange(item.UnitPrice, 1.00m, 500.00m);
        Assert.Null(order.TenantId);
    }

    [Fact]
    public void FluentChain_SetsEveryValue()
    {
        var order = new OrderBuilder()
            .WithCustomerEmail("test@kibo.com")
            .WithItems(2)
            .ForTenant("tenant-xyz")
            .Build();

        Assert.Equal("test@kibo.com", order.CustomerEmail);
        Assert.Equal(2, order.LineItems!.Count);
        Assert.Equal("tenant-xyz", order.TenantId);
    }

    [Fact]
    public void WithItem_ExplicitAndPartialItems_AreAppendedInOrder()
    {
        var order = new OrderBuilder()
            .WithItem("SKU-001", 2, 29.99m)
            .WithItem(i => i.WithUnitPrice(-1m))
            .WithItems(1)
            .Build();

        Assert.Collection(
            order.LineItems!,
            first => Assert.Equivalent(new LineItem { ProductCode = "SKU-001", Quantity = 2, UnitPrice = 29.99m }, first),
            second =>
            {
                Assert.Equal(-1m, second.UnitPrice);
                Assert.InRange(second.Quantity, 1, 10); // unset values stay random
            },
            third => Assert.StartsWith("SKU-", third.ProductCode));
    }

    [Fact]
    public void WithLineItems_CopiesItems_SoLaterMutationDoesNotLeak()
    {
        var item = new LineItem { ProductCode = "SKU-777", Quantity = 1, UnitPrice = 5m };
        var builder = new OrderBuilder().WithLineItems([item]);

        item.Quantity = 999;

        Assert.Equal(1, Assert.Single(builder.Build().LineItems!).Quantity);
    }

    [Fact]
    public void WithNoItems_SendsEmptyArray_WhileWithoutLineItems_OmitsField()
    {
        Assert.Empty(new OrderBuilder().WithNoItems().Build().LineItems!);
        Assert.Null(new OrderBuilder().WithoutLineItems().Build().LineItems);
    }

    [Fact]
    public void WithoutCustomerEmail_OmitsField()
    {
        Assert.Null(new OrderBuilder().WithoutCustomerEmail().Build().CustomerEmail);
    }

    [Fact]
    public void SameSeed_ProducesIdenticalData()
    {
        var first = new OrderBuilder().WithSeed(42).WithItems(3).Build();
        var second = new OrderBuilder().WithSeed(42).WithItems(3).Build();

        Assert.Equivalent(first, second, strict: true);
    }

    [Fact]
    public void Build_ReturnsIndependentInstances()
    {
        var builder = new OrderBuilder().WithItems(1);

        var first = builder.Build();
        first.LineItems!.Clear();

        Assert.Single(builder.Build().LineItems!);
    }

    [Fact]
    public void WithItems_NegativeCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OrderBuilder().WithItems(-1));
    }
}
