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

    /// <summary>
    /// Which version of the placement content and algorithm produced this run.
    /// </summary>
    /// <remarks>
    /// Stored so a historical result stays interpretable after the test changes
    /// (§27). Without it, comparing a learner placed under v1 with one placed
    /// under v2 silently compares two different instruments.
    /// </remarks>
    public int TestVersion { get; private set; } = PlacementVersion.Current;

    public IReadOnlyList<PlacementAnswer> Answers => _answers;

    public static PlacementSession Start(Guid userId, DateTimeOffset now) =>
        new() { UserId = userId, StartedAt = now };

    /// <summary>
    /// The evidence the ability estimator sees.
    /// </summary>
    /// <remarks>
    /// A grammar item appears <b>twice</b> — once for the skill it scores into
    /// and once for the skill it also supports (§19). Speaking is judged from
    /// very few prompts, and grammar is the clearest cross-cutting signal of
    /// production ability, so letting it inform both is what makes those
    /// estimates defensible rather than a guess from two answers.
    /// </remarks>
    public IReadOnlyList<PlacementResponse> ToResponses()
    {
        var responses = new List<PlacementResponse>();

        foreach (var answer in _answers)
        {
            responses.Add(new PlacementResponse(
                answer.ItemId, answer.Skill, answer.Difficulty, answer.Score));

            if (answer.AlsoEvidenceFor is { } second)
            {
                responses.Add(new PlacementResponse(
                    $"{answer.ItemId}:{second.ToWire()}",
                    second,
                    answer.Difficulty,
                    answer.Score));
            }
        }

        return responses;
    }

    public PlacementAnswer RecordAnswer(
        BankItem item,
        double difficulty,
        double score,
        DateTimeOffset now,
        bool scoredByFallback,
        string? rawAnswer = null,
        string? evaluationJson = null)
    {
        var answer = PlacementAnswer.Create(
            Id, item, difficulty, score, now, rawAnswer, evaluationJson);
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

    /// <summary>The CEFR band the item was authored for.</summary>
    public CefrLevel Level { get; private set; }

    /// <summary>What the item measures — grammar and spelling included.</summary>
    public PlacementDomain Domain { get; private set; }

    /// <summary>A second skill this answer is evidence for, if any.</summary>
    public SkillType? AlsoEvidenceFor { get; private set; }

    /// <summary>The band the AI thought this answer showed, if it rated it.</summary>
    public CefrLevel? AiEstimatedLevel { get; private set; }

    /// <summary>
    /// What the learner actually said or wrote.
    /// </summary>
    /// <remarks>
    /// Kept for the analytics layer (§26): a level on its own cannot be
    /// re-examined, and the transcript of a spoken answer is the most useful
    /// record this test produces. Never returned to the learner.
    /// </remarks>
    public string? RawAnswer { get; private set; }

    /// <summary>The AI evaluation, as returned, for free responses.</summary>
    public string? EvaluationJson { get; private set; }

    /// <summary>
    /// Replaces the offline score with the AI's judgement of the same answer.
    /// </summary>
    /// <remarks>
    /// Only Speaking and Writing reach this: they have no answer key, so the
    /// score recorded during the test was a length-and-variety heuristic
    /// standing in until the whole set could be read together.
    ///
    /// The evidence is kept alongside, because a band that surprises the Owner
    /// should be answerable with what the learner actually said.
    /// </remarks>
    public void ApplyAiRating(double score, CefrLevel? estimatedLevel, string evidence)
    {
        Score = Math.Clamp(score, 0, 1);
        AiEstimatedLevel = estimatedLevel;
        EvaluationJson = evidence;
    }

    internal static PlacementAnswer Create(
        Guid sessionId,
        BankItem item,
        double difficulty,
        double score,
        DateTimeOffset now,
        string? rawAnswer,
        string? evaluationJson) =>
        new()
        {
            SessionId = sessionId,
            ItemId = item.Id,
            Skill = item.Skill,
            Level = item.Level,
            Domain = item.MeasuredDomain,
            AlsoEvidenceFor = item.AlsoEvidenceFor,
            Difficulty = difficulty,
            Score = score,
            AnsweredAt = now,
            // Only production items carry a raw answer; storing the letter a
            // learner tapped on a multiple-choice item is noise.
            RawAnswer = item.IsFreeText ? rawAnswer : null,
            EvaluationJson = evaluationJson,
        };
}

/// <summary>
/// The version of the placement instrument.
/// </summary>
/// <remarks>
/// Bumped whenever the item bank or the scoring changes in a way that makes old
/// results incomparable — which is the whole reason it is recorded (§27).
///
/// v2 (2026-08-17): Speaking answered by voice rather than typed, nine grammar
/// items added as evidence for Speaking and Writing, per-answer evidence stored.
/// </remarks>
public static class PlacementVersion
{
    public const int Current = 2;
}
