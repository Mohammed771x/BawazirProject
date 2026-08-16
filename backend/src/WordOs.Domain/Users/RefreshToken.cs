namespace WordOs.Domain.Users;

/// <summary>
/// A refresh token, stored hashed.
/// </summary>
/// <remarks>
/// Single-use and rotating: redeeming one issues a replacement and marks the
/// old one used. If a token is presented twice, the second attempt is a signal
/// that it leaked, so the whole family is revoked rather than quietly refused
/// (docs/07-SECURITY.md §2).
/// </remarks>
public class RefreshToken
{
    private RefreshToken() { } // EF Core

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid UserId { get; private set; }

    /// <summary>SHA-256 of the token. The token itself is never stored.</summary>
    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public DateTimeOffset? UsedAt { get; private set; }

    /// <summary>Ties rotations together so a leak can revoke the whole chain.</summary>
    public Guid FamilyId { get; private set; }

    public bool IsActive(DateTimeOffset now) =>
        RevokedAt is null && UsedAt is null && ExpiresAt > now;

    public static RefreshToken Issue(
        Guid userId,
        string tokenHash,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        Guid? familyId = null) =>
        new()
        {
            UserId = userId,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = expiresAt,
            FamilyId = familyId ?? Guid.CreateVersion7(),
        };

    public void MarkUsed(DateTimeOffset now) => UsedAt = now;

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
