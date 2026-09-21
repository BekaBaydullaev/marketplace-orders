using Marketplace.Api.Models;
using Npgsql;

namespace Marketplace.Api.Repositories;

public class UserRepository(NpgsqlDataSource dataSource)
{
    public async Task<User?> TryInsertAsync(string email, string passwordHash, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO users (email, password_hash)
            VALUES (@email, @passwordHash)
            ON CONFLICT (email) DO NOTHING
            RETURNING id, email, password_hash
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("passwordHash", passwordHash);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapUser(reader) : null;
    }

    public async Task<User?> GetByEmailAsync(string email, CancellationToken ct)
    {
        const string sql = """
            SELECT id, email, password_hash
            FROM users
            WHERE email = @email
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("email", email);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapUser(reader) : null;
    }

    private static User MapUser(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Email = reader.GetString(1),
        PasswordHash = reader.GetString(2)
    };
}