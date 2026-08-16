using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using WordOs.Domain.Users;

namespace WordOs.Infrastructure.Security;

/// <summary>Issues access tokens and opaque refresh tokens.</summary>
public sealed class JwtTokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _options = options.Value;

    public sealed record IssuedToken(
        string AccessToken,
        DateTimeOffset ExpiresAt,
        string RefreshToken,
        DateTimeOffset RefreshExpiresAt);

    public IssuedToken Issue(User user, DateTimeOffset now)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(_options.SigningKey));

        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        // Only what authorization needs. No email, no display name — a JWT is
        // signed, not encrypted, so anything in it is readable by whoever holds
        // it (docs/07-SECURITY.md §12).
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
            new(ClaimTypes.Role, user.Role.ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(
                key, SecurityAlgorithms.HmacSha256));

        return new IssuedToken(
            AccessToken: new JwtSecurityTokenHandler().WriteToken(token),
            ExpiresAt: expires,
            // Opaque and random, not a JWT: it is a database lookup key, so it
            // carries no claims an attacker could read or tamper with.
            RefreshToken: Convert.ToBase64String(
                RandomNumberGenerator.GetBytes(48)),
            RefreshExpiresAt: now.AddDays(_options.RefreshTokenDays));
    }

    /// <summary>
    /// Hashes a refresh token for storage. Refresh tokens are stored hashed for
    /// the same reason passwords are: a database leak must not hand out live
    /// sessions.
    /// </summary>
    public static string HashRefreshToken(string refreshToken) =>
        Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
}
