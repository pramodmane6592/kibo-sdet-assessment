using Kibo.TestingFramework.Models;

namespace Kibo.TestingFramework.Builders;

/// <summary>
/// Fluent builder for <see cref="CreateOrderRequest"/> payloads.
/// </summary>
/// <remarks>
/// <para>
/// With no configuration, <see cref="Build"/> returns a valid order: a random customer e-mail and
/// one random line item. Every <c>With…</c> method returns the builder, so orders read as one line:
/// <code>
/// var order = OrderBuilder.Default().WithCustomerEmail("test@kibo.com").WithItems(2).ForTenant("t1").Build();
/// </code>
/// </para>
/// <para>
/// Random values come from a seeded <see cref="System.Random"/>. The seed is exposed via
/// <see cref="Seed"/> so a failing test can log it and reproduce the exact same data with
/// <see cref="WithSeed"/>.
/// </para>
/// <para>
/// Inputs are deliberately not validated (apart from programming errors such as a negative item
/// count) so the same builder can produce invalid payloads for negative tests.
/// </para>
/// </remarks>
public sealed class OrderBuilder
{
    // Each entry produces one line item at Build() time, so randomization honors the final seed.
    private readonly List<Func<Random, LineItem>> _items = [];
    private bool _itemsConfigured;
    private bool _omitLineItems;
    private bool _emailConfigured;
    private string? _customerEmail;
    private string? _tenantId;

    /// <summary>Creates a builder with a random seed.</summary>
    public OrderBuilder()
    {
        Seed = Random.Shared.Next();
    }

    /// <summary>Seed for random values; log it to reproduce generated data.</summary>
    public int Seed { get; private set; }

    /// <summary>Shorthand for <c>new OrderBuilder()</c>.</summary>
    public static OrderBuilder Default() => new();

    /// <summary>Uses a fixed seed so generated values are reproducible.</summary>
    /// <param name="seed">Random seed.</param>
    public OrderBuilder WithSeed(int seed)
    {
        Seed = seed;
        return this;
    }

    /// <summary>Sets the customer e-mail.</summary>
    /// <param name="email">E-mail (not validated). <c>null</c> omits the field from the payload.</param>
    public OrderBuilder WithCustomerEmail(string? email)
    {
        _customerEmail = email;
        _emailConfigured = true;
        return this;
    }

    /// <summary>Omits <c>customerEmail</c> from the payload entirely.</summary>
    public OrderBuilder WithoutCustomerEmail() => WithCustomerEmail(null);

    /// <summary>Adds <paramref name="count"/> random line items.</summary>
    /// <param name="count">Number of items to add; 0 results in an empty <c>lineItems</c> array.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public OrderBuilder WithItems(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        for (var i = 0; i < count; i++)
        {
            _items.Add(random => new LineItemBuilder(random).Build());
        }

        return MarkItemsConfigured();
    }

    /// <summary>Adds a line item with explicit values.</summary>
    /// <param name="productCode">Product SKU.</param>
    /// <param name="quantity">Number of units (not validated).</param>
    /// <param name="unitPrice">Price per unit (not validated).</param>
    public OrderBuilder WithItem(string productCode, int quantity, decimal unitPrice) =>
        WithItem(item => item.WithProductCode(productCode).WithQuantity(quantity).WithUnitPrice(unitPrice));

    /// <summary>Adds a line item, customizing only what matters; other values stay random.</summary>
    /// <param name="configure">Customizes the item, e.g. <c>i =&gt; i.WithUnitPrice(-1m)</c>.</param>
    public OrderBuilder WithItem(Func<LineItemBuilder, LineItemBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _items.Add(random => configure(new LineItemBuilder(random)).Build());
        return MarkItemsConfigured();
    }

    /// <summary>Adds pre-built line items (copied, so later changes to them don't leak in).</summary>
    /// <param name="items">Items to add.</param>
    public OrderBuilder WithLineItems(IEnumerable<LineItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        foreach (var item in items)
        {
            var (productCode, quantity, unitPrice) = (item.ProductCode, item.Quantity, item.UnitPrice);
            _items.Add(_ => new LineItem { ProductCode = productCode, Quantity = quantity, UnitPrice = unitPrice });
        }

        return MarkItemsConfigured();
    }

    /// <summary>Sends an empty <c>lineItems</c> array.</summary>
    public OrderBuilder WithNoItems()
    {
        _items.Clear();
        return MarkItemsConfigured();
    }

    /// <summary>Omits <c>lineItems</c> from the payload entirely.</summary>
    public OrderBuilder WithoutLineItems()
    {
        _items.Clear();
        _itemsConfigured = true;
        _omitLineItems = true;
        return this;
    }

    /// <summary>Creates the order under a specific tenant (sent as the <c>x-kibo-tenant</c> header).</summary>
    /// <param name="tenantId">Tenant identifier, sent verbatim.</param>
    public OrderBuilder ForTenant(string tenantId)
    {
        _tenantId = tenantId;
        return this;
    }

    /// <summary>
    /// Builds a new, independent <see cref="CreateOrderRequest"/>. Calling it repeatedly returns
    /// identical data for a given <see cref="Seed"/>.
    /// </summary>
    public CreateOrderRequest Build()
    {
        var random = new Random(Seed);

        var email = _emailConfigured ? _customerEmail : TestData.Email(random);

        List<LineItem>? lineItems = null;
        if (!_omitLineItems)
        {
            lineItems = _itemsConfigured
                ? _items.Select(create => create(random)).ToList()
                : [new LineItemBuilder(random).Build()];
        }

        return new CreateOrderRequest
        {
            TenantId = _tenantId,
            CustomerEmail = email,
            LineItems = lineItems,
        };
    }

    private OrderBuilder MarkItemsConfigured()
    {
        _itemsConfigured = true;
        _omitLineItems = false;
        return this;
    }
}
