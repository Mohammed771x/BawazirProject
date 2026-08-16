using WordOs.Domain.Common;

namespace WordOs.Domain.Levels;

/// <summary>
/// One learner's standing in one skill.
/// </summary>
/// <remarks>
/// Rule R6: <see cref="UserSelectedLevel"/> is a content preference the learner
/// controls; <see cref="SystemAssessedLevel"/> is what performance has proven,
/// and only it may drive progression and archiving. They are deliberately
/// separate fields and are never conflated.
///
/// Both are <b>nullable</b>: Spelling is measured but carries no CEFR band
/// (ADR-008). Call sites branch on <see cref="CarriesCefrLevel"/>.
/// </remarks>
public class SkillLevel
{
    private SkillLevel() { } // EF Core

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid UserId { get; private set; }

    public SkillType Skill { get; private set; }

    public CefrLevel? UserSelectedLevel { get; private set; }

    public CefrLevel? SystemAssessedLevel { get; private set; }

    /// <summary>Placement precision, 0–1.</summary>
    public double Confidence { get; private set; }

    /// <summary>Sessions accumulated since the last level decision.</summary>
    public int EvaluationSessions { get; private set; }

    /// <summary>
    /// Sum of the per-session accuracies in the current window.
    /// </summary>
    /// <remarks>
    /// Stored as a sum rather than as an incrementally-updated mean. The
    /// incremental form <c>((mean * n) + x) / (n + 1)</c> accumulates
    /// floating-point drift, and a learner sitting exactly on the 85% threshold
    /// would then fail to promote — which a test caught. Summing and dividing
    /// once is exact for the counts involved.
    /// </remarks>
    public double AccuracySum { get; private set; }

    public double RollingAccuracy =>
        EvaluationSessions == 0 ? 0 : AccuracySum / EvaluationSessions;

    public int DailyTargetWords { get; private set; }

    /// <summary>Spelling only — which input affordance to start the learner on.</summary>
    public SpellingInputMode? SpellingSupportMode { get; private set; }

    public bool CarriesCefrLevel => Skill != SkillType.Spelling;

    public static SkillLevel Create(
        Guid userId,
        SkillType skill,
        WordOsConfiguration config,
        CefrLevel? initial = null)
    {
        var levelled = skill != SkillType.Spelling;
        return new SkillLevel
        {
            UserId = userId,
            Skill = skill,
            UserSelectedLevel = levelled ? initial : null,
            SystemAssessedLevel = levelled ? initial : null,
            DailyTargetWords = config.DefaultDailyTarget,
        };
    }

    /// <summary>
    /// A manual change moves <b>only</b> the user-selected level (rule R6). It
    /// can never archive a word or alter what the system has validated.
    /// </summary>
    public void SetUserSelectedLevel(CefrLevel level)
    {
        if (!CarriesCefrLevel)
            throw new InvalidOperationException(
                "Spelling is measured but does not carry a CEFR level.");
        UserSelectedLevel = level;
    }

    public void SetDailyTarget(int target, WordOsConfiguration config) =>
        DailyTargetWords = config.ClampDailyTarget(target);

    /// <summary>Seeds both levels from placement (ADR-007).</summary>
    public void ApplyPlacement(CefrLevel? level, double confidence, double accuracy)
    {
        if (CarriesCefrLevel)
        {
            UserSelectedLevel = level;
            SystemAssessedLevel = level;
        }

        Confidence = confidence;
        AccuracySum = 0;
        EvaluationSessions = 0;
    }

    public void SetSpellingSupportMode(SpellingInputMode mode) =>
        SpellingSupportMode = mode;

    /// <summary>Folds one completed session into the evaluation window.</summary>
    public void RecordSession(double accuracy)
    {
        AccuracySum += Math.Clamp(accuracy, 0, 1);
        EvaluationSessions++;
    }

    /// <summary>
    /// Moves the system-validated level and resets the evidence window.
    /// </summary>
    /// <remarks>
    /// The reset matters: without it a learner sitting at 90% would be promoted
    /// again on the next session against evidence already spent (ADR-013).
    /// </remarks>
    internal void ApplyValidatedLevel(CefrLevel? level)
    {
        if (level is not null) SystemAssessedLevel = level;
        EvaluationSessions = 0;
        AccuracySum = 0;
    }
}
