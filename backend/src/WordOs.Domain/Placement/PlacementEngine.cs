using WordOs.Domain.Common;

namespace WordOs.Domain.Placement;

/// <summary>
/// Drives one adaptive placement test.
/// </summary>
/// <remarks>
/// Shape of a run:
/// <code>
/// for each CEFR skill (Reading, Listening, Speaking, Writing):
///     ask the item whose difficulty is closest to the current ability estimate
///     re-estimate ability by EAP after every answer
///     stop when the posterior SE is small enough, or the item cap is reached
/// then Spelling: a short fixed ladder, measured but never levelled
/// </code>
///
/// The engine is <b>stateless</b>: it is handed the responses so far and
/// returns the next decision. Persistence lives in the API layer, which is what
/// lets a run survive across HTTP requests without the engine knowing about a
/// database. Full method in <c>docs/06-PLACEMENT-ALGORITHM.md</c>.
/// </remarks>
public sealed class PlacementEngine(
    PlacementConfig? config = null,
    IFreeResponseScorer? scorer = null)
{
    public PlacementConfig Config { get; } = config ?? new PlacementConfig();

    private readonly IFreeResponseScorer _scorer =
        scorer ?? new HeuristicFreeResponseScorer();

    private AbilityEstimator Estimator => new(Config.Scale);

    /// <summary>Scores one answer against the item it was given for.</summary>
    public double ScoreAnswer(BankItem item, string answer) =>
        item.IsFreeText
            ? _scorer.Score(item, answer)
            : string.Equals(answer, item.CorrectAnswer, StringComparison.Ordinal)
                ? 1
                : 0;

    /// <summary>
    /// Chooses the next item, given everything answered so far.
    /// </summary>
    /// <remarks>
    /// Walks the skills in order, moving on when the current one has been
    /// measured precisely enough or has run out of items. Returns null when the
    /// whole test is finished.
    /// </remarks>
    public BankItem? NextItem(
        IReadOnlyList<PlacementResponse> responses,
        Random random)
    {
        var asked = responses.Select(r => r.ItemId).ToHashSet(StringComparer.Ordinal);

        foreach (var skill in Config.SkillOrder)
        {
            var forSkill = responses
                .Where(r => r.Skill == skill)
                .Select(r => new ScoredResponse(r.ItemId, r.Difficulty, r.Score))
                .ToList();

            var limits = Config.LimitsFor(skill);
            if (forSkill.Count >= limits.MaxItems) continue;

            if (forSkill.Count >= limits.MinItems)
            {
                var estimate = Estimator.Estimate(forSkill);
                // Confident enough — spend the remaining questions elsewhere.
                if (estimate.StandardError <= limits.TargetStandardError) continue;
            }

            var pool = PlacementItemBank.ForSkill(skill)
                .Where(i => !asked.Contains(i.Id))
                .ToList();
            if (pool.Count == 0) continue;

            // Spelling is not adaptive: it walks a short fixed ladder so the
            // accuracy figure is comparable between learners (ADR-008).
            if (skill == SkillType.Spelling) return pool[0];

            var theta = forSkill.Count == 0
                ? Config.Scale.PriorMean
                : Estimator.Estimate(forSkill).Theta;

            // Maximum Fisher information for a Rasch item is at
            // difficulty == ability, so "closest difficulty" *is* the optimal
            // choice under this model. Ties break at random for exposure
            // control, so two learners of the same level do not always see an
            // identical test.
            var ranked = pool
                .OrderBy(i => Math.Abs(Config.Scale.DifficultyOf(i.Level) - theta))
                .ToList();

            var best = Math.Abs(Config.Scale.DifficultyOf(ranked[0].Level) - theta);
            var tied = ranked
                .Where(i => Math.Abs(
                    Math.Abs(Config.Scale.DifficultyOf(i.Level) - theta) - best) < 1e-9)
                .ToList();

            return tied[random.Next(tied.Count)];
        }

        return null;
    }

    /// <summary>Computes the final per-skill levels and the spelling diagnostic.</summary>
    public PlacementOutcome Complete(IReadOnlyList<PlacementResponse> responses)
    {
        var levels = new List<PlacementSkillOutcome>();

        foreach (var skill in Config.SkillOrder)
        {
            var forSkill = responses.Where(r => r.Skill == skill).ToList();
            var accuracy = forSkill.Count == 0
                ? 0
                : forSkill.Average(r => r.Score);

            if (skill == SkillType.Spelling)
            {
                levels.Add(new PlacementSkillOutcome(
                    skill, Level: null, Confidence: forSkill.Count == 0 ? 0 : 1,
                    Accuracy: accuracy));
                continue;
            }

            var estimate = Estimator.Estimate(forSkill
                .Select(r => new ScoredResponse(r.ItemId, r.Difficulty, r.Score))
                .ToList());

            levels.Add(new PlacementSkillOutcome(
                skill,
                Level: Config.Scale.LevelFor(estimate.Theta),
                Confidence: Config.Scale.ConfidenceFor(estimate.StandardError),
                Accuracy: accuracy));
        }

        var spelling = responses.Where(r => r.Skill == SkillType.Spelling).ToList();
        var spellingCorrect = spelling.Count(r => r.Score >= 0.999);
        var spellingAccuracy = spelling.Count == 0
            ? 0
            : (double)spellingCorrect / spelling.Count;

        return new PlacementOutcome(
            Levels: levels,
            SpellingItemsAnswered: spelling.Count,
            SpellingCorrect: spellingCorrect,
            // Weak spellers start with letter tiles; confident ones type
            // freely. Spelling sessions can move a learner between modes later
            // — this is only the starting affordance (ADR-008).
            SpellingSupportMode: spellingAccuracy >= Config.FreeTypingThreshold
                ? SpellingInputMode.FreeTyping
                : SpellingInputMode.LetterTiles);
    }
}

/// <summary>One recorded answer within a run.</summary>
public sealed record PlacementResponse(
    string ItemId,
    SkillType Skill,
    double Difficulty,
    double Score);

public sealed record PlacementSkillOutcome(
    SkillType Skill,
    CefrLevel? Level,
    double Confidence,
    double Accuracy);

public sealed record PlacementOutcome(
    IReadOnlyList<PlacementSkillOutcome> Levels,
    int SpellingItemsAnswered,
    int SpellingCorrect,
    SpellingInputMode SpellingSupportMode)
{
    /// <summary>
    /// True when at least one skill was placed with low confidence, in which
    /// case the UI tells the learner the level is provisional rather than
    /// pretending to a precision the test did not reach.
    /// </summary>
    public bool HasLowConfidence =>
        Levels.Any(l => l.Level is not null && l.Confidence < 0.5);
}

/// <summary>Every tunable of the placement algorithm, in one place (rule R3).</summary>
public sealed record PlacementConfig
{
    public AbilityScale Scale { get; init; } = new();

    public IReadOnlyList<SkillType> SkillOrder { get; init; } =
    [
        SkillType.Reading,
        SkillType.Listening,
        SkillType.Speaking,
        SkillType.Writing,
        SkillType.Spelling,
    ];

    /// <summary>Receptive skills — cheap items, so we can afford precision.</summary>
    public SkillLimits CefrLimits { get; init; } = new(3, 6, 0.40);

    /// <summary>
    /// Productive skills. Each item costs the learner a written or spoken
    /// answer and an AI evaluation, so the caps are tighter and the SE target
    /// looser; the level engine refines these from real sessions afterwards.
    /// </summary>
    public SkillLimits ProductionLimits { get; init; } = new(2, 3, 0.55);

    public SkillLimits SpellingLimits { get; init; } = new(4, 4, 0);

    /// <summary>Spelling accuracy at or above which free typing is the start.</summary>
    public double FreeTypingThreshold { get; init; } = 0.75;

    /// <summary>
    /// Shown to the learner as "about N questions" — an adaptive test has no
    /// fixed length.
    /// </summary>
    public int EstimatedTotalItems { get; init; } = 20;

    public SkillLimits LimitsFor(SkillType skill) => skill switch
    {
        SkillType.Reading or SkillType.Listening => CefrLimits,
        SkillType.Speaking or SkillType.Writing => ProductionLimits,
        SkillType.Spelling => SpellingLimits,
        _ => CefrLimits,
    };
}

public sealed record SkillLimits(
    int MinItems,
    int MaxItems,
    /// <summary>
    /// Stop asking once the posterior standard error drops to this. 0.40
    /// logits is ~0.8 of a CEFR step at the default spacing.
    /// </summary>
    double TargetStandardError);
