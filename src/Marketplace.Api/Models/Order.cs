namespace Marketplace.Api.Models;

public class Order
{
    public long Id { get; init; }
    public long UserId { get; init; }
    public required string Status { get; init; }
    public decimal TotalAmount { get; init; }
    public required string RequestHash { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public List<OrderItem> Items { get; init; } = [];
}