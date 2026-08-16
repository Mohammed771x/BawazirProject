using WordOs.Domain.Common;

namespace WordOs.Domain.Placement;

/// <summary>
/// A <b>Rasch (1PL) ability estimator with expert-assigned item difficulties</b>,
/// updated after every response by <b>Expected A Posteriori (EAP)</b>
/// integration over a discrete ability grid.
/// </summary>
/// <remarks>
/// Why this and not full IRT 2PL/3PL: those need discrimination and guessing
/// parameters calibrated from real response data, which a pre-pilot product
/// does not have. Fitting them to zero learners would be false precision.
/// Rasch with a-priori difficulties is the standard way to run a new item bank
/// before calibration data exists, and it upgrades to calibrated parameters by
/// changing data, not code.
///
/// Ported unchanged from the specification in
/// <c>mobile/lib/mock_backend/engine/placement/ability_estimator.dart</c>;
/// the method, the rationale and the replacement path are documented in
/// <c>docs/06-PLACEMENT-ALGORITHM.md</c>.
/// </remarks>
public sealed class AbilityEstimator(AbilityScale? scale = null)
{
    public AbilityScale Scale { get; } = scale ?? new AbilityScale();

    /// <summary>Probability of a correct response under the Rasch model.</summary>
    public static double ProbabilityCorrect(double theta, double difficulty) =>
        1 / (1 + Math.Exp(-(theta - difficulty)));

    /// <summary>
    /// Fisher information of a Rasch item at <paramref name="theta"/>.
    /// </summary>
    /// <remarks>
    /// Maximised when the item difficulty equals the ability, which is what
    /// makes "pick the item closest to the current estimate" the optimal
    /// next-item rule for this model rather than a heuristic.
    /// </remarks>
    public static double Information(double theta, double difficulty)
    {
        var p = ProbabilityCorrect(theta, difficulty);
        return p * (1 - p);
    }

    /// <summary>
    /// Posterior mean and standard error of ability given the responses.
    /// </summary>
    /// <remarks>
    /// Scores are continuous in <c>[0, 1]</c>: a multiple-choice item
    /// contributes 0 or 1, while an AI-rubric-scored writing or speaking
    /// response contributes its partial credit. The likelihood
    /// <c>P^s · (1-P)^(1-s)</c> reduces exactly to the Bernoulli likelihood
    /// when <c>s</c> is 0 or 1, so both item kinds live on one scale without a
    /// second model.
    /// </remarks>
    public AbilityEstimate Estimate(IReadOnlyList<ScoredResponse> responses)
    {
        var grid = Scale.Grid;
        var weights = new double[grid.Count];

        for (var i = 0; i < grid.Count; i++)
        {
            var theta = grid[i];

            // Worked in log space: a long response vector otherwise underflows
            // to zero and the posterior collapses to NaN. There is a
            // regression test for exactly this.
            var logLikelihood = LogNormalDensity(
                theta, Scale.PriorMean, Scale.PriorSd);

            foreach (var response in responses)
            {
                var p = Math.Clamp(
                    ProbabilityCorrect(theta, response.Difficulty),
                    1e-9, 1 - 1e-9);
                var s = Math.Clamp(response.Score, 0, 1);
                logLikelihood += (s * Math.Log(p)) + ((1 - s) * Math.Log(1 - p));
            }

            weights[i] = logLikelihood;
        }

        var maxLog = weights.Max();
        var total = 0.0;
        for (var i = 0; i < weights.Length; i++)
        {
            weights[i] = Math.Exp(weights[i] - maxLog);
            total += weights[i];
        }

        if (total <= 0 || double.IsNaN(total) || double.IsInfinity(total))
        {
            // Degenerate posterior: fall back to the prior rather than emitting
            // NaN into a learner's level.
            return new AbilityEstimate(Scale.PriorMean, Scale.PriorSd, responses.Count);
        }

        var mean = 0.0;
        for (var i = 0; i < grid.Count; i++) mean += grid[i] * weights[i];
        mean /= total;

        var variance = 0.0;
        for (var i = 0; i < grid.Count; i++)
        {
            var d = grid[i] - mean;
            variance += d * d * weights[i];
        }
        variance /= total;

        return new AbilityEstimate(mean, Math.Sqrt(variance), responses.Count);
    }

    private static double LogNormalDensity(double x, double mean, double sd)
    {
        var z = (x - mean) / sd;
        return (-0.5 * z * z) - Math.Log(sd) - (0.5 * Math.Log(2 * Math.PI));
    }
}

/// <summary>A single scored response feeding the estimator.</summary>
public sealed record ScoredResponse(string ItemId, double Difficulty, double Score);

public sealed record AbilityEstimate(
    double Theta,
    double StandardError,
    int ResponseCount);

/// <summary>
/// Maps the CEFR ladder onto the logit scale the estimator works in.
/// </summary>
/// <remarks>
/// Every number here is a tunable (rule R3). They live in one object so the
/// backend can seed them from <c>configurations</c> rather than hard-coding
/// them.
/// </remarks>
public sealed record AbilityScale
{
    /// <summary>The CEFR band placed at 0 logits.</summary>
    public CefrLevel Centre { get; init; } = CefrLevel.B1Plus;

    /// <summary>Logit distance between two adjacent CEFR bands.</summary>
    public double StepLogits { get; init; } = 0.5;

    /// <summary>
    /// Population prior. Slightly below the centre band because the pilot
    /// audience skews A2–B1; it only matters before the first few answers.
    /// </summary>
    public double PriorMean { get; init; } = -0.25;

    public double PriorSd { get; init; } = 1.2;

    public double GridMin { get; init; } = -3.5;

    public double GridMax { get; init; } = 3.5;

    public double GridStep { get; init; } = 0.05;

    /// <summary>
    /// Posterior SE at which the placement is treated as fully confident. Tied
    /// to the stopping rule on purpose: a test that stopped because it hit its
    /// SE target should report high confidence, not middling confidence.
    /// </summary>
    public double ConfidentStandardError { get; init; } = 0.40;

    /// <summary>
    /// Posterior SE at which the test has learned essentially nothing beyond
    /// the prior, so confidence is zero.
    /// </summary>
    public double UninformativeStandardError { get; init; } = 1.10;

    private IReadOnlyList<double>? _grid;

    public IReadOnlyList<double> Grid
    {
        get
        {
            if (_grid is not null) return _grid;
            var points = new List<double>();
            for (var t = GridMin; t <= GridMax + 1e-9; t += GridStep) points.Add(t);
            return _grid = points;
        }
    }

    /// <summary>Difficulty in logits of an item written at a CEFR band.</summary>
    public double DifficultyOf(CefrLevel level) =>
        (level.Rank() - Centre.Rank()) * StepLogits;

    /// <summary>
    /// The CEFR band whose anchor point is nearest to <paramref name="theta"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately a nearest-anchor rule rather than a table of cut scores:
    /// with evenly spaced anchors the two are equivalent, and this version
    /// cannot develop gaps or overlaps when the spacing is retuned.
    /// </remarks>
    public CefrLevel LevelFor(double theta)
    {
        var best = CefrLevel.A1;
        var bestDistance = double.MaxValue;

        foreach (var level in Enum.GetValues<CefrLevel>())
        {
            var distance = Math.Abs(DifficultyOf(level) - theta);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = level;
        }

        return best;
    }

    /// <summary>Reported confidence in <c>[0, 1]</c> from the posterior SE.</summary>
    public double ConfidenceFor(double standardError)
    {
        if (standardError <= ConfidentStandardError) return 1;
        if (standardError >= UninformativeStandardError) return 0;
        return 1 - ((standardError - ConfidentStandardError)
                    / (UninformativeStandardError - ConfidentStandardError));
    }
}
