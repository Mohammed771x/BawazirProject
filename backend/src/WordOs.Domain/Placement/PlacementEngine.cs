using WordOs.Domain.Common;

namespace WordOs.Domain.Placement;

/// <summary>
/// Drives one adaptive placement test.
/// </summary>
/// <remarks>
/// Shape of a run:
/// <code>
/// Reading    3 items — easiest first, then climbing with the estimate
/// Listening  3 items — the same ladder
/// Speaking   1 item  — pitched at what Reading and Listening already showed
/// Writing    1 item  — likewise
/// </code>
///
/// Eight questions, and no spelling ladder (ADR-098). The test is a first
/// estimate a learner can overrule, not an examination, and twenty questions
/// bought precision at the price of the people who never finished it.
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

            // Speaking and Writing must be measured on language the learner
            // actually produced. Grammar items are filed under Writing and are
            // multiple-choice, so a learner who answers those well can satisfy
            // the stopping rule before ever writing a sentence — and then the
            // band comes from ticking boxes about a skill defined by producing
            // language. One produced answer is the minimum evidence.
            var producedAny = skill is not (SkillType.Speaking or SkillType.Writing)
                              || responses.Any(r =>
                                  r.Skill == skill &&
                                  PlacementItemBank.Find(r.ItemId) is
                                      { } asked2 &&
                                  (asked2.IsFreeText || asked2.IsSpoken));

            if (producedAny && forSkill.Count >= limits.MinItems)
            {
                var estimate = Estimator.Estimate(forSkill);
                // Confident enough — spend the remaining questions elsewhere.
                if (estimate.StandardError <= limits.TargetStandardError) continue;
            }

            var pool = PlacementItemBank.ForSkill(skill)
                .Where(i => !asked.Contains(i.Id))
                .ToList();

            // Until there is one, only produced-language items count as
            // progress for these two skills.
            if (!producedAny)
            {
                var produced = pool
                    .Where(i => i.IsFreeText || i.IsSpoken)
                    .ToList();
                if (produced.Count > 0) pool = produced;
            }

            if (pool.Count == 0) continue;

            // Spelling is not adaptive: it walks a short fixed ladder so the
            // accuracy figure is comparable between learners (ADR-008).
            if (skill == SkillType.Spelling) return pool[0];

            // The first question of a skill is the easiest one available, not
            // the most informative one.
            //
            // Adaptive-testing theory says to open at the population mean,
            // because that is where a single answer tells you most. It is also
            // how you greet a beginner with a B1 question and lose them before
            // the test has begun. Opening easy costs a strong learner an item
            // or two — the ladder climbs from their second answer — and costs
            // a weak learner nothing, which is the trade this product wants:
            // the result must never read as a verdict (Part 1).
            //
            // Unless the skill has exactly one question to spend. Then there is
            // no ladder to climb: the opening item is also the closing one, and
            // pitching it at the floor would measure every learner alive at the
            // floor. Speaking and Writing are asked after Reading and
            // Listening precisely so that something is already known by the
            // time their single question is chosen (ADR-098).
            var oneShot = limits.MaxItems == 1 && responses.Count > 0;

            if (forSkill.Count == 0 && !oneShot)
            {
                var floor = pool.Min(i => Config.Scale.DifficultyOf(i.Level));
                var easiest = pool
                    .Where(i => Math.Abs(
                        Config.Scale.DifficultyOf(i.Level) - floor) < 1e-9)
                    .ToList();

                return easiest[random.Next(easiest.Count)];
            }

            // A skill with answers of its own is estimated from those; a
            // one-shot skill borrows the estimate from everything answered so
            // far, because it has nothing of its own yet.
            var theta = Estimator.Estimate(
                forSkill.Count > 0
                    ? forSkill
                    : responses
                        .Select(r => new ScoredResponse(
                            r.ItemId, r.Difficulty, r.Score))
                        .ToList()).Theta;

            // Never more than one band above where the learner currently sits.
            //
            // Without this, a learner failing everything gets walked *upwards*
            // once the easy items run out, because "closest remaining
            // difficulty" is all the rule says — and the closest thing left to
            // someone at the floor is something harder. Parading progressively
            // harder questions at a person who is already struggling is the
            // opposite of what this test is for. When nothing is within reach,
            // the skill is finished; the estimate is not going to improve.
            var reach = theta + Config.Scale.StepLogits;

            // And never harder than the question they just got wrong.
            //
            // The band rule alone is not enough at the floor: one missed A1
            // item leaves the estimate well above A1, so the nearest remaining
            // difficulty is A2 and the second question is harder than the
            // first. Over six questions that corrected itself; over three
            // (ADR-098) it is most of the test. A wrong answer may hold the
            // level or lower it — never raise it.
            if (forSkill.Count > 0 && forSkill[^1].Score < 0.5)
                reach = Math.Min(reach, forSkill[^1].Difficulty);
            var within = pool
                .Where(i => Config.Scale.DifficultyOf(i.Level) <= reach)
                .ToList();

            // For a one-shot skill the reach rule would be a way of asking
            // nothing at all: there is no "next item" to protect the learner
            // from, and skipping leaves the skill unmeasured. Fall back to the
            // easiest item it has rather than returning nothing.
            if (within.Count == 0 && oneShot)
            {
                var floor = pool.Min(i => Config.Scale.DifficultyOf(i.Level));
                within = pool
                    .Where(i => Math.Abs(
                        Config.Scale.DifficultyOf(i.Level) - floor) < 1e-9)
                    .ToList();
            }

            pool = within;

            if (pool.Count == 0) continue;

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
    /// <remarks>
    /// Skills are measured separately — never averaged into one number — with
    /// one exception that ADR-098 introduced and names openly.
    ///
    /// Speaking and Writing are now one question each. One response cannot
    /// place anybody: the population prior dominates it, so every learner
    /// alive lands within a band or two of the middle whatever they wrote, and
    /// a learner who answered the whole test wrongly is told their Writing is
    /// B1. Measured, it was A2+ to B2 across the entire range of behaviour.
    ///
    /// So a single-response skill is estimated against a prior centred on what
    /// the rest of the test showed, rather than on the population mean. It
    /// borrows the <i>location</i> and not the <i>certainty</i>: the prior keeps
    /// its usual width, so the one produced answer still moves the band about
    /// as far as one answer should, and the reported confidence — computed from
    /// that answer alone — stays near zero. The learner is shown that as
    /// "provisional", and the level engine replaces it from real sessions.
    /// Reading and Listening have three answers each and are untouched.
    /// </remarks>
    public PlacementOutcome Complete(IReadOnlyList<PlacementResponse> responses)
    {
        var levels = new List<PlacementSkillOutcome>();

        var whole = responses
            .Select(r => new ScoredResponse(r.ItemId, r.Difficulty, r.Score))
            .ToList();

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

            var own = forSkill
                .Select(r => new ScoredResponse(r.ItemId, r.Difficulty, r.Score))
                .ToList();

            var precision = Estimator.Estimate(own);

            var band = own.Count > 1
                ? precision
                : new AbilityEstimator(Config.Scale with
                {
                    PriorMean = Estimator.Estimate(whole).Theta,
                }).Estimate(own);

            levels.Add(new PlacementSkillOutcome(
                skill,
                Level: Config.Scale.LevelFor(band.Theta),
                Confidence: Config.Scale.ConfidenceFor(precision.StandardError),
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

    /// <summary>
    /// The order the placement test asks its skills in — the pipeline's order
    /// (ADR-001, ADR-087), so the test reads as a preview of the journey.
    /// </summary>
    /// <remarks>
    /// Spelling is not in it. It was four questions that produced no level —
    /// only the choice between letter tiles and free typing, which the first
    /// real spelling session settles anyway (ADR-098). Its items are still in
    /// the bank, so putting it back is this list plus its limits.
    ///
    /// Speaking and Writing come last for a reason that now matters more than
    /// the pipeline order: each has a single question, and it is chosen from
    /// what Reading and Listening already established.
    /// </remarks>
    public IReadOnlyList<SkillType> SkillOrder { get; init; } =
    [
        SkillType.Reading,
        SkillType.Listening,
        SkillType.Speaking,
        SkillType.Writing,
    ];

    /// <summary>
    /// Receptive skills: three questions each, no early stop.
    /// </summary>
    /// <remarks>
    /// Fixed length rather than adaptive stopping, because at three items the
    /// stopping rule can only ever cost a question — and a test whose length
    /// varies between learners is harder to describe honestly before they
    /// start it. The ladder still adapts: easiest first, then climbing with the
    /// estimate, which is the beginner / middle / harder shape asked for.
    /// </remarks>
    public SkillLimits CefrLimits { get; init; } = new(3, 3, 0);

    /// <summary>
    /// Productive skills: one question each.
    /// </summary>
    /// <remarks>
    /// One written answer and one spoken one. Each costs the learner real
    /// effort and costs the service an AI evaluation, and three of them were
    /// where the old test lost people. A single item cannot produce a
    /// confident band — <see cref="PlacementOutcome.HasLowConfidence"/> will
    /// say so — and the level engine refines it from real sessions.
    /// </remarks>
    public SkillLimits ProductionLimits { get; init; } = new(1, 1, 0);

    /// <summary>
    /// Unused while Spelling is out of <see cref="SkillOrder"/> (ADR-098).
    /// </summary>
    public SkillLimits SpellingLimits { get; init; } = new(4, 4, 0);

    /// <summary>Spelling accuracy at or above which free typing is the start.</summary>
    public double FreeTypingThreshold { get; init; } = 0.75;

    /// <summary>
    /// How many questions the test asks: 3 + 3 + 1 + 1 (ADR-098).
    /// </summary>
    /// <remarks>
    /// Exact now rather than an estimate, because nothing stops early any
    /// more. It stays a tunable: the client only reads it to draw progress.
    /// </remarks>
    public int EstimatedTotalItems { get; init; } = 8;

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
