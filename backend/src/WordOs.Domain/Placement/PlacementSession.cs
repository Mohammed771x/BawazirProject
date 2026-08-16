using WordOs.Domain.Common;

namespace WordOs.Domain.Placement;

/// <summary>
/// One in-flight adaptive placement test.
/// </summary>
/// <remarks>
/// An adaptive test spans many HTTP requests, so the run has to be persisted:
/// the next question depends on every answer so far. Keeping it server-side is
/// also what stops a client replaying or reordering answers to manufacture a
/// level (rule R1).
/// </remarks>
public class PlacementSession
{
    private readonly List<PlacementAnswer> _answers = [];

    private PlacementSession() { } // EF Core

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid UserId { get; private set; }

    /// <summary>The item the learner is currently looking at.</summary>
    public string? CurrentItemId { get; private set; }

    public bool IsComplete { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>
    /// How many answers were scored by the offline fallback rather than the AI
    /// service, so analytics can flag placements that ran degraded.
    /// </summary>
    public int FallbackScoredCount { get; private set; }

    public IReadOnlyList<PlacementAnswer> Answers => _answers;

    public static PlacementSession Start(Guid userId, DateTimeOffset now) =>
        new() { UserId = userId, StartedAt = now };

    public IReadOnlyList<PlacementResponse> ToResponses() =>
        _answers
            .Select(a => new PlacementResponse(a.ItemId, a.Skill, a.Difficulty, a.Score))
            .ToList();

    public PlacementAnswer RecordAnswer(
        BankItem item,
        double difficulty,
        double score,
        DateTimeOffset now,
        bool scoredByFallback)
    {
        var answer = PlacementAnswer.Create(Id, item, difficulty, score, now);
        _answers.Add(answer);
        if (scoredByFallback) FallbackScoredCount++;
        return answer;
    }

    public void SetCurrentItem(string? itemId) => CurrentItemId = itemId;

    public void Complete(DateTimeOffset now)
    {
        IsComplete = true;
        CompletedAt = now;
        CurrentItemId = null;
    }
}

/// <summary>One answer inside a placement run.</summary>
public class PlacementAnswer
{
    private PlacementAnswer() { } // EF Core

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid SessionId { get; private set; }

    public string ItemId { get; private set; } = string.Empty;

    public SkillType Skill { get; private set; }

    /// <summary>Rasch difficulty in logits, resolved server-side.</summary>
    public double Difficulty { get; private set; }

    /// <summary>Partial credit in <c>[0, 1]</c>, computed server-side.</summary>
    public double Score { get; private set; }

    public DateTimeOffset AnsweredAt { get; private set; }

    internal static PlacementAnswer Create(
        Guid sessionId,
        BankItem item,
        double difficulty,
        double score,
        DateTimeOffset now) =>
        new()
        {
            SessionId = sessionId,
            ItemId = item.Id,
            Skill = item.Skill,
            Difficulty = difficulty,
            Score = score,
            AnsweredAt = now,
        };
}
