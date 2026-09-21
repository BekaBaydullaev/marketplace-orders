using Marketplace.Api.Auth;
using Marketplace.Api.Contracts;
using Marketplace.Api.Models;
using Marketplace.Api.Repositories;
using Microsoft.AspNetCore.Identity;

namespace Marketplace.Api.Services;

public class AuthService(UserRepository userRepository, JwtTokenGenerator tokenGenerator)
{
    private const int MinPasswordLength = 8;
    private static readonly PasswordHasher<User> PasswordHasher = new();

    public async Task<ServiceResult<UserResponse>> RegisterAsync(RegisterRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (!email.Contains('@') || email.Length > 255)
        {
            return ServiceError.Validation("Email is not valid.");
        }

        if (request.Password.Length < MinPasswordLength)
        {
            return ServiceError.Validation($"Password must be at least {MinPasswordLength} characters long.");
        }

        var passwordHash = PasswordHasher.HashPassword(new User { Email = email }, request.Password);
        var user = await userRepository.TryInsertAsync(email, passwordHash, ct);
        if (user is null)
        {
            return ServiceError.Conflict("Email is already registered.");
        }

        return new UserResponse(user.Id, user.Email);
    }

    public async Task<ServiceResult<TokenResponse>> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await userRepository.GetByEmailAsync(email, ct);
        if (user is null)
        {
            return ServiceError.Unauthorized("Invalid email or password.");
        }

        var verification = PasswordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            return ServiceError.Unauthorized("Invalid email or password.");
        }

        return tokenGenerator.Generate(user);
    }
}