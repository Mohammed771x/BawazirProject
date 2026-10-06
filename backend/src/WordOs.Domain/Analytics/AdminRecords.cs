namespace WordOs.Domain.Analytics;

/// <summary>
/// An internal note on a piece of learner feedback (admin brief §17).
/// </summary>
/// <remarks>
/// Never shown to the learner. "Investigating Reading difficulty at B2" is the
/// team talking to itself, and keeping it beside the report is what stops two
/// people investigating the same thing twice.
/// </remarks>
public sealed class AdminNote
{
    private AdminNote() { }

    public Guid Id { get; private set; }

    public Guid FeedbackId { get; private set; }

    public Guid AuthorId { get; private set; }

    public string Body { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static AdminNote Create(Guid feedbackId, Guid authorId, string body, DateTimeOffset now)
    {
        var text = body?.Trim() ?? string.Empty;
        if (text.Length == 0)
            throw new ArgumentException("A note cannot be empty.", nameof(body));

        return new AdminNote
        {
            Id = Guid.CreateVersion7(now),
            FeedbackId = feedbackId,
            AuthorId = authorId,
            Body = text,
            CreatedAt = now,
        };
    }
}

/// <summary>
/// A question an admin asked of the data, and what came back (admin brief §25–§27).
/// </summary>
/// <remarks>
/// The whole result is kept, not only the question: an investigation is
/// evidence for a product decision, and evidence that recomputes differently
/// next month is not evidence. Re-opening one shows what was true when it was
/// asked; asking again shows what is true now.
/// </remarks>
public sealed class AdminInquiry
{
    private AdminInquiry() { }

    public Guid Id { get; private set; }

    public Guid AuthorId { get; private set; }

    public string Section { get; private set; } = string.Empty;

    public string Question { get; private set; } = string.Empty;

    /// <summary>The one-paragraph answer, for the history list.</summary>
    public string Summary { get; private set; } = string.Empty;

    /// <summary><c>ai</c> or <c>rules</c> — who wrote the interpretation.</summary>
    public string InterpretedBy { get; private set; } = string.Empty;

    /// <summary>The full investigation as the admin saw it.</summary>
    public string ResultJson { get; private set; } = "{}";

    public DateTimeOffset CreatedAt { get; private set; }

    public static AdminInquiry Create(
        Guid id, Guid authorId, string section, string question, string summary,
        string interpretedBy, string resultJson, DateTimeOffset now) =>
        new()
        {
            Id = id,
            AuthorId = authorId,
            Section = section,
            Question = question.Trim(),
            Summary = summary,
            InterpretedBy = interpretedBy,
            ResultJson = resultJson,
            CreatedAt = now,
        };
}

/// <summary>
/// Who in the admin area looked at, or changed, what (admin brief §33).
/// </summary>
/// <remarks>
/// The admin area shows learners' email addresses and phone numbers and every
/// answer they gave. A record of who opened which learner is the minimum that
/// makes handing that access to a second person defensible.
/// </remarks>
public sealed class AdminAuditEvent
{
    private AdminAuditEvent() { }

    public long Id { get; private set; }

    public Guid ActorId { get; private set; }

    /// <summary><c>user.viewed</c>, <c>inquiry.created</c>, <c>feedback.note</c>…</summary>
    public string Action { get; private set; } = string.Empty;

    public string? TargetId { get; private set; }

    public string? Detail { get; private set; }

    public string? ClientAddress { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static AdminAuditEvent Record(
        Guid actorId, string action, DateTimeOffset now,
        string? targetId = null, string? detail = null, string? clientAddress = null) =>
        new()
        {
            ActorId = actorId,
            Action = action,
            TargetId = targetId,
            Detail = detail is { Length: > 256 } ? detail[..256] : detail,
            ClientAddress = clientAddress is { Length: > 64 } ? clientAddress[..64] : clientAddress,
            CreatedAt = now,
        };
}
