namespace Marketplace.Api.Contracts;

public record RegisterRequest(string Email, string Password);

public record LoginRequest(string Email, string Password);

public record UserResponse(long Id, string Email);

public record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt);