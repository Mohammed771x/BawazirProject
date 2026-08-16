import 'dart:math';

import '../../../core/models/enums.dart';

/// ⚠️ DISPOSABLE DEVELOPMENT COMPONENT — the C# backend owns this in Phase 5.
///
/// A **Rasch (1PL) ability estimator with expert-assigned item difficulties**,
/// updated after every response by **Expected A Posteriori (EAP)** integration
/// over a discrete ability grid.
///
/// Why this and not full IRT 2PL/3PL: those need discrimination and guessing
/// parameters calibrated from real response data, which a pre-pilot product does
/// not have. Fitting them to zero learners would be false precision. Rasch with
/// a-priori difficulties is the standard way to run a new item bank before
/// calibration data exists, and it upgrades to calibrated parameters by changing
/// data, not code — [PlacementItemBank] simply starts returning measured
/// difficulties.
///
/// The full rationale, including how to replace this module, is in
/// `docs/06-PLACEMENT-ALGORITHM.md`.
class AbilityEstimator {
  const AbilityEstimator({this.scale = const AbilityScale()});

  final AbilityScale scale;

  /// Probability of a correct response under the Rasch model.
  static double probabilityCorrect({
    required double theta,
    required double difficulty,
  }) =>
      1 / (1 + exp(-(theta - difficulty)));

  /// Fisher information of a Rasch item at [theta]. Maximised when the item
  /// difficulty equals the ability, which is what makes "pick the item closest
  /// to the current estimate" the optimal next-item rule for this model.
  static double information({
    required double theta,
    required double difficulty,
  }) {
    final p = probabilityCorrect(theta: theta, difficulty: difficulty);
    return p * (1 - p);
  }

  /// Posterior mean and standard error of ability given [responses].
  ///
  /// Scores are continuous in `[0, 1]`: a multiple-choice item contributes 0 or
  /// 1, while an AI-rubric-scored writing or speaking response contributes its
  /// partial credit. The likelihood `P^s · (1-P)^(1-s)` reduces exactly to the
  /// Bernoulli likelihood when `s` is 0 or 1, so both item kinds live on one
  /// scale without a second model.
  AbilityEstimate estimate(List<ScoredResponse> responses) {
    final grid = scale.grid;
    final weights = List<double>.filled(grid.length, 0);

    for (var i = 0; i < grid.length; i++) {
      final theta = grid[i];
      // Work in log space: a long response vector otherwise underflows to zero
      // and the posterior collapses to NaN.
      var logLikelihood = _logNormalDensity(
        theta,
        mean: scale.priorMean,
        sd: scale.priorSd,
      );
      for (final response in responses) {
        final p = probabilityCorrect(
          theta: theta,
          difficulty: response.difficulty,
        ).clamp(1e-9, 1 - 1e-9);
        final s = response.score.clamp(0.0, 1.0);
        logLikelihood += s * log(p) + (1 - s) * log(1 - p);
      }
      weights[i] = logLikelihood;
    }

    final maxLog = weights.reduce(max);
    var total = 0.0;
    for (var i = 0; i < weights.length; i++) {
      weights[i] = exp(weights[i] - maxLog);
      total += weights[i];
    }

    if (total <= 0 || !total.isFinite) {
      // Degenerate posterior: fall back to the prior rather than emitting NaN.
      return AbilityEstimate(
        theta: scale.priorMean,
        standardError: scale.priorSd,
        responseCount: responses.length,
      );
    }

    var mean = 0.0;
    for (var i = 0; i < grid.length; i++) {
      mean += grid[i] * weights[i];
    }
    mean /= total;

    var variance = 0.0;
    for (var i = 0; i < grid.length; i++) {
      final d = grid[i] - mean;
      variance += d * d * weights[i];
    }
    variance /= total;

    return AbilityEstimate(
      theta: mean,
      standardError: sqrt(variance),
      responseCount: responses.length,
    );
  }

  static double _logNormalDensity(
    double x, {
    required double mean,
    required double sd,
  }) {
    final z = (x - mean) / sd;
    return -0.5 * z * z - log(sd) - 0.5 * log(2 * pi);
  }
}

/// A single scored response feeding the estimator.
class ScoredResponse {
  const ScoredResponse({
    required this.itemId,
    required this.difficulty,
    required this.score,
  });

  final String itemId;

  /// Rasch difficulty in logits — see [AbilityScale.difficultyOf].
  final double difficulty;

  /// Continuous credit in `[0, 1]`.
  final double score;
}

class AbilityEstimate {
  const AbilityEstimate({
    required this.theta,
    required this.standardError,
    required this.responseCount,
  });

  final double theta;
  final double standardError;
  final int responseCount;
}

/// Maps the CEFR ladder onto the logit scale the estimator works in.
///
/// Every number here is a tunable (rule R3). They live in one object so the
/// backend can seed them from `configurations` rather than hard-coding them.
class AbilityScale {
  const AbilityScale({
    this.centre = CefrLevel.b1Plus,
    this.stepLogits = 0.5,
    this.priorMean = -0.25,
    this.priorSd = 1.2,
    this.gridMin = -3.5,
    this.gridMax = 3.5,
    this.gridStep = 0.05,
    this.confidentStandardError = 0.40,
    this.uninformativeStandardError = 1.10,
  });

  /// The CEFR band placed at 0 logits.
  final CefrLevel centre;

  /// Logit distance between two adjacent CEFR bands.
  final double stepLogits;

  /// Population prior. Slightly below the centre band because the pilot
  /// audience skews A2–B1; it only matters before the first few answers.
  final double priorMean;
  final double priorSd;

  final double gridMin;
  final double gridMax;
  final double gridStep;

  List<double> get grid => [
        for (var t = gridMin; t <= gridMax + 1e-9; t += gridStep) t,
      ];

  /// Difficulty in logits of an item written at [level].
  double difficultyOf(CefrLevel level) =>
      (level.rank - centre.rank) * stepLogits;

  /// The CEFR band whose anchor point is nearest to [theta].
  ///
  /// Deliberately a nearest-anchor rule rather than a table of cut scores: with
  /// evenly spaced anchors the two are equivalent, and this version cannot
  /// develop gaps or overlaps when the spacing is retuned.
  CefrLevel levelFor(double theta) {
    var best = CefrLevel.values.first;
    var bestDistance = double.infinity;
    for (final level in CefrLevel.values) {
      final distance = (difficultyOf(level) - theta).abs();
      if (distance < bestDistance) {
        bestDistance = distance;
        best = level;
      }
    }
    return best;
  }

  /// Posterior SE at which the placement is treated as fully confident. It is
  /// tied to the stopping rule on purpose: a test that stopped because it hit
  /// its SE target should report high confidence, not middling confidence.
  final double confidentStandardError;

  /// Posterior SE at which the test has learned essentially nothing beyond the
  /// prior, so confidence is zero.
  final double uninformativeStandardError;

  /// Reported confidence in `[0, 1]`, derived from the posterior standard error.
  double confidenceFor(double standardError) {
    if (standardError <= confidentStandardError) return 1;
    if (standardError >= uninformativeStandardError) return 0;
    return 1 -
        (standardError - confidentStandardError) /
            (uninformativeStandardError - confidentStandardError);
  }
}
