using WordOs.Domain.Common;
using WordOs.Domain.Words;

namespace WordOs.Domain.Levels;

/// <summary>
/// The system-validated level policy and the archiving rule that depends on it.
/// </summary>
/// <remarks>
/// Ported from the behavioural specification in
/// <c>mobile/test/level_progression_test.dart</c>. Sources:
/// <c>MVP Core.txt</c> §22–23 (thresholds), <c>Word Life Cycle.txt</c> §27–31
/// (archiving), ADR-013 (the judgement calls the documents left open).
/// </remarks>
public sealed class LevelEngine(WordOsConfiguration config)
{
    private readonly WordOsConfiguration _config = config;

    /// <summary>
    /// Tolerance for threshold comparisons.
    /// </summary>
    /// <remarks>
    /// Accuracy is a mean of binary-fraction values, so a learner who scores
    /// exactly the threshold every session lands a few ulps below it — 0.85 is
    /// not representable, and twenty of them sum to 16.999999999999996. Without
    /// this tolerance such a learner would never be promoted, which a test
    /// caught. The epsilon is far smaller than any meaningful difference in
    /// accuracy.
    /// </remarks>
    private const double ThresholdEpsilon = 1e-9;

    /// <summary>
    /// Decides what should happen to one skill's system-validated level.
    /// Returns null when there is not yet enough evidence to decide anything —
    /// which is the common case, and deliberately so.
    /// </summary>
    public LevelDecision? Evaluate(SkillLevel level)
    {
        // Spelling is measured but carries no CEFR band, so there is nothing to
        // promote or demote (ADR-008).
        if (!level.CarriesCefrLevel) return null;

        var current = level.SystemAssessedLevel;
        if (current is null) return null;

        // "لا يتم تغيير مستوى المستخدم بناءً على سؤال واحد" — a level never moves
        // on one session; the decision needs an accumulated window.
        if (level.EvaluationSessions < _config.MinEvaluationSessions) return null;

        var accuracy = level.RollingAccuracy;

        if (accuracy >= _config.PromoteThreshold - ThresholdEpsilon)
        {
            var next = current.Value.Step(1);
            return next is null
                // Already at the top of the ladder: hold, but spend the window.
                ? LevelDecision.Hold(level.Skill, accuracy)
                : new LevelDecision(
                    level.Skill,
                    current,
                    next,
                    accuracy,
                    level.EvaluationSessions,
                    LevelChangeReason.Promoted);
        }

        if (accuracy < _config.DemoteThreshold - ThresholdEpsilon)
        {
            var next = current.Value.Step(-1);
            return next is null
                ? LevelDecision.Hold(level.Skill, accuracy)
                : new LevelDecision(
                    level.Skill,
                    current,
                    next,
                    accuracy,
                    level.EvaluationSessions,
                    LevelChangeReason.Demoted);
        }

        // Between the thresholds the level is right where it should be.
        return LevelDecision.Hold(level.Skill, accuracy);
    }

    /// <summary>Applies a decision, resetting the evidence window.</summary>
    public void Apply(SkillLevel level, LevelDecision decision) =>
        level.ApplyValidatedLevel(decision.Next);

    /// <summary>
    /// The level the system has <i>proven</i> across the CEFR skills: the
    /// <b>minimum</b>, not the average.
    /// </summary>
    /// <remarks>
    /// Archiving removes a word from active rotation, so it follows the weakest
    /// evidence rather than a figure flattered by one strong skill. A learner
    /// who reads at C1 but listens at A2 has not outgrown A2 vocabulary
    /// (ADR-013).
    /// </remarks>
    public CefrLevel? SystemValidatedLevel(IEnumerable<SkillLevel> levels)
    {
        CefrLevel? lowest = null;
        foreach (var level in levels)
        {
            var assessed = level.SystemAssessedLevel;
            if (assessed is null) continue; // Spelling carries none.
            if (lowest is null || assessed.Value.Rank() < lowest.Value.Rank())
                lowest = assessed;
        }
        return lowest;
    }

    /// <summary>
    /// Whether a word may be archived, given the learner's proven level.
    /// All three conditions are required.
    /// </summary>
    public bool ShouldArchive(
        CefrLevel wordLevel,
        WordState state,
        int exposureCount,
        CefrLevel? systemValidatedLevel)
    {
        // 1. Finished the pipeline. A word still being learned is never archived.
        if (state != WordState.Active) return false;
        if (systemValidatedLevel is null) return false;

        // 2. Far enough below the *system-validated* level — never the
        //    user-selected one (Word Life Cycle §28).
        var gap = systemValidatedLevel.Value.Rank() - wordLevel.Rank();
        if (gap < _config.ArchiveLevelGapSteps) return false;

        // 3. Established, not merely easy (§30).
        return exposureCount >= _config.ArchiveMinExposure;
    }

    /// <summary>
    /// Archives every Active word the learner has visibly outgrown. Only ever
    /// called after a <b>promotion</b>: a demotion must not archive anything.
    /// </summary>
    public IReadOnlyList<Word> ArchiveOutgrown(
        IEnumerable<Word> words,
        IEnumerable<SkillLevel> levels,
        DateTimeOffset now)
    {
        var proven = SystemValidatedLevel(levels);
        if (proven is null) return [];

        var archived = new List<Word>();
        foreach (var word in words)
        {
            if (!ShouldArchive(word.CefrLevel, word.State, word.ExposureCount, proven))
                continue;
            word.Archive(now);
            archived.Add(word);
        }
        return archived;
    }
}

public enum LevelChangeReason
{
    Promoted,
    Demoted,
    Held,
}

/// <summary>The outcome of evaluating one skill.</summary>
public sealed record LevelDecision(
    SkillType Skill,
    CefrLevel? Previous,
    CefrLevel? Next,
    double Accuracy,
    int SessionsConsidered,
    LevelChangeReason Reason)
{
    /// <summary>No movement, but the evaluation window is spent and resets.</summary>
    public static LevelDecision Hold(SkillType skill, double accuracy) =>
        new(skill, null, null, accuracy, 0, LevelChangeReason.Held);

    public bool Moved => Reason != LevelChangeReason.Held;

    public bool IsPromotion => Reason == LevelChangeReason.Promoted;
}
