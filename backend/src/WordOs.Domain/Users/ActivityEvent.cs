using WordOs.Domain.Common;

namespace WordOs.Domain.Users;

/// <summary>What a learner did. One row per occurrence, never updated.</summary>
public enum ActivityType
{
    SignedIn,
    Registered,
    WordAdded,
    SessionStarted,
    SessionCompleted,
    PracticeStarted,
    PracticeCompleted,
    ReviewCompleted,
    PlacementCompleted,

    /// <summary>
    /// The Owner brought this learner's schedule forward, to demonstrate or
    /// test the spaced gaps without waiting two days for each one.
    /// </summary>
    /// <remarks>
    /// Recorded because it is the one event that makes the other figures lie:
    /// a pipeline completed in an afternoon looks like an extraordinary
    /// learner unless the log says the clock was moved.
    /// </remarks>
    ScheduleAdvanced,

    /// <summary>
    /// The learner sent the Owner a message (ADR-053).
    /// </summary>
    /// <remarks>
    /// The event records only that they wrote; the text itself lives in
    /// <see cref="FeedbackMessage"/>, because this log deliberately holds no
    /// free text from learners.
    /// </remarks>
    FeedbackSent,
}

/// <summary>
/// The activity log (Part 3 §34–§35).
/// </summary>
/// <remarks>
/// Analytics were being reconstructed from whatever happened to be durable —
/// "did they sign in that day?" was answered by looking for a completed session
/// or an added word, because <c>LastLoginAt</c> records one moment rather than a
/// history. That works until it does not: a learner who opened the app, read a
/// passage and left produced no evidence at all.
///
/// This is the missing durable trail. It is <b>append-only</b>: rows are written
/// as things happen and never edited or deleted, so a figure on the dashboard
/// can always be traced back to the events that produced it, and a changed
/// analytics query recomputes the same history rather than a different one.
///
/// It deliberately holds no free text from learners and nothing sensitive — a
/// type, a timestamp, and the id of whatever the event was about. What was said
/// in a session lives with the session, under the learner's own account.
/// </remarks>
public class ActivityEvent
{
    private ActivityEvent() { } // EF Core

    public long Id { get; private set; }

    public Guid UserId { get; private set; }

    public ActivityType Type { get; private set; }

    /// <summary>The skill this concerned, where one applies.</summary>
    public SkillType? Skill { get; private set; }

    /// <summary>The session, word or review this was about.</summary>
    public Guid? EntityId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static ActivityEvent Record(
        Guid userId,
        ActivityType type,
        DateTimeOffset now,
        SkillType? skill = null,
        Guid? entityId = null) =>
        new()
        {
            UserId = userId,
            Type = type,
            Skill = skill,
            EntityId = entityId,
            CreatedAt = now,
        };
}
