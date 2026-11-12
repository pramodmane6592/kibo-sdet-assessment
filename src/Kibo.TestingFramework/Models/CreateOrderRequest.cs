using System.Text.Json.Serialization;

namespace Kibo.TestingFramework.Models;

/// <summary>
/// Payload for <c>POST /v1/orders</c>. Properties are nullable so negative tests can omit them;
/// <c>null</c> properties are left out of the serialized JSON.
/// </summary>
public sealed class CreateOrderRequest
{
    /// <summary>
    /// Tenant to create the order under. Not part of the JSON body — the API reads the tenant from
    /// the <c>x-kibo-tenant</c> header, so <see cref="Clients.OrdersClient"/> sends it there.
    /// When <c>null</c>, the client's default tenant is used.
    /// </summary>
    [JsonIgnore]
    public string? TenantId { get; set; }

    /// <summary>Customer e-mail address.</summary>
    public string? CustomerEmail { get; set; }

    /// <summary>Ordered products.</summary>
    public List<LineItem>? LineItems { get; set; }
}
