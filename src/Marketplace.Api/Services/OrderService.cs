using System.Security.Cryptography;
using System.Text;
using Marketplace.Api.Caching;
using Marketplace.Api.Contracts;
using Marketplace.Api.Models;
using Marketplace.Api.Repositories;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Marketplace.Api.Services;

public class OrderService(NpgsqlDataSource dataSource, OrderRepository orderRepository, ProductRepository productRepository, RedisCache cache, IOptions<OrderOptions> options)
{
    private const int MaxItemsPerOrder = 50;
    private const int MaxQuantityPerItem = 1000;
    private static readonly TimeSpan PendingOrderCacheTtl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FinalOrderCacheTtl = TimeSpan.FromMinutes(30);

    public async Task<ServiceResult<OrderCreationResult>> CreateAsync(long userId, string idempotencyKey, CreateOrderRequest request, CancellationToken ct)
    {
        var validationError = Validate(request);
        if (validationError is not null)
        {
            return validationError;
        }

        var items = request.Items
            .GroupBy(x => x.ProductId)
            .Select(x => new OrderItemRequest(x.Key, x.Sum(i => i.Quantity)))
            .OrderBy(x => x.ProductId)
            .ToList();

        var requestHash = ComputeRequestHash(items);

        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var orderId = await orderRepository.TryInsertAsync(userId, idempotencyKey, requestHash, options.Value.PaymentTimeout, transaction, ct);
        if (orderId is null)
        {
            await transaction.RollbackAsync(ct);
            return await ReplayAsync(userId, idempotencyKey, requestHash, ct);
        }

        var totalAmount = 0m;
        foreach (var item in items)
        {
            var unitPrice = await productRepository.TryReserveStockAsync(item.ProductId, item.Quantity, transaction, ct);
            if (unitPrice is null)
            {
                var productExists = await productRepository.ExistsAsync(item.ProductId, transaction, ct);
                await transaction.RollbackAsync(ct);

                return productExists
                    ? ServiceError.Conflict($"Not enough stock for product {item.ProductId}.")
                    : ServiceError.Validation($"Product {item.ProductId} does not exist.");
            }

            await orderRepository.InsertItemAsync(orderId.Value, item.ProductId, item.Quantity, unitPrice.Value, transaction, ct);
            totalAmount += unitPrice.Value * item.Quantity;
        }

        await orderRepository.SetTotalAmountAsync(orderId.Value, totalAmount, transaction, ct);
        await transaction.CommitAsync(ct);

        await cache.RemoveAsync(items.Select(x => CacheKeys.Product(x.ProductId)).ToArray());

        var order = await orderRepository.GetByIdAsync(orderId.Value, ct);
        return new OrderCreationResult(order!.ToResponse(), IsReplayed: false);
    }

    public async Task<ServiceResult<OrderResponse>> GetByIdAsync(long orderId, long userId, CancellationToken ct)
    {
        var order = await GetOrderAsync(orderId, ct);

        if (order is null || order.UserId != userId)
        {
            return ServiceError.NotFound($"Order {orderId} not found.");
        }

        return order.ToResponse();
    }

    public async Task<ServiceResult<OrderResponse>> PayAsync(long orderId, long userId, CancellationToken ct)
    {
        var confirmed = await orderRepository.TryConfirmAsync(orderId, userId, ct);
        if (!confirmed)
        {
            return await StatusChangeErrorAsync(orderId, userId, ct);
        }

        await cache.RemoveAsync(CacheKeys.Order(orderId));
        return await GetByIdAsync(orderId, userId, ct);
    }

    public async Task<ServiceResult<OrderResponse>> CancelAsync(long orderId, long userId, CancellationToken ct)
    {
        var cancelled = await TryCancelAsync(orderId, userId, ct);
        if (!cancelled)
        {
            return await StatusChangeErrorAsync(orderId, userId, ct);
        }

        return await GetByIdAsync(orderId, userId, ct);
    }

    public async Task<int> CancelExpiredAsync(int batchSize, CancellationToken ct)
    {
        var orderIds = await orderRepository.GetExpiredPendingIdsAsync(batchSize, ct);

        var cancelledCount = 0;
        foreach (var orderId in orderIds)
        {
            if (await TryCancelAsync(orderId, userId: null, ct))
            {
                cancelledCount++;
            }
        }

        return cancelledCount;
    }

    private async Task<bool> TryCancelAsync(long orderId, long? userId, CancellationToken ct)
    {
        await using var connection = await dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        var cancelled = await orderRepository.TryCancelAsync(orderId, userId, transaction, ct);
        if (!cancelled)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        var items = await orderRepository.GetItemsAsync(orderId, transaction, ct);
        foreach (var item in items)
        {
            await productRepository.ReleaseStockAsync(item.ProductId, item.Quantity, transaction, ct);
        }

        await transaction.CommitAsync(ct);

        var cacheKeys = items.Select(x => CacheKeys.Product(x.ProductId)).Append(CacheKeys.Order(orderId));
        await cache.RemoveAsync(cacheKeys.ToArray());

        return true;
    }

    private async Task<ServiceResult<OrderCreationResult>> ReplayAsync(long userId, string idempotencyKey, string requestHash, CancellationToken ct)
    {
        var order = await orderRepository.GetByIdempotencyKeyAsync(userId, idempotencyKey, ct);
        if (order is null)
        {
            return ServiceError.Conflict("Request with this Idempotency-Key is still being processed.");
        }

        if (order.RequestHash != requestHash)
        {
            return ServiceError.Validation("Idempotency-Key was already used with a different request.");
        }

        return new OrderCreationResult(order.ToResponse(), IsReplayed: true);
    }

    private async Task<ServiceError> StatusChangeErrorAsync(long orderId, long userId, CancellationToken ct)
    {
        var order = await orderRepository.GetByIdAsync(orderId, ct);
        if (order is null || order.UserId != userId)
        {
            return ServiceError.NotFound($"Order {orderId} not found.");
        }

        if (order.Status == OrderStatus.Pending)
        {
            return ServiceError.Conflict("Payment window for this order has expired.");
        }

        return ServiceError.Conflict($"Order is already {order.Status}.");
    }

    private async Task<Order?> GetOrderAsync(long orderId, CancellationToken ct)
    {
        var cacheKey = CacheKeys.Order(orderId);
        var order = await cache.GetAsync<Order>(cacheKey);
        if (order is not null)
        {
            return order;
        }

        order = await orderRepository.GetByIdAsync(orderId, ct);
        if (order is not null)
        {
            var ttl = order.Status == OrderStatus.Pending ? PendingOrderCacheTtl : FinalOrderCacheTtl;
            await cache.SetAsync(cacheKey, order, ttl);
        }

        return order;
    }

    private static ServiceError? Validate(CreateOrderRequest request)
    {
        if (request.Items.Count == 0 || request.Items.Count > MaxItemsPerOrder)
        {
            return ServiceError.Validation($"Order must contain from 1 to {MaxItemsPerOrder} items.");
        }

        if (request.Items.Any(x => x.Quantity <= 0 || x.Quantity > MaxQuantityPerItem))
        {
            return ServiceError.Validation($"Quantity must be between 1 and {MaxQuantityPerItem}.");
        }

        return null;
    }

    private static string ComputeRequestHash(List<OrderItemRequest> items)
    {
        var payload = string.Join(';', items.Select(x => $"{x.ProductId}:{x.Quantity}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}