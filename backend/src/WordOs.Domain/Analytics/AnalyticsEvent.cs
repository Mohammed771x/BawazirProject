using WordOs.Domain.Common;

namespace WordOs.Domain.Analytics;

/// <summary>Who wrote the event: the phone, or the server itself.</summary>
public enum AnalyticsSource
{
    Client,
    Server,
}

/// <summary>
/// One thing that happened, at the grain nothing else records (ADR-125).
/// </summary>
/// <remarks>
/// The schema already holds a great deal of history — sessions, their items,
/// word events, level changes, weekly reviews, feedback — and none of that is
/// copied here. Rule §32 of the admin brief is the same as this codebase's
/// habit: one fact is stored once, and every chart is a query over it.
///
/// What lives here is what had nowhere else to go:
///
/// * <b>attempt history</b> — a session item keeps its attempt count and its
///   last answer, so the second of three wrong answers, and how long each one
///   took, was gone the moment the third arrived;
/// * <b>why an AI judgement failed</b> — Writing's and Speaking's observations
///   were returned to the phone and never kept, so "Speaking failed" could not
///   be told apart from "never used the word";
/// * <b>what the learner did on the screen</b> — opening a translation,
///   replaying a clip, asking for a hint, leaving mid-exercise. Only the phone
///   sees these.
///
/// Append-only, like <see cref="Users.ActivityEvent"/>, and for the same
/// reason: a figure on the dashboard must be traceable to the rows behind it.
/// It carries no free text the learner typed — <see cref="PropsJson"/> is
/// bounded and holds flags, counts and identifiers.
/// </remarks>
public class AnalyticsEvent
{
    private AnalyticsEvent() { } // EF Core

    /// <summary>Longest props document accepted, in characters.</summary>
    public const int MaxPropsLength = 2048;

    public long Id { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>A name from <see cref="AnalyticsEventNames"/>, snake_case.</summary>
    public string Name { get; private set; } = string.Empty;

    public AnalyticsSource Source { get; private set; }

    /// <summary>When it happened, by the clock of whoever saw it.</summary>
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>When the server stored it — the phone may batch, or be offline.</summary>
    public DateTimeOffset ReceivedAt { get; private set; }

    /// <summary>
    /// The phone's own id for one foreground stretch of the app.
    /// </summary>
    /// <remarks>
    /// Not a skill session: "app session" is what App Time is measured over,
    /// and one of them can hold three lessons or none.
    /// </remarks>
    public string? AppSessionId { get; private set; }

    /// <summary>The skill session or weekly review this was part of.</summary>
    public Guid? SessionId { get; private set; }

    public Guid? WordId { get; private set; }

    public SkillType? Skill { get; private set; }

    /// <summary>The attempt this was, 1-based, where attempts apply.</summary>
    public int? Attempt { get; private set; }

    /// <summary><c>pass</c>, <c>fail</c>, <c>ok</c>, <c>error</c> — short and closed.</summary>
    public string? Result { get; private set; }

    public int? DurationMs { get; private set; }

    /// <summary>The content level in use, wire form (<c>B1_PLUS</c>).</summary>
    public string? ContentLevel { get; private set; }

    public string? Screen { get; private set; }

    public string? AppVersion { get; private set; }

    public string? Platform { get; private set; }

    /// <summary>Flags and counts specific to this event. JSON object, bounded.</summary>
    public string? PropsJson { get; private set; }

    public static AnalyticsEvent Create(
        Guid userId,
        string name,
        AnalyticsSource source,
        DateTimeOffset occurredAt,
        DateTimeOffset receivedAt,
        string? appSessionId = null,
        Guid? sessionId = null,
        Guid? wordId = null,
        SkillType? skill = null,
        int? attempt = null,
        string? result = null,
        int? durationMs = null,
        string? contentLevel = null,
        string? screen = null,
        string? appVersion = null,
        string? platform = null,
        string? propsJson = null)
    {
        if (!AnalyticsEventNames.IsKnown(name))
            throw new ArgumentException($"Unknown analytics event '{name}'.", nameof(name));

        return new AnalyticsEvent
        {
            UserId = userId,
            Name = name,
            Source = source,
            OccurredAt = occurredAt,
            ReceivedAt = receivedAt,
            AppSessionId = Bounded(appSessionId, 64),
            SessionId = sessionId,
            WordId = wordId,
            Skill = skill,
            Attempt = attempt is > 0 ? attempt : null,
            Result = Bounded(result, 16),
            // A negative or absurd duration is a clock that jumped, not a
            // learner who spent a week on one question.
            DurationMs = durationMs is >= 0 and <= 6 * 60 * 60 * 1000 ? durationMs : null,
            ContentLevel = Bounded(contentLevel, 8),
            Screen = Bounded(screen, 48),
            AppVersion = Bounded(appVersion, 32),
            Platform = Bounded(platform, 16),
            PropsJson = propsJson is { Length: > 0 and <= MaxPropsLength } ? propsJson : null,
        };
    }

    private static string? Bounded(string? value, int max)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= max ? text : text[..max];
    }
}

/// <summary>
/// The closed list of event names (admin brief §31).
/// </summary>
/// <remarks>
/// Closed on purpose. An open string is how an events table fills with
/// <c>translationOpened</c>, <c>translation_open</c> and <c>tr_open</c>, each
/// a third of the truth. Events the schema already records elsewhere —
/// <c>word_added</c>, <c>skill_passed</c>, <c>level_changed</c>,
/// <c>weekly_review_completed</c> — are deliberately absent: the admin API
/// reads them from where they already live (§32).
/// </remarks>
public static class AnalyticsEventNames
{
    // ── Written by the phone ────────────────────────────────────────────────
    public const string AppOpened = "app_opened";
    public const string AppBackgrounded = "app_backgrounded";
    public const string ScreenViewed = "screen_viewed";
    public const string ScreenLeft = "screen_left";
    public const string TranslationOpened = "translation_opened";
    public const string AudioPlayed = "audio_played";
    public const string AudioReplayed = "audio_replayed";
    public const string AudioPaused = "audio_paused";
    public const string AudioCompleted = "audio_completed";
    public const string HintUsed = "hint_used";
    public const string FeedbackViewed = "feedback_viewed";
    public const string ExerciseExited = "exercise_exited";
    public const string NotificationOpened = "notification_opened";
    public const string ApiError = "api_error";
    public const string EmptyStateShown = "empty_state_shown";

    // ── Written by the server ───────────────────────────────────────────────
    public const string AnswerSubmitted = "answer_submitted";
    public const string WritingEvaluated = "writing_evaluated";
    public const string SpeakingTurn = "speaking_turn";
    public const string SpeakingWordEvaluated = "speaking_word_evaluated";
    public const string ReviewAnswered = "review_answered";
    public const string AiCall = "ai_call";

    /// <summary>
    /// A session the learner walked away from. Written before the row is
    /// removed — abandoning deletes the session, so without this the only
    /// trace of an abandoned exercise was its absence.
    /// </summary>
    public const string SessionAbandoned = "session_abandoned";

    /// <summary>What a phone is allowed to send. Server events are not on it.</summary>
    public static readonly IReadOnlySet<string> ClientNames = new HashSet<string>
    {
        AppOpened, AppBackgrounded, ScreenViewed, ScreenLeft,
        TranslationOpened, AudioPlayed, AudioReplayed, AudioPaused, AudioCompleted,
        HintUsed, FeedbackViewed, ExerciseExited, NotificationOpened,
        ApiError, EmptyStateShown,
    };

    public static readonly IReadOnlySet<string> ServerNames = new HashSet<string>
    {
        AnswerSubmitted, WritingEvaluated, SpeakingTurn, SpeakingWordEvaluated,
        ReviewAnswered, AiCall, SessionAbandoned,
    };

    public static bool IsKnown(string name) =>
        ClientNames.Contains(name) || ServerNames.Contains(name);
}
