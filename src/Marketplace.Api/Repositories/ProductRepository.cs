using Marketplace.Api.Models;
using Npgsql;

namespace Marketplace.Api.Repositories;

public class ProductRepository(NpgsqlDataSource dataSource)
{
    public async Task<Product> InsertAsync(string name, decimal price, int stock, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO products (name, price, stock)
            VALUES (@name, @price, @stock)
            RETURNING id, name, price, stock, created_at, updated_at
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("price", price);
        command.Parameters.AddWithValue("stock", stock);

        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return MapProduct(reader);
    }

    public async Task<Product?> GetByIdAsync(long productId, CancellationToken ct)
    {
        const string sql = """
            SELECT id, name, price, stock, created_at, updated_at
            FROM products
            WHERE id = @productId
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("productId", productId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapProduct(reader) : null;
    }

    public async Task<decimal?> TryReserveStockAsync(long productId, int quantity, NpgsqlTransaction transaction, CancellationToken ct)
    {
        const string sql = """
            UPDATE products
            SET stock = stock - @quantity, updated_at = now()
            WHERE id = @productId AND stock >= @quantity
            RETURNING price
            """;

        await using var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue("productId", productId);
        command.Parameters.AddWithValue("quantity", quantity);

        return (decimal?)await command.ExecuteScalarAsync(ct);
    }

    public async Task ReleaseStockAsync(long productId, int quantity, NpgsqlTransaction transaction, CancellationToken ct)
    {
        const string sql = """
            UPDATE products
            SET stock = stock + @quantity, updated_at = now()
            WHERE id = @productId
            """;

        await using var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue("productId", productId);
        command.Parameters.AddWithValue("quantity", quantity);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ExistsAsync(long productId, NpgsqlTransaction transaction, CancellationToken ct)
    {
        const string sql = "SELECT EXISTS (SELECT 1 FROM products WHERE id = @productId)";

        await using var command = new NpgsqlCommand(sql, transaction.Connection, transaction);
        command.Parameters.AddWithValue("productId", productId);

        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    private static Product MapProduct(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        Price = reader.GetDecimal(2),
        Stock = reader.GetInt32(3),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(4),
        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(5)
    };
}