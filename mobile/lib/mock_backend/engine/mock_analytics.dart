import '../../core/models/models.dart';

/// ⚠️ DISPOSABLE DEVELOPMENT COMPONENT — Phase 5 replaces this with the
/// `analytics_events` table and real aggregation.
///
/// Records what the Owner dashboard needs to answer "is the algorithm working?"
/// (`MVP Core.txt` §53–68). The engine writes here at every meaningful
/// transition; nothing reads user state directly to reconstruct history after
/// the fact, because the real system will not be able to either.
class MockAnalytics {
  final List<SignInEvent> signIns = [];
  final List<SessionRecord> sessions = [];
  final List<WordAttempt> attempts = [];

  /// AI calls that fell back to a deterministic scorer, over total AI calls.
  int aiCalls = 0;
  int aiFallbacks = 0;

  double get aiFallbackRate => aiCalls == 0 ? 0 : aiFallbacks / aiCalls;

  void recordSignIn(String userId, DateTime at) =>
      signIns.add(SignInEvent(userId: userId, at: at));

  void recordAiCall({required bool fellBack}) {
    aiCalls++;
    if (fellBack) aiFallbacks++;
  }

  void recordSession({
    required String userId,
    required SkillType skill,
    required DateTime at,
    required int durationMs,
    required int wordCount,
  }) =>
      sessions.add(SessionRecord(
        userId: userId,
        skill: skill,
        at: at,
        durationMs: durationMs,
        wordCount: wordCount,
      ));

  void recordAttempt({
    required String userId,
    required String wordId,
    required SkillType skill,
    required bool passed,
    required int attemptNumber,
    required DateTime at,
  }) =>
      attempts.add(WordAttempt(
        userId: userId,
        wordId: wordId,
        skill: skill,
        passed: passed,
        attemptNumber: attemptNumber,
        at: at,
      ));

  DateTime? lastActivityFor(String userId) {
    final times = [
      ...signIns.where((e) => e.userId == userId).map((e) => e.at),
      ...sessions.where((e) => e.userId == userId).map((e) => e.at),
    ]..sort();
    return times.isEmpty ? null : times.last;
  }

  int signInCountFor(String userId) =>
      signIns.where((e) => e.userId == userId).length;

  /// Per-skill aggregate, optionally narrowed to one user.
  List<SkillStat> skillStats({String? userId, required List<SkillType> order}) {
    return [
      for (final skill in order)
        () {
          final skillAttempts = attempts.where((a) =>
              a.skill == skill && (userId == null || a.userId == userId));
          final skillSessions = sessions.where((s) =>
              s.skill == skill && (userId == null || s.userId == userId));
          return SkillStat(
            skill: skill,
            sessionsCompleted: skillSessions.length,
            wordsPassed: skillAttempts.where((a) => a.passed).length,
            wordsFailed: skillAttempts.where((a) => !a.passed).length,
            firstAttemptPasses: skillAttempts
                .where((a) => a.passed && a.attemptNumber == 1)
                .length,
            // Distinct words, not attempts — the denominator first-attempt
            // accuracy belongs over. A word that failed twice before passing
            // is three attempts and one word.
            wordsDecided:
                skillAttempts.map((a) => a.wordId).toSet().length,
          );
        }(),
    ];
  }
}

class SignInEvent {
  const SignInEvent({required this.userId, required this.at});

  final String userId;
  final DateTime at;
}

class SessionRecord {
  const SessionRecord({
    required this.userId,
    required this.skill,
    required this.at,
    required this.durationMs,
    required this.wordCount,
  });

  final String userId;
  final SkillType skill;
  final DateTime at;
  final int durationMs;
  final int wordCount;
}

class WordAttempt {
  const WordAttempt({
    required this.userId,
    required this.wordId,
    required this.skill,
    required this.passed,
    required this.attemptNumber,
    required this.at,
  });

  final String userId;
  final String wordId;
  final SkillType skill;
  final bool passed;
  final int attemptNumber;
  final DateTime at;
}
