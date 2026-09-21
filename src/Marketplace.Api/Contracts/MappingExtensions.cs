using Marketplace.Api.Models;

namespace Marketplace.Api.Contracts;

public static class MappingExtensions
{
    public static ProductResponse ToResponse(this Product product)
    {
        return new ProductResponse(product.Id, product.Name, product.Price, product.Stock, product.CreatedAt);
    }

    public static OrderResponse ToResponse(this Order order)
    {
        var items = order.Items
            .Select(x => new OrderItemResponse(x.ProductId, x.ProductName, x.Quantity, x.UnitPrice))
            .ToList();

        return new OrderResponse(order.Id, order.Status, order.TotalAmount, order.ExpiresAt, order.CreatedAt, items);
    }
}