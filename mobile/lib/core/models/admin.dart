import 'enums.dart';
import 'placement.dart';
import 'user.dart';
import 'word.dart';

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
    required this.wordsDecided,
  });

  final SkillType skill;
  final int sessionsCompleted;
  final int wordsPassed;
  final int wordsFailed;

  /// Words passed on their first attempt at this skill — the headline
  /// "First Attempt Accuracy" metric.
  final int firstAttemptPasses;

  /// Distinct words this skill has decided — passed or failed at least once.
  final int wordsDecided;

  /// Attempts, not words: a word that failed twice and then passed is three
  /// of these and one of [wordsDecided].
  int get wordsAttempted => wordsPassed + wordsFailed;

  double get passRate => wordsAttempted == 0 ? 0 : wordsPassed / wordsAttempted;

  double get failRate => wordsAttempted == 0 ? 0 : wordsFailed / wordsAttempted;

  /// Share of words that passed this skill without ever failing it.
  ///
  /// Over [wordsDecided] — words — because the numerator counts words. Divided
  /// by attempts instead, as it used to be, Speaking read 67% where the answer
  /// is 86%: every retry inflated the denominator by one while the numerator
  /// could not move.
  double get firstAttemptAccuracy =>
      wordsDecided == 0 ? 0 : firstAttemptPasses / wordsDecided;

  factory SkillStat.fromJson(Map<String, dynamic> json) => SkillStat(
        skill: SkillType.fromWire(json['skill'] as String?),
        sessionsCompleted: (json['sessionsCompleted'] as num?)?.toInt() ?? 0,
        wordsPassed: (json['wordsPassed'] as num?)?.toInt() ?? 0,
        wordsFailed: (json['wordsFailed'] as num?)?.toInt() ?? 0,
        firstAttemptPasses: (json['firstAttemptPasses'] as num?)?.toInt() ?? 0,
        wordsDecided: (json['wordsDecided'] as num?)?.toInt() ?? 0,
      );

  Map<String, dynamic> toJson() => {
        'skill': skill.wire,
        'sessionsCompleted': sessionsCompleted,
        'wordsPassed': wordsPassed,
        'wordsFailed': wordsFailed,
        'firstAttemptPasses': firstAttemptPasses,
        'wordsDecided': wordsDecided,
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
/// What a time skip actually moved.
class ScheduleAdvance {
  const ScheduleAdvance({
    required this.days,
    required this.wordsShifted,
    required this.skillsDueNow,
  });

  final int days;
  final int wordsShifted;

  /// How many skills are available *now* as a result — so the dashboard can
  /// say what the skip unlocked rather than only that it worked.
  final int skillsDueNow;

  factory ScheduleAdvance.fromJson(Map<String, dynamic> json) =>
      ScheduleAdvance(
        days: json['days'] as int? ?? 0,
        wordsShifted: json['wordsShifted'] as int? ?? 0,
        skillsDueNow: json['skillsDueNow'] as int? ?? 0,
      );
}

class AdminOverview {
  const AdminOverview({
    required this.userCount,
    required this.activeToday,
    required this.activeThisWeek,
    required this.wordsAddedTotal,
    required this.averageWordsPerUserPerDay,
    required this.averageSessionsPerUser,
    required this.averageSessionDurationMs,
    required this.medianSessionDurationMs,
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

  /// The middle session's duration — the one a person would recognise.
  ///
  /// Shown instead of the mean: a session is resumable, so one finished the
  /// next morning is a duration in hours, and a handful of those moved the
  /// mean from 16 seconds to 45 minutes.
  final int medianSessionDurationMs;

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
        medianSessionDurationMs:
            (json['medianSessionDurationMs'] as num?)?.toInt() ?? 0,
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
        'medianSessionDurationMs': medianSessionDurationMs,
        'pipelineCompletionRate': pipelineCompletionRate,
        'skillStats': skillStats.map((e) => e.toJson()).toList(),
        'levelDistributions': levelDistributions.map((e) => e.toJson()).toList(),
        'topInterests': topInterests.map((e) => e.toJson()).toList(),
        'aiFallbackRate': aiFallbackRate,
      };
}

/// One word in an Owner's view of a learner's vocabulary.
///
/// The mirror image of what the learner sees: My Words hides the pipeline
/// states because they are internal machinery (Part 2 §42), and this exists to
/// inspect exactly those (Part 3).
class AdminWord {
  const AdminWord({
    required this.id,
    required this.text,
    required this.meaning,
    required this.cefrLevel,
    required this.state,
    required this.currentSkill,
    required this.addedAt,
    required this.exposureCount,
    required this.skillsPassed,
    required this.attempts,
  });

  final String id;
  final String text;
  final String meaning;
  final CefrLevel cefrLevel;
  final WordState state;
  final SkillType? currentSkill;
  final DateTime addedAt;
  final int exposureCount;
  final int skillsPassed;
  final int attempts;

  factory AdminWord.fromJson(Map<String, dynamic> json) => AdminWord(
        id: json['id'] as String,
        text: json['text'] as String? ?? '',
        meaning: json['meaning'] as String? ?? '',
        cefrLevel: CefrLevel.fromWire(json['cefrLevel'] as String?),
        state: WordState.fromWire(json['state'] as String?),
        currentSkill: json['currentSkill'] == null
            ? null
            : SkillType.fromWire(json['currentSkill'] as String),
        addedAt: DateTime.tryParse(json['addedAt'] as String? ?? '')?.toUtc() ??
            DateTime.now().toUtc(),
        exposureCount: (json['exposureCount'] as num?)?.toInt() ?? 0,
        skillsPassed: (json['skillsPassed'] as num?)?.toInt() ?? 0,
        attempts: (json['attempts'] as num?)?.toInt() ?? 0,
      );
}

/// One page of an Owner's view of a learner's vocabulary.
class AdminWordPage {
  const AdminWordPage({
    required this.items,
    required this.total,
    this.hasMore = false,
  });

  final List<AdminWord> items;
  final int total;
  final bool hasMore;

  factory AdminWordPage.fromJson(Map<String, dynamic> json) => AdminWordPage(
        items: (json['items'] as List<dynamic>? ?? const [])
            .map((e) => AdminWord.fromJson((e as Map).cast<String, dynamic>()))
            .toList(),
        total: (json['total'] as num?)?.toInt() ?? 0,
        hasMore: json['hasMore'] as bool? ?? false,
      );
}

/// One word's whole life: added, attempted, passed, matured, archived.
///
/// Built from the append-only word event log, so a word that failed Reading
/// twice before passing shows all three events rather than only where it
/// ended up.
class AdminWordJourney {
  const AdminWordJourney({
    required this.word,
    required this.learnerName,
    required this.learnerId,
    required this.skills,
    required this.events,
    required this.exposures,
  });

  final AdminWord word;
  final String learnerName;
  final String learnerId;
  final List<WordSkillState> skills;
  final List<WordEvent> events;

  /// When this word was reused in generated content or a weekly review — a
  /// priority signal, never a limit (rule R8).
  final List<DateTime> exposures;

  factory AdminWordJourney.fromJson(Map<String, dynamic> json) {
    final learner = json['learner'] as Map<String, dynamic>? ?? const {};
    return AdminWordJourney(
      word: AdminWord.fromJson(
          (json['word'] as Map).cast<String, dynamic>()),
      learnerName: learner['displayName'] as String? ?? '',
      learnerId: learner['id'] as String? ?? '',
      skills: (json['skills'] as List<dynamic>? ?? const [])
          .map((e) => WordSkillState.fromJson(e as Map<String, dynamic>))
          .toList(),
      events: (json['events'] as List<dynamic>? ?? const [])
          .map((e) => WordEvent.fromJson(e as Map<String, dynamic>))
          .toList(),
      exposures: (json['exposures'] as List<dynamic>? ?? const [])
          .map((e) => DateTime.tryParse(
              (e as Map)['occurredAt'] as String? ?? ''))
          .whereType<DateTime>()
          .toList(),
    );
  }
}

/// One placement item as it was actually answered.
///
/// A CEFR band is a conclusion; this is the evidence behind it (Part 3). The
/// learner's own words are stored verbatim precisely so this can exist — a
/// score of 0.4 says nothing about *why*.
class PlacementEvidenceItem {
  const PlacementEvidenceItem({
    required this.itemId,
    required this.skill,
    required this.domain,
    required this.level,
    required this.difficulty,
    required this.score,
    required this.rawAnswer,
    required this.answeredAt,
    this.alsoEvidenceFor,
  });

  final String itemId;
  final SkillType skill;

  /// What the item measures — grammar and spelling included, even though
  /// neither is a visible skill.
  final String domain;

  final CefrLevel level;
  final double difficulty;

  /// Partial credit in [0, 1], computed server-side.
  final double score;

  final String? rawAnswer;
  final DateTime answeredAt;

  /// A second skill this answer counted towards, if any.
  final SkillType? alsoEvidenceFor;

  factory PlacementEvidenceItem.fromJson(Map<String, dynamic> json) =>
      PlacementEvidenceItem(
        itemId: json['itemId'] as String? ?? '',
        skill: SkillType.fromWire(json['skill'] as String?),
        domain: json['domain'] as String? ?? '',
        level: CefrLevel.fromWire(json['level'] as String?),
        difficulty: (json['difficulty'] as num?)?.toDouble() ?? 0,
        score: (json['score'] as num?)?.toDouble() ?? 0,
        rawAnswer: json['rawAnswer'] as String?,
        answeredAt:
            DateTime.tryParse(json['answeredAt'] as String? ?? '')?.toUtc() ??
                DateTime.now().toUtc(),
        alsoEvidenceFor: json['alsoEvidenceFor'] == null
            ? null
            : SkillType.fromWire(json['alsoEvidenceFor'] as String),
      );
}

/// Where a learner started against where they are now.
class PlacementProgressRow {
  const PlacementProgressRow({
    required this.skill,
    required this.initialLevel,
    required this.currentLevel,
    required this.confidence,
    required this.rollingAccuracy,
  });

  final SkillType skill;

  /// The band the placement test assigned. Null when placement never ran.
  final CefrLevel? initialLevel;

  /// Where they are now — system-validated where one exists (rule R6).
  final CefrLevel? currentLevel;

  final double confidence;
  final double rollingAccuracy;

  factory PlacementProgressRow.fromJson(Map<String, dynamic> json) =>
      PlacementProgressRow(
        skill: SkillType.fromWire(json['skill'] as String?),
        initialLevel: json['initialLevel'] == null
            ? null
            : CefrLevel.fromWire(json['initialLevel'] as String),
        currentLevel: json['currentLevel'] == null
            ? null
            : CefrLevel.fromWire(json['currentLevel'] as String),
        confidence: (json['confidence'] as num?)?.toDouble() ?? 0,
        rollingAccuracy: (json['rollingAccuracy'] as num?)?.toDouble() ?? 0,
      );
}

/// The placement test as the Owner sees it: the result, and its evidence.
class PlacementEvidence {
  const PlacementEvidence({
    required this.completed,
    required this.testVersion,
    required this.fallbackScoredCount,
    required this.progress,
    required this.answers,
    this.completedAt,
  });

  final bool completed;

  /// Which item bank produced this result. A result from an older version is
  /// not comparable to a current one, and without the stamp there is no way to
  /// tell which you are looking at.
  final int testVersion;

  /// Free-text answers scored offline rather than by the AI evaluator — the
  /// caveat that belongs beside the result.
  final int fallbackScoredCount;

  final List<PlacementProgressRow> progress;
  final List<PlacementEvidenceItem> answers;
  final DateTime? completedAt;

  factory PlacementEvidence.fromJson(Map<String, dynamic> json) {
    final progress = json['progress'] as Map<String, dynamic>? ?? const {};
    return PlacementEvidence(
      completed: json['completed'] as bool? ?? false,
      testVersion: (json['testVersion'] as num?)?.toInt() ?? 0,
      fallbackScoredCount:
          (json['fallbackScoredCount'] as num?)?.toInt() ?? 0,
      progress: (progress['levels'] as List<dynamic>? ?? const [])
          .map((e) =>
              PlacementProgressRow.fromJson((e as Map).cast<String, dynamic>()))
          .toList(),
      answers: (json['answers'] as List<dynamic>? ?? const [])
          .map((e) => PlacementEvidenceItem.fromJson(
              (e as Map).cast<String, dynamic>()))
          .toList(),
      completedAt:
          DateTime.tryParse(json['completedAt'] as String? ?? '')?.toUtc(),
    );
  }
}

/// One page of the learners list (Part 3 §37).
class AdminUserPage {
  const AdminUserPage({
    required this.items,
    required this.total,
    this.page = 0,
    this.hasMore = false,
  });

  final List<AdminUserSummary> items;

  /// How many learners match the search and window — not how many are on this
  /// page, which is what the Owner is actually asking.
  final int total;

  final int page;
  final bool hasMore;

  factory AdminUserPage.fromJson(Map<String, dynamic> json) => AdminUserPage(
        items: (json['items'] as List<dynamic>? ?? const [])
            .map((e) =>
                AdminUserSummary.fromJson((e as Map).cast<String, dynamic>()))
            .toList(),
        total: (json['total'] as num?)?.toInt() ?? 0,
        page: (json['page'] as num?)?.toInt() ?? 0,
        hasMore: json['hasMore'] as bool? ?? false,
      );
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
    this.phone,
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

  /// International form, Owner-only, null when they gave no number (ADR-053).
  ///
  /// Here so the Owner reading a bug report can reach the person who sent it —
  /// and, as asked for, gather numbers for a group.
  final String? phone;

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
        phone: json['phone'] as String?,
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
        'phone': phone,
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

/// One message a learner sent the Owner (ADR-053).
///
/// The body is whatever they typed. It is rendered as text and never
/// interpreted — there is no markup, no link handling and no HTML anywhere in
/// this path.
class FeedbackMessage {
  const FeedbackMessage({
    required this.id,
    required this.body,
    required this.handled,
    required this.createdAt,
    required this.senderName,
    required this.senderEmail,
    this.senderId,
    this.senderPhone,
    this.appVersion,
    this.platform,
  });

  final String id;
  final String body;
  final bool handled;
  final DateTime createdAt;

  final String senderName;
  final String senderEmail;

  /// Null when the account has been removed since — the message survives it.
  final String? senderId;

  /// International form, or null when they never gave one.
  final String? senderPhone;

  /// What they were running, so "it crashed" comes with a build attached.
  final String? appVersion;
  final String? platform;

  factory FeedbackMessage.fromJson(Map<String, dynamic> json) {
    final user = json['user'] as Map<String, dynamic>?;

    return FeedbackMessage(
      id: json['id'] as String,
      body: json['body'] as String? ?? '',
      handled: (json['status'] as String?)?.toUpperCase() == 'HANDLED',
      createdAt:
          DateTime.tryParse(json['createdAt'] as String? ?? '')?.toUtc() ??
              DateTime.now().toUtc(),
      senderId: user?['id'] as String?,
      senderName: user?['displayName'] as String? ?? '',
      senderEmail: user?['email'] as String? ?? '',
      senderPhone: _phone(user),
      appVersion: json['appVersion'] as String?,
      platform: json['platform'] as String?,
    );
  }

  static String? _phone(Map<String, dynamic>? user) {
    final number = user?['phoneNumber'] as String?;
    if (number == null || number.isEmpty) return null;
    final code = user?['phoneCountryCode'] as String? ?? '';
    return '+$code$number';
  }

  Map<String, dynamic> toJson() => {
        'id': id,
        'body': body,
        'status': handled ? 'HANDLED' : 'NEW',
        'createdAt': createdAt.toIso8601String(),
        'appVersion': appVersion,
        'platform': platform,
        'user': {
          'id': senderId,
          'displayName': senderName,
          'email': senderEmail,
          if (senderPhone != null) 'phoneNumber': senderPhone,
        },
      };
}

/// A page of feedback, with the count that decides whether the tab shows a dot.
class FeedbackPage {
  const FeedbackPage({
    required this.items,
    required this.total,
    required this.unread,
    required this.hasMore,
  });

  final List<FeedbackMessage> items;
  final int total;
  final int unread;
  final bool hasMore;

  factory FeedbackPage.fromJson(Map<String, dynamic> json) => FeedbackPage(
        items: (json['items'] as List<dynamic>? ?? [])
            .map((e) => FeedbackMessage.fromJson(e as Map<String, dynamic>))
            .toList(),
        total: (json['total'] as num?)?.toInt() ?? 0,
        unread: (json['unread'] as num?)?.toInt() ?? 0,
        hasMore: json['hasMore'] as bool? ?? false,
      );
}
