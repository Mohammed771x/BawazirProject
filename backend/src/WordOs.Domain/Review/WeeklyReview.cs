using WordOs.Domain.Common;

namespace WordOs.Domain.Review;

/// <summary>
/// The weekly review: a measurement, and nothing else.
/// </summary>
/// <remarks>
/// Rule R9 is the whole design constraint. This aggregate deliberately has no
/// way to touch a word's skill status, schedule, state or level — the only
/// thing it produces is a score. A learner who does badly here loses nothing;
/// a learner who does well gains nothing but the number.
///
/// It is scored on <b>first attempts only</b>, while wrong answers still return
/// to the end of the queue so the learner leaves having seen the right meaning
/// (demo review §48: wrong is never discarded).
/// </remarks>
public class WeeklyReview
{
    private readonly List<WeeklyReviewItem> _items = [];

    private WeeklyReview() { } // EF Core

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid UserId { get; private set; }

    public DateTimeOffset PeriodStart { get; private set; }

    public DateTimeOffset PeriodEnd { get; private set; }

    public int TotalWords { get; private set; }

    public int FirstPassCorrect { get; private set; }

    public double WeeklyScore { get; private set; }

    public int TotalAttempts { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsComplete { get; private set; }

    public Guid? CurrentItemId { get; private set; }

    /// <summary>
    /// A practice round over a finished review's words, not the review itself
    /// (ADR-120).
    /// </summary>
    /// <remarks>
    /// The learner asked to go over the week's words again after the challenge
    /// had closed. A practice is played exactly like a review, and is recorded
    /// as nothing: it marks no word reviewed, counts no exposure, logs no
    /// completed review, and its score is shown and never reported. The weekly
    /// review stays one measurement a week (rule R9).
    /// </remarks>
    public bool IsPractice { get; private set; }

    public IReadOnlyList<WeeklyReviewItem> Items => _items;

    public static WeeklyReview Start(
        Guid userId,
        DateTimeOffset periodStart,
        DateTimeOffset now) =>
        new()
        {
            UserId = userId,
            PeriodStart = periodStart,
            PeriodEnd = now,
            StartedAt = now,
        };

    /// <summary>A practice round over <paramref name="of"/>'s words (ADR-120).</summary>
    public static WeeklyReview StartPractice(
        Guid userId,
        WeeklyReview of,
        DateTimeOffset now) =>
        new()
        {
            UserId = userId,
            PeriodStart = of.PeriodStart,
            PeriodEnd = of.PeriodEnd,
            StartedAt = now,
            IsPractice = true,
        };

    public WeeklyReviewItem AddItem(WeeklyReviewItem item)
    {
        item.AttachTo(Id, _items.Count);
        _items.Add(item);
        TotalWords = _items.Count;
        CurrentItemId ??= item.Id;
        return item;
    }

    public IReadOnlyList<WeeklyReviewItem> Queue =>
        _items.Where(i => !i.IsCleared)
            .OrderBy(i => i.RequeuedAt ?? int.MinValue)
            .ThenBy(i => i.Position)
            .ToList();

    public int ClearedCount => _items.Count(i => i.IsCleared);

    /// <summary>
    /// Records one answer.
    /// </summary>
    /// <returns>True when the item returned to the queue.</returns>
    public bool RecordAttempt(WeeklyReviewItem item, bool isCorrect, int maxAttempts)
    {
        TotalAttempts++;
        var requeued = item.RecordAttempt(isCorrect, maxAttempts, TotalAttempts);
        CurrentItemId = Queue.FirstOrDefault()?.Id;
        return requeued;
    }

    public void Complete(DateTimeOffset now)
    {
        FirstPassCorrect = _items.Count(i => i.FirstAttemptCorrect == true);
        // Only first attempts count: a word rescued on the third try was not
        // remembered, and the score exists to say so honestly (R9).
        WeeklyScore = _items.Count == 0
            ? 0
            : (double)FirstPassCorrect / _items.Count;

        IsComplete = true;
        CompletedAt = now;
        CurrentItemId = null;
    }
}

public class WeeklyReviewItem
{
    private WeeklyReviewItem() { } // EF Core

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid ReviewId { get; private set; }

    public int Position { get; private set; }

    public Guid WordId { get; private set; }

    public string Prompt { get; private set; } = string.Empty;

    /// <summary>Shuffled server-side (rule R7).</summary>
    public string OptionsJson { get; private set; } = "[]";

    /// <summary>Never sent to the client before the answer arrives.</summary>
    public string CorrectAnswer { get; private set; } = string.Empty;

    public int Attempts { get; private set; }

    public bool? FirstAttemptCorrect { get; private set; }

    public bool IsCleared { get; private set; }

    public int? RequeuedAt { get; private set; }

    public DateTimeOffset? AnsweredAt { get; private set; }

    public static WeeklyReviewItem Create(
        Guid wordId,
        string prompt,
        IReadOnlyList<string> options,
        string correct) =>
        new()
        {
            WordId = wordId,
            Prompt = prompt,
            OptionsJson = System.Text.Json.JsonSerializer.Serialize(options),
            CorrectAnswer = correct,
        };

    internal void AttachTo(Guid reviewId, int position)
    {
        ReviewId = reviewId;
        Position = position;
    }

    internal bool RecordAttempt(bool isCorrect, int maxAttempts, int sequence)
    {
        Attempts++;
        FirstAttemptCorrect ??= isCorrect;

        if (isCorrect || Attempts >= maxAttempts)
        {
            IsCleared = true;
            return false;
        }

        RequeuedAt = sequence;
        return true;
    }

    public void MarkAnswered(DateTimeOffset now) => AnsweredAt = now;
}
