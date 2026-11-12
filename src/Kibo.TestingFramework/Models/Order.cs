namespace Kibo.TestingFramework.Models;

/// <summary>An order as returned by the Kibo API.</summary>
public sealed class Order
{
    /// <summary>Server-generated identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Tenant taken from the <c>x-kibo-tenant</c> header at creation time.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Customer e-mail address.</summary>
    public string CustomerEmail { get; set; } = string.Empty;

    /// <summary>Lifecycle status; see <see cref="OrderStatus"/>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Ordered products.</summary>
    public List<LineItem> LineItems { get; set; } = [];
}

/// <summary>A single product line within an order.</summary>
public sealed class LineItem
{
    /// <summary>Product SKU.</summary>
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>Number of units.</summary>
    public int Quantity { get; set; }

    /// <summary>Price per unit.</summary>
    public decimal UnitPrice { get; set; }
}

/// <summary>Known values of <see cref="Order.Status"/>.</summary>
public static class OrderStatus
{
    /// <summary>Initial status of every new order.</summary>
    public const string Pending = "Pending";

    /// <summary>Status the server moves an order to roughly 5 seconds after creation.</summary>
    public const string ReadyForFulfillment = "ReadyForFulfillment";
}
