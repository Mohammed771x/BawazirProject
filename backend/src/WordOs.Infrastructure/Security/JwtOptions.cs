using System.ComponentModel.DataAnnotations;

namespace WordOs.Infrastructure.Security;

/// <summary>
/// JWT settings. The signing key is a secret and is never committed.
/// </summary>
/// <remarks>
/// Bound from configuration and validated at startup, so a missing or weak key
/// stops the process rather than producing tokens anyone can forge.
/// </remarks>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; init; } = "wordos";

    [Required]
    public string Audience { get; init; } = "wordos-app";

    /// <summary>
    /// HMAC signing key. Must be at least 32 bytes — a shorter key weakens
    /// HS256 below its nominal strength.
    /// </summary>
    [Required]
    [MinLength(32, ErrorMessage =
        "Jwt:SigningKey must be at least 32 characters. Generate one with " +
        "`openssl rand -base64 48` and store it in user-secrets or the " +
        "environment — never in source.")]
    public string SigningKey { get; init; } = string.Empty;

    /// <summary>
    /// Deliberately short. A stolen access token stays useful for minutes, not
    /// weeks; the refresh token carries longevity and can be revoked.
    /// </summary>
    [Range(1, 1440)]
    public int AccessTokenMinutes { get; init; } = 15;

    [Range(1, 365)]
    public int RefreshTokenDays { get; init; } = 30;
}
