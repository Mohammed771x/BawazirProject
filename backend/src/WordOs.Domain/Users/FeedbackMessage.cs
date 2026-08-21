namespace WordOs.Domain.Users;

/// <summary>
/// How the Owner reads it: unread, or dealt with.
/// </summary>
/// <remarks>
/// Two states and no more. A longer workflow — triaged, in progress, won't fix —
/// is a support system, and what this replaces is a learner having no way to
/// reach anyone at all.
/// </remarks>
public enum FeedbackStatus
{
    New,
    Handled,
}

/// <summary>
/// Something a learner wanted to tell the Owner (ADR-053).
/// </summary>
/// <remarks>
/// A learner who hits a problem has no way to report it: no email address in the
/// app, no support screen, nothing. The realistic alternative is that they stop
/// using it, and nobody ever learns why — which for an experiment whose whole
/// point is measurement is the worst possible outcome.
///
/// Unlike <see cref="ActivityEvent"/> this deliberately <i>does</i> hold free
/// text the learner typed, which makes two things the caller's responsibility:
/// it is bounded in length at the edge, and it is never interpreted — it is
/// shown as text and nothing else.
///
/// The row is kept even if the learner is later archived: a bug report outlives
/// the session that produced it.
/// </remarks>
public sealed class FeedbackMessage
{
    private FeedbackMessage() { }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>What they wrote, exactly as they wrote it.</summary>
    public string Body { get; private set; } = string.Empty;

    /// <summary>
    /// Which build they were running.
    /// </summary>
    /// <remarks>
    /// Sent by the client rather than asked of the learner: "it crashed" is
    /// twice as useful with a version beside it, and nobody reporting a crash
    /// should have to go and find one.
    /// </remarks>
    public string? AppVersion { get; private set; }

    /// <summary>iOS, Android, web — same reason as the version.</summary>
    public string? Platform { get; private set; }

    public FeedbackStatus Status { get; private set; } = FeedbackStatus.New;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>When the Owner marked it dealt with.</summary>
    public DateTimeOffset? HandledAt { get; private set; }

    public static FeedbackMessage Create(
        Guid userId,
        string body,
        DateTimeOffset now,
        string? appVersion = null,
        string? platform = null)
    {
        var text = body?.Trim() ?? string.Empty;

        if (text.Length == 0)
            throw new ArgumentException("Feedback cannot be empty.", nameof(body));

        return new FeedbackMessage
        {
            Id = Guid.CreateVersion7(now),
            UserId = userId,
            Body = text,
            AppVersion = Trimmed(appVersion),
            Platform = Trimmed(platform),
            Status = FeedbackStatus.New,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Marks it dealt with, or puts it back.
    /// </summary>
    /// <remarks>
    /// Reversible on purpose: the Owner reading a long list will mark the wrong
    /// one eventually, and a message that cannot be un-handled is a message that
    /// is lost.
    /// </remarks>
    public void SetHandled(bool handled, DateTimeOffset now)
    {
        Status = handled ? FeedbackStatus.Handled : FeedbackStatus.New;
        HandledAt = handled ? now : null;
    }

    private static string? Trimmed(string? value)
    {
        var text = value?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
