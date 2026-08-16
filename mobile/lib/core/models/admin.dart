import 'enums.dart';
import 'placement.dart';
import 'user.dart';

/// Owner-only analytics projections.
///
/// The MVP is an algorithm-validation experiment, not just an app: these shapes
/// exist to answer the questions in `MVP Core.txt` §57–61 — do words stick,
/// which skill causes drop-off, is the 2-day gap right, is 10 words/day right.
/// Every field here traces back to a metric named in the source documents; none
/// of it is invented for decoration.

/// One skill's aggregate performance across all users (`MVP Core.txt` §60).
class SkillStat {
  const SkillStat({
    required this.skill,
    required this.sessionsCompleted,
    required this.wordsPassed,
    required this.wordsFailed,
    required this.firstAttemptPasses,
  });

  final SkillType skill;
  final int sessionsCompleted;
  final int wordsPassed;
  final int wordsFailed;

  /// Words passed on their first attempt at this skill — the headline
  /// "First Attempt Accuracy" metric.
  final int firstAttemptPasses;

  int get wordsAttempted => wordsPassed + wordsFailed;

  double get passRate => wordsAttempted == 0 ? 0 : wordsPassed / wordsAttempted;

  double get failRate => wordsAttempted == 0 ? 0 : wordsFailed / wordsAttempted;

  double get firstAttemptAccuracy =>
      wordsAttempted == 0 ? 0 : firstAttemptPasses / wordsAttempted;

  factory SkillStat.fromJson(Map<String, dynamic> json) => SkillStat(
        skill: SkillType.fromWire(json['skill'] as String?),
        sessionsCompleted: (json['sessionsCompleted'] as num?)?.toInt() ?? 0,
        wordsPassed: (json['wordsPassed'] as num?)?.toInt() ?? 0,
        wordsFailed: (json['wordsFailed'] as num?)?.toInt() ?? 0,
        firstAttemptPasses: (json['firstAttemptPasses'] as num?)?.toInt() ?? 0,
      );

  Map<String, dynamic> toJson() => {
        'skill': skill.wire,
        'sessionsCompleted': sessionsCompleted,
        'wordsPassed': wordsPassed,
        'wordsFailed': wordsFailed,
        'firstAttemptPasses': firstAttemptPasses,
      };
}

/// How many users sit at each CEFR band for one skill.
class LevelDistribution {
  const LevelDistribution({required this.skill, required this.counts});

  final SkillType skill;
  final Map<CefrLevel, int> counts;

  int get total => counts.values.fold(0, (a, b) => a + b);

  factory LevelDistribution.fromJson(Map<String, dynamic> json) =>
      LevelDistribution(
        skill: SkillType.fromWire(json['skill'] as String?),
        counts: _parseCounts(json['counts']),
      );

  /// Unknown band strings are skipped rather than defaulting to A1, which would
  /// silently invent learners at the bottom of the chart.
  static Map<CefrLevel, int> _parseCounts(Object? raw) {
    final counts = <CefrLevel, int>{};
    if (raw is! Map) return counts;
    raw.forEach((key, value) {
      final level = CefrLevel.tryFromWire(key as String?);
      if (level != null && value is num) counts[level] = value.toInt();
    });
    return counts;
  }

  Map<String, dynamic> toJson() => {
        'skill': skill.wire,
        'counts': {
          for (final entry in counts.entries) entry.key.wire: entry.value,
        },
      };
}

class InterestCount {
  const InterestCount({
    required this.interest,
    required this.userCount,
    required this.isCustom,
  });

  final String interest;
  final int userCount;

  /// True when the interest was typed by a learner rather than picked from the
  /// catalogue — exactly the signal we want for growing the catalogue.
  final bool isCustom;

  factory InterestCount.fromJson(Map<String, dynamic> json) => InterestCount(
        interest: json['interest'] as String? ?? '',
        userCount: (json['userCount'] as num?)?.toInt() ?? 0,
        isCustom: json['isCustom'] as bool? ?? false,
      );

  Map<String, dynamic> toJson() => {
        'interest': interest,
        'userCount': userCount,
        'isCustom': isCustom,
      };
}

/// Global picture across all users (`MVP Core.txt` §57).
class AdminOverview {
  const AdminOverview({
    required this.userCount,
    required this.activeToday,
    required this.activeThisWeek,
    required this.wordsAddedTotal,
    required this.averageWordsPerUserPerDay,
    required this.averageSessionsPerUser,
    required this.averageSessionDurationMs,
    required this.pipelineCompletionRate,
    required this.skillStats,
    required this.levelDistributions,
    required this.topInterests,
    required this.aiFallbackRate,
  });

  final int userCount;
  final int activeToday;
  final int activeThisWeek;
  final int wordsAddedTotal;
  final double averageWordsPerUserPerDay;
  final double averageSessionsPerUser;
  final int averageSessionDurationMs;

  /// Share of words that started the pipeline and reached Active.
  final double pipelineCompletionRate;

  final List<SkillStat> skillStats;
  final List<LevelDistribution> levelDistributions;
  final List<InterestCount> topInterests;

  /// Share of AI calls that fell back to a deterministic scorer. A rising number
  /// here invalidates the evaluation data, so it is a first-class metric
  /// (`MVP Core.txt` §62).
  final double aiFallbackRate;

  /// Where failures concentrate — the "Failure Distribution" chart. Shares sum
  /// to 1 across skills that have any failures.
  Map<SkillType, double> get failureDistribution {
    final total = skillStats.fold(0, (sum, s) => sum + s.wordsFailed);
    if (total == 0) return {for (final s in skillStats) s.skill: 0};
    return {for (final s in skillStats) s.skill: s.wordsFailed / total};
  }

  factory AdminOverview.fromJson(Map<String, dynamic> json) => AdminOverview(
        userCount: (json['userCount'] as num?)?.toInt() ?? 0,
        activeToday: (json['activeToday'] as num?)?.toInt() ?? 0,
        activeThisWeek: (json['activeThisWeek'] as num?)?.toInt() ?? 0,
        wordsAddedTotal: (json['wordsAddedTotal'] as num?)?.toInt() ?? 0,
        averageWordsPerUserPerDay:
            (json['averageWordsPerUserPerDay'] as num?)?.toDouble() ?? 0,
        averageSessionsPerUser:
            (json['averageSessionsPerUser'] as num?)?.toDouble() ?? 0,
        averageSessionDurationMs:
            (json['averageSessionDurationMs'] as num?)?.toInt() ?? 0,
        pipelineCompletionRate:
            (json['pipelineCompletionRate'] as num?)?.toDouble() ?? 0,
        skillStats: (json['skillStats'] as List<dynamic>? ?? const [])
            .map((e) => SkillStat.fromJson(e as Map<String, dynamic>))
            .toList(),
        levelDistributions:
            (json['levelDistributions'] as List<dynamic>? ?? const [])
                .map((e) => LevelDistribution.fromJson(e as Map<String, dynamic>))
                .toList(),
        topInterests: (json['topInterests'] as List<dynamic>? ?? const [])
            .map((e) => InterestCount.fromJson(e as Map<String, dynamic>))
            .toList(),
        aiFallbackRate: (json['aiFallbackRate'] as num?)?.toDouble() ?? 0,
      );

  Map<String, dynamic> toJson() => {
        'userCount': userCount,
        'activeToday': activeToday,
        'activeThisWeek': activeThisWeek,
        'wordsAddedTotal': wordsAddedTotal,
        'averageWordsPerUserPerDay': averageWordsPerUserPerDay,
        'averageSessionsPerUser': averageSessionsPerUser,
        'averageSessionDurationMs': averageSessionDurationMs,
        'pipelineCompletionRate': pipelineCompletionRate,
        'skillStats': skillStats.map((e) => e.toJson()).toList(),
        'levelDistributions': levelDistributions.map((e) => e.toJson()).toList(),
        'topInterests': topInterests.map((e) => e.toJson()).toList(),
        'aiFallbackRate': aiFallbackRate,
      };
}

/// One row of the users list.
class AdminUserSummary {
  const AdminUserSummary({
    required this.id,
    required this.displayName,
    required this.email,
    required this.role,
    required this.createdAt,
    required this.lastActiveAt,
    required this.wordsTotal,
    required this.wordsActive,
    required this.sessionsCompleted,
  });

  final String id;
  final String displayName;
  final String email;
  final UserRole role;
  final DateTime createdAt;
  final DateTime? lastActiveAt;
  final int wordsTotal;
  final int wordsActive;
  final int sessionsCompleted;

  factory AdminUserSummary.fromJson(Map<String, dynamic> json) =>
      AdminUserSummary(
        id: json['id'] as String,
        displayName: json['displayName'] as String? ?? '',
        email: json['email'] as String? ?? '',
        role: UserRole.fromWire(json['role'] as String?),
        createdAt:
            DateTime.tryParse(json['createdAt'] as String? ?? '')?.toUtc() ??
                DateTime.now().toUtc(),
        lastActiveAt:
            DateTime.tryParse(json['lastActiveAt'] as String? ?? '')?.toUtc(),
        wordsTotal: (json['wordsTotal'] as num?)?.toInt() ?? 0,
        wordsActive: (json['wordsActive'] as num?)?.toInt() ?? 0,
        sessionsCompleted: (json['sessionsCompleted'] as num?)?.toInt() ?? 0,
      );

  Map<String, dynamic> toJson() => {
        'id': id,
        'displayName': displayName,
        'email': email,
        'role': role.wire,
        'createdAt': createdAt.toIso8601String(),
        'lastActiveAt': lastActiveAt?.toIso8601String(),
        'wordsTotal': wordsTotal,
        'wordsActive': wordsActive,
        'sessionsCompleted': sessionsCompleted,
      };
}

/// One day of a learner's activity (`MVP Core.txt` §59).
class AdminDailyRow {
  const AdminDailyRow({
    required this.date,
    required this.wordsAdded,
    required this.perSkillCompleted,
    required this.signedIn,
  });

  final DateTime date;
  final int wordsAdded;
  final Map<SkillType, int> perSkillCompleted;
  final bool signedIn;

  factory AdminDailyRow.fromJson(Map<String, dynamic> json) => AdminDailyRow(
        date: DateTime.tryParse(json['date'] as String? ?? '')?.toUtc() ??
            DateTime.now().toUtc(),
        wordsAdded: (json['wordsAdded'] as num?)?.toInt() ?? 0,
        perSkillCompleted: {
          for (final entry in (json['perSkillCompleted'] as Map?)
                  ?.cast<String, dynamic>()
                  .entries ??
              const <MapEntry<String, dynamic>>[])
            SkillType.fromWire(entry.key): (entry.value as num).toInt(),
        },
        signedIn: json['signedIn'] as bool? ?? false,
      );

  Map<String, dynamic> toJson() => {
        'date': date.toIso8601String(),
        'wordsAdded': wordsAdded,
        'perSkillCompleted': {
          for (final e in perSkillCompleted.entries) e.key.wire: e.value,
        },
        'signedIn': signedIn,
      };
}

/// A word the learner has got wrong, and where.
class AdminMistake {
  const AdminMistake({
    required this.wordId,
    required this.text,
    required this.meaning,
    required this.skill,
    required this.attempts,
    required this.lastFailedAt,
  });

  final String wordId;
  final String text;
  final String meaning;
  final SkillType skill;
  final int attempts;
  final DateTime? lastFailedAt;

  factory AdminMistake.fromJson(Map<String, dynamic> json) => AdminMistake(
        wordId: json['wordId'] as String? ?? '',
        text: json['text'] as String? ?? '',
        meaning: json['meaning'] as String? ?? '',
        skill: SkillType.fromWire(json['skill'] as String?),
        attempts: (json['attempts'] as num?)?.toInt() ?? 0,
        lastFailedAt:
            DateTime.tryParse(json['lastFailedAt'] as String? ?? '')?.toUtc(),
      );

  Map<String, dynamic> toJson() => {
        'wordId': wordId,
        'text': text,
        'meaning': meaning,
        'skill': skill.wire,
        'attempts': attempts,
        'lastFailedAt': lastFailedAt?.toIso8601String(),
      };
}

/// The complete journey of one learner (`MVP Core.txt` §58, Core Components §23).
class AdminUserDetail {
  const AdminUserDetail({
    required this.summary,
    required this.interests,
    required this.levels,
    required this.spelling,
    required this.wordsLearning,
    required this.wordsActive,
    required this.wordsArchived,
    required this.wordsAddedToday,
    required this.wordsAddedThisWeek,
    required this.wordsAddedThisMonth,
    required this.skillStats,
    required this.daily,
    required this.mistakes,
    required this.masteredWords,
    required this.signInCount,
    this.levelChanges = const [],
  });

  final AdminUserSummary summary;
  final List<String> interests;
  final List<SkillLevel> levels;
  final SpellingDiagnostic spelling;

  final int wordsLearning;
  final int wordsActive;
  final int wordsArchived;
  final int wordsAddedToday;
  final int wordsAddedThisWeek;
  final int wordsAddedThisMonth;

  final List<SkillStat> skillStats;
  final List<AdminDailyRow> daily;
  final List<AdminMistake> mistakes;
  final List<String> masteredWords;
  final int signInCount;

  /// Level history, newest last. The split between manual and system-validated
  /// changes is what answers "are the levels the system assigns realistic?"
  /// (`MVP Core.txt` §60).
  final List<LevelChangeRecord> levelChanges;

  int get systemLevelChanges => levelChanges
      .where((c) => c.changeType == LevelChangeType.systemValidated)
      .length;

  int get manualLevelChanges => levelChanges
      .where((c) => c.changeType == LevelChangeType.userManualChange)
      .length;

  factory AdminUserDetail.fromJson(Map<String, dynamic> json) =>
      AdminUserDetail(
        summary: AdminUserSummary.fromJson(
            json['summary'] as Map<String, dynamic>? ?? const {}),
        interests:
            (json['interests'] as List<dynamic>? ?? const []).cast<String>(),
        levels: (json['levels'] as List<dynamic>? ?? const [])
            .map((e) => SkillLevel.fromJson(e as Map<String, dynamic>))
            .toList(),
        spelling: SpellingDiagnostic.fromJson(
            json['spelling'] as Map<String, dynamic>? ?? const {}),
        wordsLearning: (json['wordsLearning'] as num?)?.toInt() ?? 0,
        wordsActive: (json['wordsActive'] as num?)?.toInt() ?? 0,
        wordsArchived: (json['wordsArchived'] as num?)?.toInt() ?? 0,
        wordsAddedToday: (json['wordsAddedToday'] as num?)?.toInt() ?? 0,
        wordsAddedThisWeek: (json['wordsAddedThisWeek'] as num?)?.toInt() ?? 0,
        wordsAddedThisMonth: (json['wordsAddedThisMonth'] as num?)?.toInt() ?? 0,
        skillStats: (json['skillStats'] as List<dynamic>? ?? const [])
            .map((e) => SkillStat.fromJson(e as Map<String, dynamic>))
            .toList(),
        daily: (json['daily'] as List<dynamic>? ?? const [])
            .map((e) => AdminDailyRow.fromJson(e as Map<String, dynamic>))
            .toList(),
        mistakes: (json['mistakes'] as List<dynamic>? ?? const [])
            .map((e) => AdminMistake.fromJson(e as Map<String, dynamic>))
            .toList(),
        masteredWords:
            (json['masteredWords'] as List<dynamic>? ?? const []).cast<String>(),
        signInCount: (json['signInCount'] as num?)?.toInt() ?? 0,
        levelChanges: (json['levelChanges'] as List<dynamic>? ?? const [])
            .map((e) => LevelChangeRecord.fromJson(e as Map<String, dynamic>))
            .toList(),
      );

  Map<String, dynamic> toJson() => {
        'summary': summary.toJson(),
        'interests': interests,
        'levels': levels.map((e) => e.toJson()).toList(),
        'spelling': spelling.toJson(),
        'wordsLearning': wordsLearning,
        'wordsActive': wordsActive,
        'wordsArchived': wordsArchived,
        'wordsAddedToday': wordsAddedToday,
        'wordsAddedThisWeek': wordsAddedThisWeek,
        'wordsAddedThisMonth': wordsAddedThisMonth,
        'skillStats': skillStats.map((e) => e.toJson()).toList(),
        'daily': daily.map((e) => e.toJson()).toList(),
        'mistakes': mistakes.map((e) => e.toJson()).toList(),
        'masteredWords': masteredWords,
        'signInCount': signInCount,
        'levelChanges': levelChanges.map((e) => e.toJson()).toList(),
      };
}
