using WordOs.Domain.Common;

namespace WordOs.Domain.Common;

/// <summary>
/// Every tunable of the WordOS algorithm, in one place.
/// </summary>
/// <remarks>
/// Rule R3: nothing tunable is hard-coded. In production these values are
/// seeded into the <c>configurations</c> table and loaded at startup; the
/// defaults here are the ones documented in <c>docs/00-PROJECT-PLAN.md</c> §5
/// and exist so the domain is testable without a database.
/// </remarks>
public sealed record WordOsConfiguration
{
    /// <summary>Days between one skill passing and the next becoming available.</summary>
    public int SkillIntervalDays { get; init; } = 2;

    public int MinDailyTarget { get; init; } = 5;

    public int MaxDailyTarget { get; init; } = 15;

    public int DefaultDailyTarget { get; init; } = 10;

    public int WeeklyReviewPeriodDays { get; init; } = 7;

    /// <summary>
    /// The pipeline order. Configurable per ADR-001; the product owner
    /// confirmed <c>Speaking → Writing</c> on 2026-08-15.
    /// </summary>
    public IReadOnlyList<SkillType> SkillsOrder { get; init; } =
    [
        SkillType.Reading,
        SkillType.Listening,
        SkillType.Speaking,
        SkillType.Writing,
        SkillType.Spelling,
    ];

    /// <summary>Sessions of evidence needed before a level may move at all.</summary>
    public int MinEvaluationSessions { get; init; } = 14;

    /// <summary><c>MVP Core.txt</c> §23 — a strong indicator of mastery.</summary>
    public double PromoteThreshold { get; init; } = 0.85;

    /// <summary><c>MVP Core.txt</c> §22 — below this the content is too hard.</summary>
    public double DemoteThreshold { get; init; } = 0.70;

    /// <summary>
    /// Ladder steps a word must sit below the proven level before it is an
    /// archive candidate. Four steps is two full CEFR bands (ADR-013).
    /// </summary>
    public int ArchiveLevelGapSteps { get; init; } = 4;

    /// <summary>
    /// Exposure floor for archiving. Exposure is a priority signal, never a
    /// limit or a delete trigger (rule R8) — it appears here only so that a
    /// word nobody has actually met in content is not retired.
    /// </summary>
    public int ArchiveMinExposure { get; init; } = 3;

    /// <summary>How many times one item may be asked within a single session.</summary>
    public int MaxAttemptsPerItem { get; init; } = 3;

    /// <summary>Comprehension questions per Reading/Listening session.</summary>
    public int ComprehensionQuestionCount { get; init; } = 5;

    /// <summary>
    /// How many Active words are offered to the generator per session.
    /// </summary>
    /// <remarks>
    /// The least-exposed words go first (rule R8: exposure prioritises, it never
    /// limits). Kept small on purpose — a passage stuffed with old vocabulary
    /// stops being a passage, and the target words are what the session is
    /// actually for.
    /// </remarks>
    public int ActiveReuseWordsPerSession { get; init; } = 3;

    public SkillType? NextSkillAfter(SkillType skill)
    {
        var index = SkillsOrder.ToList().IndexOf(skill);
        if (index < 0 || index >= SkillsOrder.Count - 1) return null;
        return SkillsOrder[index + 1];
    }

    public SkillType FirstSkill => SkillsOrder[0];

    public int ClampDailyTarget(int target) =>
        Math.Clamp(target, MinDailyTarget, MaxDailyTarget);
}
