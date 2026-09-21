using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Marketplace.Api.Auth;

public static class ClaimsPrincipalExtensions
{
    public static long GetUserId(this ClaimsPrincipal principal)
    {
        var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (!long.TryParse(subject, out var userId))
        {
            throw new InvalidOperationException("Authenticated user has no valid subject claim.");
        }

        return userId;
    }
}