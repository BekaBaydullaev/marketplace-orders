namespace Marketplace.Api.Models;

public class User
{
    public long Id { get; init; }
    public required string Email { get; init; }
    public string PasswordHash { get; init; } = string.Empty;
}