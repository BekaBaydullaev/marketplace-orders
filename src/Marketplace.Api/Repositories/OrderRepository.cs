using Marketplace.Api.Models;
using Npgsql;

namespace Marketplace.Api.Repositories;

public class OrderRepository(NpgsqlDataSource dataSource)
{
    public async Task<long?> TryInsertAsync(long userId, string idempotencyKey, string requestHash, TimeSpan paymentTimeout, NpgsqlTransaction transaction, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO orders (user_id, status, idempotency_key, request_hash, expires_at)
            VALUES (@userId, 'pending', @idempotencyKey, @requestHash, now() + @paymentTimeout)
            ON CONFLICT (user_id, idempotency_key) DO NOTHING
            RETURNING id
            """;

        await using var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("idempotencyKey", idempotencyKey);
        command.Parameters.AddWithValue("requestHash", requestHash);
        command.Parameters.AddWithValue("paymentTimeout", paymentTimeout);

        return (long?)await command.ExecuteScalarAsync(ct);
    }

    public async Task InsertItemAsync(long orderId, long productId, int quantity, decimal unitPrice, NpgsqlTransaction transaction, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO order_items (order_id, product_id, quantity, unit_price)
            VALUES (@orderId, @productId, @quantity, @unitPrice)
            """;

        await using var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue("orderId", orderId);
        command.Parameters.AddWithValue("productId", productId);
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("unitPrice", unitPrice);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task SetTotalAmountAsync(long orderId, decimal totalAmount, NpgsqlTransaction transaction, CancellationToken ct)
    {
        const string sql = "UPDATE orders SET total_amount = @totalAmount WHERE id = @orderId";

        await using var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue("orderId", orderId);
        command.Parameters.AddWithValue("totalAmount", totalAmount);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<Order?> GetByIdAsync(long orderId, CancellationToken ct)
    {
        const string sql = """
            SELECT id, user_id, status, total_amount, request_hash, expires_at, created_at, updated_at
            FROM orders
            WHERE id = @orderId;

            SELECT oi.product_id, p.name, oi.quantity, oi.unit_price
            FROM order_items oi
            JOIN products p ON p.id = oi.product_id
            WHERE oi.order_id = @orderId
            ORDER BY oi.product_id;
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("orderId", orderId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        var order = new Order
        {
            Id = reader.GetInt64(0),
            UserId = reader.GetInt64(1),
            Status = reader.GetString(2),
            TotalAmount = reader.GetDecimal(3),
            RequestHash = reader.GetString(4),
            ExpiresAt = reader.GetFieldValue<DateTimeOffset>(5),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(6),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(7)
        };

        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            order.Items.Add(new OrderItem
            {
                ProductId = reader.GetInt64(0),
                ProductName = reader.GetString(1),
                Quantity = reader.GetInt32(2),
                UnitPrice = reader.GetDecimal(3)
            });
        }

        return order;
    }

    public async Task<Order?> GetByIdempotencyKeyAsync(long userId, string idempotencyKey, CancellationToken ct)
    {
        const string sql = """
            SELECT id
            FROM orders
            WHERE user_id = @userId AND idempotency_key = @idempotencyKey
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("userId", userId);
        command.Parameters.AddWithValue("idempotencyKey", idempotencyKey);

        var orderId = (long?)await command.ExecuteScalarAsync(ct);
        return orderId is null ? null : await GetByIdAsync(orderId.Value, ct);
    }

    public async Task<List<OrderItem>> GetItemsAsync(long orderId, NpgsqlTransaction transaction, CancellationToken ct)
    {
        const string sql = """
            SELECT product_id, quantity, unit_price
            FROM order_items
            WHERE order_id = @orderId
            ORDER BY product_id
            """;

        await using var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue("orderId", orderId);

        var items = new List<OrderItem>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new OrderItem
            {
                ProductId = reader.GetInt64(0),
                Quantity = reader.GetInt32(1),
                UnitPrice = reader.GetDecimal(2)
            });
        }

        return items;
    }

    public async Task<bool> TryConfirmAsync(long orderId, long userId, CancellationToken ct)
    {
        const string sql = """
            UPDATE orders
            SET status = 'confirmed', updated_at = now()
            WHERE id = @orderId AND user_id = @userId AND status = 'pending' AND expires_at > now()
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("orderId", orderId);
        command.Parameters.AddWithValue("userId", userId);

        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> TryCancelAsync(long orderId, long? userId, NpgsqlTransaction transaction, CancellationToken ct)
    {
        const string sql = """
            UPDATE orders
            SET status = 'cancelled', updated_at = now()
            WHERE id = @orderId AND status = 'pending' AND (@userId::bigint IS NULL OR user_id = @userId)
            """;

        await using var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue("orderId", orderId);
        command.Parameters.AddWithValue("userId", (object?)userId ?? DBNull.Value);

        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<List<long>> GetExpiredPendingIdsAsync(int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT id
            FROM orders
            WHERE status = 'pending' AND expires_at <= now()
            ORDER BY expires_at
            LIMIT @limit
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("limit", limit);

        var orderIds = new List<long>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            orderIds.Add(reader.GetInt64(0));
        }

        return orderIds;
    }
}