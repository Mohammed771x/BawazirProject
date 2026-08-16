import '../../../core/models/models.dart';

/// ⚠️ DISPOSABLE DEVELOPMENT COMPONENT — the C# backend owns this in Phase 5.
///
/// The **system-validated level** engine and the archiving rule that depends on
/// it. These are the two decisions the documents are most emphatic about, and
/// the reason rule R6 exists: a learner may set their own level freely, but
/// only performance the system has actually observed may move the level that
/// drives progression and archiving.
///
/// Sources: `MVP Core.txt` §22–23 (thresholds), `Word Life Cycle.txt` §27–31
/// (archiving), ADR-013.
class LevelEngine {
  const LevelEngine({this.config = const LevelPolicy()});

  final LevelPolicy config;

  // ── Level progression ─────────────────────────────────────────────────────

  /// Decides what should happen to one skill's system-validated level.
  ///
  /// Returns null when nothing changes — which is the common case, and
  /// deliberately so: the documents insist a level never moves on one session.
  LevelDecision? evaluate(SkillLevel level) {
    // Spelling is measured but carries no CEFR band, so there is nothing to
    // promote or demote (ADR-008).
    if (!level.carriesCefrLevel) return null;

    final current = level.systemAssessedLevel;
    if (current == null) return null;

    // Not enough accumulated evidence yet. "لا يتم تغيير مستوى المستخدم بناءً
    // على سؤال واحد" — the decision needs a window, not a session.
    if (level.evaluationSessions < config.minEvaluationSessions) return null;

    final accuracy = level.rollingAccuracy;

    if (accuracy >= config.promoteThreshold) {
      final next = _step(current, 1);
      // Already at the top of the ladder: hold, and reset the window so the
      // learner is not re-evaluated against evidence already spent.
      if (next == null) {
        return LevelDecision.hold(skill: level.skill, accuracy: accuracy);
      }
      return LevelDecision(
        skill: level.skill,
        previous: current,
        next: next,
        accuracy: accuracy,
        sessionsConsidered: level.evaluationSessions,
        reason: LevelChangeReason.promoted,
      );
    }

    if (accuracy < config.demoteThreshold) {
      final next = _step(current, -1);
      if (next == null) {
        return LevelDecision.hold(skill: level.skill, accuracy: accuracy);
      }
      return LevelDecision(
        skill: level.skill,
        previous: current,
        next: next,
        accuracy: accuracy,
        sessionsConsidered: level.evaluationSessions,
        reason: LevelChangeReason.demoted,
      );
    }

    // Between the thresholds: the level is right where it should be. The window
    // still resets, otherwise a learner sitting at 80% would be re-tested
    // against the same accumulated number forever.
    return LevelDecision.hold(skill: level.skill, accuracy: accuracy);
  }

  /// Applies [decision] to [level], resetting the evaluation window.
  ///
  /// Only `systemAssessedLevel` moves. The learner's own choice is never
  /// touched here — that is the whole point of rule R6.
  SkillLevel apply(SkillLevel level, LevelDecision decision) => level.copyWith(
        systemAssessedLevel: decision.next ?? level.systemAssessedLevel,
        evaluationSessions: 0,
        rollingAccuracy: 0,
      );

  /// One step along the CEFR ladder, or null at either end.
  ///
  /// Promotion is deliberately one step (`B1 → B1+`), never a whole band
  /// (`B1 → B2`) — `MVP Core.txt` §23 calls this out explicitly.
  CefrLevel? _step(CefrLevel from, int direction) {
    final index = from.rank + direction;
    if (index < 0 || index >= CefrLevel.values.length) return null;
    return CefrLevel.values[index];
  }

  // ── Archiving ─────────────────────────────────────────────────────────────

  /// Whether [word] may be archived, given the learner's proven level.
  ///
  /// Three conditions, all required:
  ///
  /// 1. the word has finished the pipeline (`ACTIVE`) — a word still being
  ///    learned is never archived;
  /// 2. the word's level sits far enough below the **system-validated** level
  ///    (never the user-selected one — `Word Life Cycle.txt` §28);
  /// 3. the word has had enough exposure, so it is retired because it is
  ///    genuinely established, not merely because it is easy (§30).
  bool shouldArchive({
    required CefrLevel wordLevel,
    required WordState state,
    required int exposureCount,
    required CefrLevel? systemValidatedLevel,
  }) {
    if (state != WordState.active) return false;
    if (systemValidatedLevel == null) return false;

    final gap = systemValidatedLevel.rank - wordLevel.rank;
    if (gap < config.archiveLevelGapSteps) return false;

    return exposureCount >= config.archiveMinExposure;
  }

  /// The level the system has *proven* across the CEFR skills.
  ///
  /// The minimum across skills, not the average: archiving removes a word from
  /// active rotation, so it should follow the weakest evidence rather than a
  /// number flattered by one strong skill (ADR-013).
  CefrLevel? systemValidatedLevel(Iterable<SkillLevel> levels) {
    CefrLevel? lowest;
    for (final level in levels) {
      final assessed = level.systemAssessedLevel;
      if (assessed == null) continue; // Spelling carries none.
      if (lowest == null || assessed.rank < lowest.rank) lowest = assessed;
    }
    return lowest;
  }
}

/// Every tunable of level progression and archiving (rule R3).
class LevelPolicy {
  const LevelPolicy({
    this.minEvaluationSessions = 14,
    this.promoteThreshold = 0.85,
    this.demoteThreshold = 0.70,
    this.archiveLevelGapSteps = 4,
    this.archiveMinExposure = 3,
  });

  /// Sessions of evidence needed before the level may move at all.
  final int minEvaluationSessions;

  /// `MVP Core.txt` §23 — a strong indicator of mastery.
  final double promoteThreshold;

  /// `MVP Core.txt` §22 — below this the content is too hard.
  final double demoteThreshold;

  /// How far below the proven level a word must sit before it is a candidate
  /// for archiving. Four steps is two full CEFR bands (A1 → B1), which is the
  /// gap the worked example in `Word Life Cycle.txt` §30 uses.
  final int archiveLevelGapSteps;

  /// Exposure is a **priority signal**, never a limit or a delete trigger
  /// (rule R8). It appears here only as a floor: a word nobody has actually met
  /// in content is not established enough to retire.
  final int archiveMinExposure;
}

enum LevelChangeReason { promoted, demoted, held }

/// The outcome of evaluating one skill.
class LevelDecision {
  const LevelDecision({
    required this.skill,
    required this.previous,
    required this.next,
    required this.accuracy,
    required this.sessionsConsidered,
    required this.reason,
  });

  /// No movement, but the evaluation window is spent and resets.
  const LevelDecision.hold({required this.skill, required this.accuracy})
      : previous = null,
        next = null,
        sessionsConsidered = 0,
        reason = LevelChangeReason.held;

  final SkillType skill;
  final CefrLevel? previous;
  final CefrLevel? next;
  final double accuracy;
  final int sessionsConsidered;
  final LevelChangeReason reason;

  bool get moved => reason != LevelChangeReason.held;

  bool get isPromotion => reason == LevelChangeReason.promoted;
}
