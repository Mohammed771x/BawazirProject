import 'enums.dart';

class DailyProgress {
  const DailyProgress({required this.wordsAddedToday, required this.dailyTarget});

  final int wordsAddedToday;
  final int dailyTarget;

  double get ratio =>
      dailyTarget == 0 ? 0 : (wordsAddedToday / dailyTarget).clamp(0.0, 1.0);

  factory DailyProgress.fromJson(Map<String, dynamic> json) => DailyProgress(
        wordsAddedToday: (json['wordsAddedToday'] as num?)?.toInt() ?? 0,
        dailyTarget: (json['dailyTarget'] as num?)?.toInt() ?? 10,
      );

  Map<String, dynamic> toJson() => {
        'wordsAddedToday': wordsAddedToday,
        'dailyTarget': dailyTarget,
      };
}

/// One skill tile in the Skills Hub. `availability` is decided by the backend —
/// the client only renders it (rule R1).
class SkillCard {
  const SkillCard({
    required this.skill,
    required this.availability,
    required this.dueWordCount,
    required this.sessionWordCount,
    required this.level,
    required this.nextDueAt,
    this.activeSessionId,
  });

  final SkillType skill;
  final SkillAvailability availability;
  final int dueWordCount;
  final int sessionWordCount;

  /// Null for Spelling, which carries no CEFR band (ADR-008).
  final CefrLevel? level;
  final DateTime? nextDueAt;

  /// An unfinished session the learner can return to.
  ///
  /// Server-owned on purpose: the client stores nothing about sessions
  /// (rule R4), so a session survives the app being killed, a reinstall, and a
  /// move to another device.
  final String? activeSessionId;

  bool get hasOpenSession => activeSessionId != null;

  factory SkillCard.fromJson(Map<String, dynamic> json) => SkillCard(
        skill: SkillType.fromWire(json['skill'] as String?),
        availability: SkillAvailability.fromWire(json['availability'] as String?),
        dueWordCount: (json['dueWordCount'] as num?)?.toInt() ?? 0,
        sessionWordCount: (json['sessionWordCount'] as num?)?.toInt() ?? 0,
        level: CefrLevel.tryFromWire(json['level'] as String?),
        nextDueAt: DateTime.tryParse(json['nextDueAt'] as String? ?? '')?.toUtc(),
        activeSessionId: json['activeSessionId'] as String?,
      );

  Map<String, dynamic> toJson() => {
        'skill': skill.wire,
        'availability': availability.wire,
        'dueWordCount': dueWordCount,
        'sessionWordCount': sessionWordCount,
        'level': level?.wire,
        'nextDueAt': nextDueAt?.toIso8601String(),
        'activeSessionId': activeSessionId,
      };
}

class WeeklyReviewStatus {
  const WeeklyReviewStatus({
    required this.available,
    required this.wordCount,
    required this.periodStart,
    required this.nextAvailableAt,
  });

  final bool available;
  final int wordCount;
  final DateTime? periodStart;
  final DateTime? nextAvailableAt;

  factory WeeklyReviewStatus.fromJson(Map<String, dynamic> json) =>
      WeeklyReviewStatus(
        available: json['available'] as bool? ?? false,
        wordCount: (json['wordCount'] as num?)?.toInt() ?? 0,
        periodStart:
            DateTime.tryParse(json['periodStart'] as String? ?? '')?.toUtc(),
        nextAvailableAt:
            DateTime.tryParse(json['nextAvailableAt'] as String? ?? '')?.toUtc(),
      );

  Map<String, dynamic> toJson() => {
        'available': available,
        'wordCount': wordCount,
        'periodStart': periodStart?.toIso8601String(),
        'nextAvailableAt': nextAvailableAt?.toIso8601String(),
      };
}

class VocabularyCounts {
  const VocabularyCounts({
    required this.learning,
    required this.active,
    required this.archived,
  });

  final int learning;
  final int active;
  final int archived;

  factory VocabularyCounts.fromJson(Map<String, dynamic> json) =>
      VocabularyCounts(
        learning: (json['learning'] as num?)?.toInt() ?? 0,
        active: (json['active'] as num?)?.toInt() ?? 0,
        archived: (json['archived'] as num?)?.toInt() ?? 0,
      );

  Map<String, dynamic> toJson() => {
        'learning': learning,
        'active': active,
        'archived': archived,
      };
}

class HubState {
  const HubState({
    required this.dailyProgress,
    required this.skills,
    required this.weeklyReview,
    required this.vocabulary,
  });

  final DailyProgress dailyProgress;
  final List<SkillCard> skills;
  final WeeklyReviewStatus weeklyReview;
  final VocabularyCounts vocabulary;

  factory HubState.fromJson(Map<String, dynamic> json) => HubState(
        dailyProgress: DailyProgress.fromJson(
            json['dailyProgress'] as Map<String, dynamic>? ?? const {}),
        skills: (json['skills'] as List<dynamic>? ?? const [])
            .map((e) => SkillCard.fromJson(e as Map<String, dynamic>))
            .toList(),
        weeklyReview: WeeklyReviewStatus.fromJson(
            json['weeklyReview'] as Map<String, dynamic>? ?? const {}),
        vocabulary: VocabularyCounts.fromJson(
            json['vocabulary'] as Map<String, dynamic>? ?? const {}),
      );

  Map<String, dynamic> toJson() => {
        'dailyProgress': dailyProgress.toJson(),
        'skills': skills.map((e) => e.toJson()).toList(),
        'weeklyReview': weeklyReview.toJson(),
        'vocabulary': vocabulary.toJson(),
      };
}
