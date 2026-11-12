using Kibo.TestingFramework.Models;

namespace Kibo.TestingFramework.Builders;

/// <summary>
/// Fluent builder for <see cref="LineItem"/>. Any value not set explicitly is randomized at
/// <see cref="Build"/> time. Values are not validated, so invalid items (zero quantity,
/// negative price, ...) can be built on purpose for negative tests.
/// </summary>
public sealed class LineItemBuilder
{
    private readonly Random _random;
    private string? _productCode;
    private int? _quantity;
    private decimal? _unitPrice;

    /// <summary>Creates a builder.</summary>
    /// <param name="random">Source of random values; pass a seeded instance for reproducible data.</param>
    public LineItemBuilder(Random? random = null)
    {
        _random = random ?? Random.Shared;
    }

    /// <summary>Sets the product SKU.</summary>
    /// <param name="productCode">Product code; <c>null</c> keeps the random default.</param>
    public LineItemBuilder WithProductCode(string? productCode)
    {
        _productCode = productCode;
        return this;
    }

    /// <summary>Sets the number of units.</summary>
    /// <param name="quantity">Quantity (not validated).</param>
    public LineItemBuilder WithQuantity(int quantity)
    {
        _quantity = quantity;
        return this;
    }

    /// <summary>Sets the price per unit.</summary>
    /// <param name="unitPrice">Unit price (not validated).</param>
    public LineItemBuilder WithUnitPrice(decimal unitPrice)
    {
        _unitPrice = unitPrice;
        return this;
    }

    /// <summary>Builds a new <see cref="LineItem"/>, randomizing unset values.</summary>
    public LineItem Build() => new()
    {
        ProductCode = _productCode ?? TestData.ProductCode(_random),
        Quantity = _quantity ?? TestData.Quantity(_random),
        UnitPrice = _unitPrice ?? TestData.UnitPrice(_random),
    };
}
