import '../../core/api/wordos_api.dart';
import '../../core/models/models.dart';
import 'mock_analytics.dart';
import 'mock_engine.dart';

/// ⚠️ DISPOSABLE DEVELOPMENT COMPONENT — Phase 8 owns this for real.
///
/// Builds the Owner dashboard projections and, crucially, **enforces the role
/// check here rather than in the UI**. A normal user who calls an admin endpoint
/// directly is refused (`FORBIDDEN`), which is the behaviour the real backend
/// must have; hiding a nav item is not access control.
class MockAdmin {
  const MockAdmin(this._engine, this._analytics);

  final MockEngine _engine;
  final MockAnalytics _analytics;

  static const int _dailyRowCount = 14;

  void requireOwner(MockUser caller) {
    if (caller.role != UserRole.owner) {
      throw const ApiException(
        'FORBIDDEN',
        'This area is restricted to the system owner.',
        statusCode: 403,
      );
    }
  }

  // ── Overview ──────────────────────────────────────────────────────────────

  AdminOverview overview(MockUser caller, List<MockUser> users) {
    requireOwner(caller);

    final now = _engine.now;
    final learners = users.where((u) => u.role == UserRole.user).toList();
    final order = MockEngine.configuration.skillsOrder;

    final allWords = [for (final u in learners) ...u.words];
    final activeWords =
        allWords.where((w) => w.state == WordState.active).length;

    final oldestSignup = learners.isEmpty
        ? now
        : learners.map((u) => u.createdAt).reduce((a, b) => a.isBefore(b) ? a : b);
    final daysObserved = (now.difference(oldestSignup).inDays).clamp(1, 3650);

    return AdminOverview(
      userCount: learners.length,
      activeToday: learners
          .where((u) => _isSameDay(_analytics.lastActivityFor(u.id), now))
          .length,
      activeThisWeek: learners.where((u) {
        final last = _analytics.lastActivityFor(u.id);
        return last != null && now.difference(last).inDays < 7;
      }).length,
      wordsAddedTotal: allWords.length,
      averageWordsPerUserPerDay: learners.isEmpty
          ? 0
          : allWords.length / learners.length / daysObserved,
      averageSessionsPerUser: learners.isEmpty
          ? 0
          : _analytics.sessions.length / learners.length,
      averageSessionDurationMs: _analytics.sessions.isEmpty
          ? 0
          : (_analytics.sessions
                      .map((s) => s.durationMs)
                      .reduce((a, b) => a + b) /
                  _analytics.sessions.length)
              .round(),
      pipelineCompletionRate:
          allWords.isEmpty ? 0 : activeWords / allWords.length,
      skillStats: _analytics.skillStats(order: order),
      levelDistributions: [
        for (final skill in order)
          // Spelling carries no CEFR band, so it has no distribution to show
          // (ADR-008) — it is reported through accuracy instead.
          if (skill != SkillType.spelling)
            LevelDistribution(
              skill: skill,
              counts: _countLevels(learners, skill),
            ),
      ],
      topInterests: _interestCounts(learners),
      aiFallbackRate: _analytics.aiFallbackRate,
    );
  }

  Map<CefrLevel, int> _countLevels(List<MockUser> users, SkillType skill) {
    final counts = <CefrLevel, int>{};
    for (final user in users) {
      final level = user.levels[skill]?.systemAssessedLevel;
      if (level != null) counts[level] = (counts[level] ?? 0) + 1;
    }
    return counts;
  }

  List<InterestCount> _interestCounts(List<MockUser> users) {
    final catalogue = {for (final o in MockEngine.interestOptions) o.slug};
    final counts = <String, int>{};
    for (final user in users) {
      for (final interest in user.interests.toSet()) {
        counts[interest] = (counts[interest] ?? 0) + 1;
      }
    }
    final list = [
      for (final entry in counts.entries)
        InterestCount(
          interest: entry.key,
          userCount: entry.value,
          isCustom: !catalogue.contains(entry.key),
        ),
    ]..sort((a, b) => b.userCount.compareTo(a.userCount));
    return list;
  }

  // ── Users ─────────────────────────────────────────────────────────────────

  List<AdminUserSummary> users(MockUser caller, List<MockUser> users) {
    requireOwner(caller);
    final list = users.map(_summary).toList()
      ..sort((a, b) {
        final aAt = a.lastActiveAt ?? a.createdAt;
        final bAt = b.lastActiveAt ?? b.createdAt;
        return bAt.compareTo(aAt);
      });
    return list;
  }

  AdminUserSummary _summary(MockUser user) => AdminUserSummary(
        id: user.id,
        displayName: user.displayName,
        email: user.email,
        role: user.role,
        createdAt: user.createdAt,
        lastActiveAt: _analytics.lastActivityFor(user.id),
        wordsTotal: user.words.length,
        wordsActive:
            user.words.where((w) => w.state == WordState.active).length,
        sessionsCompleted:
            _analytics.sessions.where((s) => s.userId == user.id).length,
      );

  AdminUserDetail userDetail(MockUser caller, MockUser? target) {
    requireOwner(caller);
    if (target == null) {
      throw const ApiException('NOT_FOUND', 'User not found.', statusCode: 404);
    }

    final now = _engine.now;
    final order = MockEngine.configuration.skillsOrder;

    int addedWithin(Duration window) => target.words
        .where((w) => now.difference(w.addedAt) <= window)
        .length;

    return AdminUserDetail(
      summary: _summary(target),
      interests: List.of(target.interests),
      levels: order.map((s) => target.levels[s]!).toList(),
      spelling: target.spellingDiagnostic ??
          const SpellingDiagnostic(
            itemsAnswered: 0,
            correct: 0,
            supportMode: SpellingInputMode.letterTiles,
          ),
      wordsLearning:
          target.words.where((w) => w.state == WordState.learning).length,
      wordsActive: target.words.where((w) => w.state == WordState.active).length,
      wordsArchived:
          target.words.where((w) => w.state == WordState.archived).length,
      wordsAddedToday:
          target.words.where((w) => _isSameDay(w.addedAt, now)).length,
      wordsAddedThisWeek: addedWithin(const Duration(days: 7)),
      wordsAddedThisMonth: addedWithin(const Duration(days: 30)),
      skillStats: _analytics.skillStats(userId: target.id, order: order),
      daily: _dailyRows(target, now, order),
      mistakes: _mistakes(target),
      masteredWords: target.words
          .where((w) => w.state == WordState.active)
          .map((w) => w.text)
          .toList(),
      signInCount: _analytics.signInCountFor(target.id),
      levelChanges: List.of(target.levelChanges),
    );
  }

  /// Day-by-day activity (`MVP Core.txt` §59), newest last.
  List<AdminDailyRow> _dailyRows(
    MockUser user,
    DateTime now,
    List<SkillType> order,
  ) {
    return [
      for (var back = _dailyRowCount - 1; back >= 0; back--)
        () {
          final day = now.subtract(Duration(days: back));
          return AdminDailyRow(
            date: DateTime.utc(day.year, day.month, day.day),
            wordsAdded:
                user.words.where((w) => _isSameDay(w.addedAt, day)).length,
            perSkillCompleted: {
              for (final skill in order)
                skill: _analytics.attempts
                    .where((a) =>
                        a.userId == user.id &&
                        a.skill == skill &&
                        _isSameDay(a.at, day))
                    .length,
            },
            signedIn: _analytics.signIns
                .any((e) => e.userId == user.id && _isSameDay(e.at, day)),
          );
        }(),
    ];
  }

  /// Words the learner has failed at least once, worst first.
  List<AdminMistake> _mistakes(MockUser user) {
    final failures = _analytics.attempts
        .where((a) => a.userId == user.id && !a.passed)
        .toList();

    final byKey = <String, List<WordAttempt>>{};
    for (final failure in failures) {
      byKey.putIfAbsent('${failure.wordId}|${failure.skill.wire}', () => [])
          .add(failure);
    }

    final mistakes = <AdminMistake>[];
    for (final entry in byKey.entries) {
      final first = entry.value.first;
      final word = user.words.where((w) => w.id == first.wordId).firstOrNull;
      if (word == null) continue;
      mistakes.add(
        AdminMistake(
          wordId: word.id,
          text: word.text,
          meaning: word.meaning,
          skill: first.skill,
          attempts: entry.value.length,
          lastFailedAt: entry.value.map((a) => a.at).reduce(
              (a, b) => a.isAfter(b) ? a : b),
        ),
      );
    }

    mistakes.sort((a, b) => b.attempts.compareTo(a.attempts));
    return mistakes;
  }

  static bool _isSameDay(DateTime? a, DateTime b) =>
      a != null && a.year == b.year && a.month == b.month && a.day == b.day;
}
