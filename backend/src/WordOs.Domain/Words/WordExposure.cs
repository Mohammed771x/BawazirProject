namespace WordOs.Domain.Words;

/// <summary>Where an exposure came from.</summary>
public enum ExposureSource
{
    /// <summary>
    /// The word was reused inside AI-generated learning content — the primary
    /// source, and the reason Active vocabulary keeps circulating at all
    /// (<c>Word Life Cycle.txt</c> §24–26).
    /// </summary>
    AiContentReuse,

    /// <summary>The word came up in a weekly review.</summary>
    WeeklyReview,
}

/// <summary>
/// One recorded exposure, and the reason the count cannot drift.
/// </summary>
/// <remarks>
/// Exposure is a priority signal — never a limit, never a delete trigger
/// (rule R8). Because it feeds archiving decisions, an inflated count is not
/// harmless: it can retire a word the learner has barely met.
///
/// So exposure is an <b>event</b>, not a number the client may report:
///
/// <list type="bullet">
/// <item>every increment is a row naming its source and the thing that caused
/// it (<see cref="SourceId"/> — a session or a review);</item>
/// <item>a unique index over
/// (<see cref="WordId"/>, <see cref="Source"/>, <see cref="SourceId"/>) makes a
/// second increment from the same cause impossible, so a word appearing three
/// times in one passage, or across several turns of one conversation, still
/// counts once;</item>
/// <item>no endpoint accepts an exposure count. The server decides, by reading
/// content it generated itself.</item>
/// </list>
///
/// A <i>new</i> session is genuinely a new exposure, and counts again — that is
/// the point of the signal.
/// </remarks>
public class WordExposure
{
    private WordExposure() { } // EF Core

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid WordId { get; private set; }

    public ExposureSource Source { get; private set; }

    /// <summary>The session or review that caused it.</summary>
    public Guid SourceId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public static WordExposure Record(
        Guid wordId,
        ExposureSource source,
        Guid sourceId,
        DateTimeOffset now) =>
        new()
        {
            WordId = wordId,
            Source = source,
            SourceId = sourceId,
            OccurredAt = now,
        };
}
