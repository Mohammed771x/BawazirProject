import 'dart:math';

import '../../../core/api/wordos_api.dart';
import '../../../core/models/models.dart';
import 'ability_estimator.dart';
import 'free_response_scorer.dart';
import 'placement_item_bank.dart';

/// ⚠️ DISPOSABLE DEVELOPMENT COMPONENT — the C# backend owns this in Phase 5.
///
/// Drives one adaptive placement test. The full method, and how to replace it,
/// is documented in `docs/06-PLACEMENT-ALGORITHM.md`.
///
/// Shape of a run:
/// ```
/// for each CEFR skill (Reading, Listening, Speaking, Writing):
///     ask the item whose difficulty is closest to the current ability estimate
///     re-estimate ability by EAP after every answer
///     stop when the posterior SE is small enough, or the item cap is reached
/// then Spelling: a short fixed ladder, measured but never levelled
/// ```
class PlacementEngine {
  PlacementEngine({
    this.config = const PlacementConfig(),
    FreeResponseScorer? scorer,
    Random? random,
  })  : _scorer = scorer ?? const HeuristicFreeResponseScorer(),
        _random = random ?? Random();

  final PlacementConfig config;
  final FreeResponseScorer _scorer;
  final Random _random;

  final Map<String, PlacementRun> _runs = {};

  PlacementRun? runFor(String sessionId) => _runs[sessionId];

  // ── Lifecycle ─────────────────────────────────────────────────────────────

  PlacementStep start(String sessionId, String userId) {
    final run = PlacementRun(id: sessionId, userId: userId);
    _runs[sessionId] = run;
    return _advance(run);
  }

  /// Records an answer and returns the next step.
  ///
  /// Answering an item that is not the one currently on screen is rejected
  /// rather than silently ignored — a retried request after a dropped
  /// connection must not corrupt the estimate.
  PlacementStep answer(String sessionId, String itemId, String answer) {
    final run = _require(sessionId);

    if (run.currentItem == null || run.currentItem!.id != itemId) {
      throw const ApiException(
        'ITEM_NOT_CURRENT',
        'That question is no longer the active one.',
        statusCode: 409,
      );
    }

    final item = run.currentItem!;
    final score = item.isFreeText
        ? _scorer.score(item: item, response: answer)
        : (answer == item.correctAnswer ? 1.0 : 0.0);

    run.record(
      skill: item.skill,
      response: ScoredResponse(
        itemId: item.id,
        difficulty: config.scale.difficultyOf(item.level),
        score: score,
      ),
      rawAnswer: answer,
    );
    run.currentItem = null;

    return _advance(run);
  }

  PlacementResult complete(String sessionId) {
    final run = _require(sessionId);
    if (!run.isComplete) {
      throw const ApiException(
        'PLACEMENT_INCOMPLETE',
        'The placement test is not finished yet.',
        statusCode: 409,
      );
    }
    _runs.remove(sessionId);
    return _result(run);
  }

  void abandon(String sessionId) => _runs.remove(sessionId);

  PlacementRun _require(String sessionId) {
    final run = _runs[sessionId];
    if (run == null) {
      throw const ApiException(
        'PLACEMENT_NOT_FOUND',
        'This placement test has expired. Please start again.',
        statusCode: 404,
      );
    }
    return run;
  }

  // ── Adaptive selection ────────────────────────────────────────────────────

  /// Picks the next item, moving on to the next skill when the current one has
  /// been measured precisely enough.
  PlacementStep _advance(PlacementRun run) {
    while (run.skillIndex < config.skillOrder.length) {
      final skill = config.skillOrder[run.skillIndex];
      final next = _nextItemFor(run, skill);
      if (next != null) {
        run.currentItem = next;
        return _step(run, next);
      }
      run.skillIndex++;
    }

    run.isComplete = true;
    return _step(run, null);
  }

  BankItem? _nextItemFor(PlacementRun run, SkillType skill) {
    final asked = run.askedIds;
    final pool =
        PlacementItemBank.forSkill(skill).where((i) => !asked.contains(i.id));
    if (pool.isEmpty) return null;

    final responses = run.responsesFor(skill);
    final limits = config.limitsFor(skill);

    if (responses.length >= limits.maxItems) return null;

    if (responses.length >= limits.minItems) {
      final estimate = AbilityEstimator(scale: config.scale).estimate(responses);
      // Confident enough — spend the remaining questions on another skill.
      if (estimate.standardError <= limits.targetStandardError) return null;
    }

    // Spelling is not adaptive: it walks a short fixed ladder so the accuracy
    // figure is comparable between learners (ADR-008).
    if (skill == SkillType.spelling) {
      return pool.first;
    }

    final theta = responses.isEmpty
        ? config.scale.priorMean
        : AbilityEstimator(scale: config.scale).estimate(responses).theta;

    // Maximum Fisher information for a Rasch item is at difficulty == ability,
    // so "closest difficulty" *is* the optimal choice under this model. Ties are
    // broken at random for exposure control.
    final ranked = pool.toList()
      ..sort((a, b) {
        final da = (config.scale.difficultyOf(a.level) - theta).abs();
        final db = (config.scale.difficultyOf(b.level) - theta).abs();
        return da.compareTo(db);
      });

    final best = (config.scale.difficultyOf(ranked.first.level) - theta).abs();
    final tied = ranked
        .where((i) =>
            ((config.scale.difficultyOf(i.level) - theta).abs() - best).abs() <
            1e-9)
        .toList();
    return tied[_random.nextInt(tied.length)];
  }

  PlacementStep _step(PlacementRun run, BankItem? item) => PlacementStep(
        sessionId: run.id,
        item: item == null ? null : _project(item),
        progress: PlacementProgress(
          answered: run.totalAnswered,
          estimatedTotal: max(run.totalAnswered, config.estimatedTotalItems),
          currentSkill: config
              .skillOrder[min(run.skillIndex, config.skillOrder.length - 1)],
          skillIndex: min(run.skillIndex, config.skillOrder.length - 1),
          skillCount: config.skillOrder.length,
        ),
        isComplete: run.isComplete,
      );

  /// Server-side shuffle (rule R7) and removal of the correct answer.
  PlacementItem _project(BankItem item) => PlacementItem(
        id: item.id,
        skill: item.skill,
        type: item.type,
        prompt: item.prompt,
        options: item.options.toList()..shuffle(_random),
        passage: item.passage,
        audioText: item.audioText,
      );

  // ── Scoring ───────────────────────────────────────────────────────────────

  PlacementResult _result(PlacementRun run) {
    final estimator = AbilityEstimator(scale: config.scale);
    final levels = <SkillLevel>[];

    for (final skill in config.skillOrder) {
      final responses = run.responsesFor(skill);
      final accuracy = responses.isEmpty
          ? 0.0
          : responses.map((r) => r.score).reduce((a, b) => a + b) /
              responses.length;

      if (skill == SkillType.spelling) {
        levels.add(
          SkillLevel.unlevelled(
            skill: skill,
            evaluationSessions: 0,
            rollingAccuracy: accuracy,
            dailyTargetWords: config.defaultDailyTarget,
            confidence: responses.isEmpty ? 0 : 1,
          ),
        );
        continue;
      }

      final estimate = estimator.estimate(responses);
      final level = config.scale.levelFor(estimate.theta);
      levels.add(
        SkillLevel(
          skill: skill,
          // Placement seeds both: the system-assessed level is the measurement,
          // and the user-selected level starts as a copy the learner may then
          // override in Settings (ADR-007, rule R6).
          userSelectedLevel: level,
          systemAssessedLevel: level,
          evaluationSessions: 0,
          rollingAccuracy: accuracy,
          dailyTargetWords: config.defaultDailyTarget,
          confidence: config.scale.confidenceFor(estimate.standardError),
        ),
      );
    }

    final spellingResponses = run.responsesFor(SkillType.spelling);
    final spellingCorrect =
        spellingResponses.where((r) => r.score >= 0.999).length;
    final spellingAccuracy = spellingResponses.isEmpty
        ? 0.0
        : spellingCorrect / spellingResponses.length;

    return PlacementResult(
      levels: levels,
      spelling: SpellingDiagnostic(
        itemsAnswered: spellingResponses.length,
        correct: spellingCorrect,
        // Weak spellers start with letter tiles; confident ones type freely.
        // Spelling sessions can move a learner between modes later — this is
        // only the starting affordance (ADR-008).
        supportMode: spellingAccuracy >= config.freeTypingThreshold
            ? SpellingInputMode.freeTyping
            : SpellingInputMode.letterTiles,
      ),
      summary: '',
    );
  }
}

/// Every tunable of the placement algorithm, in one place (rule R3).
class PlacementConfig {
  const PlacementConfig({
    this.scale = const AbilityScale(),
    this.skillOrder = const [
      SkillType.reading,
      SkillType.listening,
      SkillType.speaking,
      SkillType.writing,
      SkillType.spelling,
    ],
    this.cefrLimits = const SkillLimits(
      minItems: 3,
      maxItems: 6,
      targetStandardError: 0.40,
    ),
    this.productionLimits = const SkillLimits(
      minItems: 2,
      maxItems: 3,
      targetStandardError: 0.55,
    ),
    this.spellingLimits = const SkillLimits(
      minItems: 4,
      maxItems: 4,
      targetStandardError: 0,
    ),
    this.defaultDailyTarget = 10,
    this.freeTypingThreshold = 0.75,
    this.estimatedTotalItems = 20,
  });

  final AbilityScale scale;
  final List<SkillType> skillOrder;

  /// Receptive skills — cheap items, so we can afford precision.
  final SkillLimits cefrLimits;

  /// Productive skills. Each item costs the learner a written or spoken answer
  /// and an AI evaluation, so the caps are tighter and the SE target looser;
  /// the level engine refines these from real sessions afterwards.
  final SkillLimits productionLimits;

  final SkillLimits spellingLimits;

  final int defaultDailyTarget;

  /// Spelling accuracy at or above which the learner starts on free typing.
  final double freeTypingThreshold;

  /// Shown to the learner as "about N questions" — an adaptive test has no
  /// fixed length.
  final int estimatedTotalItems;

  SkillLimits limitsFor(SkillType skill) => switch (skill) {
        SkillType.reading || SkillType.listening => cefrLimits,
        SkillType.speaking || SkillType.writing => productionLimits,
        SkillType.spelling => spellingLimits,
      };
}

class SkillLimits {
  const SkillLimits({
    required this.minItems,
    required this.maxItems,
    required this.targetStandardError,
  });

  final int minItems;
  final int maxItems;

  /// Stop asking once the posterior standard error drops to this. 0.40 logits
  /// is ~0.8 of a CEFR step at the default spacing.
  final double targetStandardError;
}

/// Mutable state of one in-flight placement test.
class PlacementRun {
  PlacementRun({required this.id, required this.userId});

  final String id;
  final String userId;

  int skillIndex = 0;
  bool isComplete = false;
  BankItem? currentItem;

  final Map<SkillType, List<ScoredResponse>> _responses = {};
  final Map<String, String> rawAnswers = {};
  final Set<String> askedIds = {};

  List<ScoredResponse> responsesFor(SkillType skill) =>
      _responses[skill] ?? const [];

  int get totalAnswered =>
      _responses.values.fold(0, (sum, list) => sum + list.length);

  void record({
    required SkillType skill,
    required ScoredResponse response,
    required String rawAnswer,
  }) {
    _responses.putIfAbsent(skill, () => []).add(response);
    rawAnswers[response.itemId] = rawAnswer;
    askedIds.add(response.itemId);
  }
}
