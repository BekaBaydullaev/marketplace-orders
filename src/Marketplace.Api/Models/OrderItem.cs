namespace Marketplace.Api.Models;

public class OrderItem
{
    public long ProductId { get; init; }
    public string? ProductName { get; init; }
    public int Quantity { get; init; }
    public decimal UnitPrice { get; init; }
}