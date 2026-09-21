namespace Marketplace.Api.Contracts;

public record CreateOrderRequest(List<OrderItemRequest> Items);

public record OrderItemRequest(long ProductId, int Quantity);

public record OrderResponse(long Id, string Status, decimal TotalAmount, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, List<OrderItemResponse> Items);

public record OrderItemResponse(long ProductId, string? ProductName, int Quantity, decimal UnitPrice);