namespace WordOs.Domain.Users;

/// <summary>
/// A one-time code that lets a learner who has forgotten their password set a
/// new one (ADR-078).
/// </summary>
/// <remarks>
/// Stored hashed, exactly like <see cref="RefreshToken"/> and for the same
/// reason: a stolen database must not hand the thief a working credential. The
/// code itself exists only in the email and in the request that redeems it
/// (docs/07-SECURITY.md §2).
///
/// Six digits is a million possibilities, which is far too few to leave
/// unguarded — so three independent limits bound the guessing, and all three
/// are needed:
///
/// <list type="bullet">
/// <item>the code dies after <see cref="ExpiresAt"/>, minutes not days;</item>
/// <item><see cref="Attempts"/> caps wrong tries against <i>this</i> code, so a
/// script cannot walk the space even at a permitted request rate;</item>
/// <item>requesting a new code invalidates the old one, so attempts cannot be
/// reset by asking again.</item>
/// </list>
/// </remarks>
public class PasswordResetCode
{
    private PasswordResetCode() { } // EF Core

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid UserId { get; private set; }

    /// <summary>
    /// Argon2id of the six digits. The code itself is never stored.
    /// </summary>
    /// <remarks>
    /// The same slow hash as a password, and for a sharper reason: six digits
    /// is a million possibilities, and a million SHA-256s is the work of an
    /// eye-blink. A fast hash would mean a leaked database hands over every
    /// outstanding reset code. Affordable because nothing looks a code up by
    /// its hash — redemption finds the row by user and verifies one candidate,
    /// so this costs one verification per attempt, exactly like a sign-in, and
    /// goes through the same concurrency cap (ADR-051).
    /// </remarks>
    public string CodeHash { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Set when the code was spent. A code works exactly once.</summary>
    public DateTimeOffset? UsedAt { get; private set; }

    /// <summary>
    /// Set when a newer request, or a completed reset, retired this code
    /// before it was used.
    /// </summary>
    public DateTimeOffset? InvalidatedAt { get; private set; }

    /// <summary>Wrong codes submitted against this row.</summary>
    public int Attempts { get; private set; }

    public bool IsRedeemable(DateTimeOffset now, int maxAttempts) =>
        UsedAt is null
        && InvalidatedAt is null
        && ExpiresAt > now
        && Attempts < maxAttempts;

    public static PasswordResetCode Issue(
        Guid userId,
        string codeHash,
        DateTimeOffset now,
        DateTimeOffset expiresAt) =>
        new()
        {
            UserId = userId,
            CodeHash = codeHash,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };

    public void MarkUsed(DateTimeOffset now) => UsedAt = now;

    public void Invalidate(DateTimeOffset now) => InvalidatedAt ??= now;

    /// <summary>
    /// Counts a wrong guess. Returns whether the code is now exhausted, so the
    /// caller can say "ask for a new one" rather than let a learner keep
    /// typing into something that will never open.
    /// </summary>
    public bool RecordFailedAttempt(int maxAttempts) => ++Attempts >= maxAttempts;
}
