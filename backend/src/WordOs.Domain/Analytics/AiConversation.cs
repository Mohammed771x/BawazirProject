namespace WordOs.Domain.Analytics;

/// <summary>
/// A WordOS AI conversation (ADR-133) — the admin's, saved as it goes.
/// </summary>
/// <remarks>
/// A conversation is not an investigation and not a decision. Saving a
/// result as an investigation, or proposing a decision from it, are explicit
/// acts that create those records; nothing here becomes one by being said.
/// </remarks>
public sealed class AiConversation
{
    private AiConversation() { }

    public Guid Id { get; private set; }
    public Guid AuthorId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public bool Archived { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static AiConversation Start(Guid id, Guid authorId, string title, DateTimeOffset now) =>
        new() { Id = id, AuthorId = authorId, Title = Clip(title), CreatedAt = now, UpdatedAt = now };

    public void Rename(string title, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        Title = Clip(title);
        UpdatedAt = now;
    }

    public void Archive(bool archived, DateTimeOffset now)
    {
        Archived = archived;
        UpdatedAt = now;
    }

    public void Touch(DateTimeOffset now) => UpdatedAt = now;

    private static string Clip(string title)
    {
        var t = title.Trim().ReplaceLineEndings(" ");
        return t.Length <= 120 ? t : t[..120];
    }
}

/// <summary>One message in a WordOS AI conversation.</summary>
/// <remarks>
/// An answer keeps what it rested on: <see cref="SourcesJson"/> lists every
/// tool the backend ran for it, with arguments, period, sample size and the
/// aggregates the model read — the "sources of the analysis" the page shows,
/// and what a follow-up may reuse instead of querying again.
/// <see cref="AttachmentsJson"/> holds the tables, charts and user lists shown
/// under it; user lists go to the page only, never to the model.
/// </remarks>
public sealed class AiMessage
{
    private AiMessage() { }

    public Guid Id { get; private set; }
    public Guid ConversationId { get; private set; }

    /// <summary><c>user</c> or <c>assistant</c>.</summary>
    public string Role { get; private set; } = "user";

    public string Content { get; private set; } = string.Empty;
    public string? SourcesJson { get; private set; }
    public string? AttachmentsJson { get; private set; }

    /// <summary>The model could not be reached; the text says so and nothing was answered.</summary>
    public bool Failed { get; private set; }

    public string? Model { get; private set; }
    public int Tokens { get; private set; }

    /// <summary>The investigation this answer was saved as, when it was.</summary>
    public Guid? InquiryId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static AiMessage FromUser(Guid id, Guid conversationId, string text, DateTimeOffset now) =>
        new() { Id = id, ConversationId = conversationId, Role = "user", Content = text.Trim(), CreatedAt = now };

    public static AiMessage FromAssistant(
        Guid id, Guid conversationId, string text, string? sourcesJson, string? attachmentsJson,
        bool failed, string? model, int tokens, DateTimeOffset now) =>
        new()
        {
            Id = id, ConversationId = conversationId, Role = "assistant", Content = text,
            SourcesJson = sourcesJson, AttachmentsJson = attachmentsJson, Failed = failed,
            Model = model, Tokens = tokens, CreatedAt = now,
        };

    public void SavedAs(Guid inquiryId) => InquiryId = inquiryId;
}
