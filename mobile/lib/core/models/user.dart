import 'enums.dart';

/// The per-skill level record. Levels are independent per skill, and the
/// user-selected level is deliberately kept separate from the system-assessed
/// one — only the latter may drive progression/archiving decisions (rule R6).
///
/// Both levels are **nullable**: Spelling is a measured skill but not a CEFR
/// scale, so its row carries accuracy and a daily target with no band at all
/// (ADR-008). Use [carriesCefrLevel] rather than null-checking at call sites.
class SkillLevel {
  const SkillLevel({
    required this.skill,
    required this.userSelectedLevel,
    required this.systemAssessedLevel,
    required this.evaluationSessions,
    required this.rollingAccuracy,
    required this.dailyTargetWords,
    this.confidence = 0,
  });

  /// A skill that is measured but never assigned a CEFR band.
  const SkillLevel.unlevelled({
    required this.skill,
    required this.evaluationSessions,
    required this.rollingAccuracy,
    required this.dailyTargetWords,
    this.confidence = 0,
  })  : userSelectedLevel = null,
        systemAssessedLevel = null;

  final SkillType skill;
  final CefrLevel? userSelectedLevel;
  final CefrLevel? systemAssessedLevel;
  final int evaluationSessions;
  final double rollingAccuracy;
  final int dailyTargetWords;

  /// Placement confidence in `[0, 1]` — how tightly the assessment pinned this
  /// level down. Low confidence is surfaced to the learner, not hidden.
  final double confidence;

  bool get carriesCefrLevel => skill != SkillType.spelling;

  factory SkillLevel.fromJson(Map<String, dynamic> json) => SkillLevel(
        skill: SkillType.fromWire(json['skill'] as String?),
        userSelectedLevel: CefrLevel.tryFromWire(json['userSelectedLevel'] as String?),
        systemAssessedLevel:
            CefrLevel.tryFromWire(json['systemAssessedLevel'] as String?),
        evaluationSessions: (json['evaluationSessions'] as num?)?.toInt() ?? 0,
        rollingAccuracy: (json['rollingAccuracy'] as num?)?.toDouble() ?? 0,
        dailyTargetWords: (json['dailyTargetWords'] as num?)?.toInt() ?? 10,
        confidence: (json['confidence'] as num?)?.toDouble() ?? 0,
      );

  Map<String, dynamic> toJson() => {
        'skill': skill.wire,
        'userSelectedLevel': userSelectedLevel?.wire,
        'systemAssessedLevel': systemAssessedLevel?.wire,
        'evaluationSessions': evaluationSessions,
        'rollingAccuracy': rollingAccuracy,
        'dailyTargetWords': dailyTargetWords,
        'confidence': confidence,
      };

  SkillLevel copyWith({
    CefrLevel? userSelectedLevel,
    CefrLevel? systemAssessedLevel,
    int? evaluationSessions,
    double? rollingAccuracy,
    int? dailyTargetWords,
    double? confidence,
  }) =>
      SkillLevel(
        skill: skill,
        userSelectedLevel: userSelectedLevel ?? this.userSelectedLevel,
        systemAssessedLevel: systemAssessedLevel ?? this.systemAssessedLevel,
        evaluationSessions: evaluationSessions ?? this.evaluationSessions,
        rollingAccuracy: rollingAccuracy ?? this.rollingAccuracy,
        dailyTargetWords: dailyTargetWords ?? this.dailyTargetWords,
        confidence: confidence ?? this.confidence,
      );
}

class UserProfile {
  const UserProfile({
    required this.id,
    required this.email,
    required this.displayName,
    required this.role,
    required this.onboardingStage,
    required this.interests,
    required this.skillLevels,
    required this.createdAt,
  });

  final String id;
  final String email;
  final String displayName;
  final UserRole role;
  final OnboardingStage onboardingStage;
  final List<String> interests;
  final List<SkillLevel> skillLevels;
  final DateTime createdAt;

  SkillLevel levelFor(SkillType skill) => skillLevels.firstWhere(
        (l) => l.skill == skill,
        orElse: () => SkillLevel(
          skill: skill,
          userSelectedLevel: CefrLevel.a1,
          systemAssessedLevel: CefrLevel.a1,
          evaluationSessions: 0,
          rollingAccuracy: 0,
          dailyTargetWords: 10,
        ),
      );

  factory UserProfile.fromJson(Map<String, dynamic> json) => UserProfile(
        id: json['id'] as String,
        email: json['email'] as String? ?? '',
        displayName: json['displayName'] as String? ?? '',
        role: UserRole.fromWire(json['role'] as String?),
        onboardingStage:
            OnboardingStage.fromWire(json['onboardingStage'] as String?),
        interests:
            (json['interests'] as List<dynamic>? ?? const []).cast<String>(),
        skillLevels: (json['skillLevels'] as List<dynamic>? ?? const [])
            .map((e) => SkillLevel.fromJson(e as Map<String, dynamic>))
            .toList(),
        createdAt:
            DateTime.tryParse(json['createdAt'] as String? ?? '')?.toUtc() ??
                DateTime.now().toUtc(),
      );

  Map<String, dynamic> toJson() => {
        'id': id,
        'email': email,
        'displayName': displayName,
        'role': role.wire,
        'onboardingStage': onboardingStage.wire,
        'interests': interests,
        'skillLevels': skillLevels.map((e) => e.toJson()).toList(),
        'createdAt': createdAt.toIso8601String(),
      };

  UserProfile copyWith({
    OnboardingStage? onboardingStage,
    List<String>? interests,
    List<SkillLevel>? skillLevels,
    String? displayName,
  }) =>
      UserProfile(
        id: id,
        email: email,
        displayName: displayName ?? this.displayName,
        role: role,
        onboardingStage: onboardingStage ?? this.onboardingStage,
        interests: interests ?? this.interests,
        skillLevels: skillLevels ?? this.skillLevels,
        createdAt: createdAt,
      );
}

class AuthResponse {
  const AuthResponse({
    required this.token,
    required this.user,
    this.refreshToken,
    this.expiresAt,
  });

  final String token;
  final UserProfile user;

  /// Long-lived and rotated on every use. Null only for the mock backend,
  /// which issues no refresh tokens.
  final String? refreshToken;

  /// When the access token stops being accepted. Advisory only — the server
  /// decides, and the client refreshes on a 401 regardless.
  final DateTime? expiresAt;

  factory AuthResponse.fromJson(Map<String, dynamic> json) => AuthResponse(
        token: json['token'] as String,
        refreshToken: json['refreshToken'] as String?,
        expiresAt: DateTime.tryParse(json['expiresAt'] as String? ?? '')?.toUtc(),
        user: UserProfile.fromJson(json['user'] as Map<String, dynamic>),
      );

  Map<String, dynamic> toJson() => {
        'token': token,
        'refreshToken': refreshToken,
        'expiresAt': expiresAt?.toIso8601String(),
        'user': user.toJson(),
      };
}

class InterestOption {
  const InterestOption({
    required this.slug,
    required this.labelEn,
    required this.labelAr,
    required this.emoji,
  });

  final String slug;
  final String labelEn;
  final String labelAr;
  final String emoji;

  factory InterestOption.fromJson(Map<String, dynamic> json) => InterestOption(
        slug: json['slug'] as String,
        labelEn: json['labelEn'] as String? ?? json['slug'] as String,
        labelAr: json['labelAr'] as String? ?? json['slug'] as String,
        emoji: json['emoji'] as String? ?? '•',
      );

  Map<String, dynamic> toJson() => {
        'slug': slug,
        'labelEn': labelEn,
        'labelAr': labelAr,
        'emoji': emoji,
      };
}

/// Client-visible tunables. These are *display* values — the backend still owns
/// every rule that uses them (rule R3 + R1).
/// One entry in the level history — the audit trail behind rule R6.
///
/// Keeping manual and system-validated changes in one list, distinguished by
/// [changeType], is what lets the Owner dashboard answer "are the levels the
/// system assigns realistic?" (`MVP Core.txt` §60).
class LevelChangeRecord {
  const LevelChangeRecord({
    required this.skill,
    required this.previous,
    required this.next,
    required this.changeType,
    required this.accuracy,
    required this.sessionsConsidered,
    required this.createdAt,
  });

  final SkillType skill;
  final CefrLevel? previous;
  final CefrLevel? next;
  final LevelChangeType changeType;
  final double accuracy;
  final int sessionsConsidered;
  final DateTime createdAt;

  factory LevelChangeRecord.fromJson(Map<String, dynamic> json) =>
      LevelChangeRecord(
        skill: SkillType.fromWire(json['skill'] as String?),
        previous: CefrLevel.tryFromWire(json['previousLevel'] as String?),
        next: CefrLevel.tryFromWire(json['newLevel'] as String?),
        changeType: LevelChangeType.fromWire(json['changeType'] as String?),
        accuracy: (json['accuracy'] as num?)?.toDouble() ?? 0,
        sessionsConsidered:
            (json['sessionsConsidered'] as num?)?.toInt() ?? 0,
        createdAt:
            DateTime.tryParse(json['createdAt'] as String? ?? '')?.toUtc() ??
                DateTime.now().toUtc(),
      );

  Map<String, dynamic> toJson() => {
        'skill': skill.wire,
        'previousLevel': previous?.wire,
        'newLevel': next?.wire,
        'changeType': changeType.wire,
        'accuracy': accuracy,
        'sessionsConsidered': sessionsConsidered,
        'createdAt': createdAt.toIso8601String(),
      };
}

class PublicConfig {
  const PublicConfig({
    required this.skillIntervalDays,
    required this.minDailyTarget,
    required this.maxDailyTarget,
    required this.defaultDailyTarget,
    required this.skillsOrder,
    required this.weeklyReviewPeriodDays,
  });

  final int skillIntervalDays;
  final int minDailyTarget;
  final int maxDailyTarget;
  final int defaultDailyTarget;
  final List<SkillType> skillsOrder;
  final int weeklyReviewPeriodDays;

  static const PublicConfig fallback = PublicConfig(
    skillIntervalDays: 2,
    minDailyTarget: 5,
    maxDailyTarget: 15,
    defaultDailyTarget: 10,
    skillsOrder: [
      SkillType.reading,
      SkillType.listening,
      SkillType.speaking,
      SkillType.writing,
      SkillType.spelling,
    ],
    weeklyReviewPeriodDays: 7,
  );

  factory PublicConfig.fromJson(Map<String, dynamic> json) => PublicConfig(
        skillIntervalDays: (json['skillIntervalDays'] as num?)?.toInt() ?? 2,
        minDailyTarget: (json['minDailyTarget'] as num?)?.toInt() ?? 5,
        maxDailyTarget: (json['maxDailyTarget'] as num?)?.toInt() ?? 15,
        defaultDailyTarget: (json['defaultDailyTarget'] as num?)?.toInt() ?? 10,
        skillsOrder: (json['skillsOrder'] as List<dynamic>? ?? const [])
            .map((e) => SkillType.fromWire(e as String))
            .toList(),
        weeklyReviewPeriodDays:
            (json['weeklyReviewPeriodDays'] as num?)?.toInt() ?? 7,
      );

  Map<String, dynamic> toJson() => {
        'skillIntervalDays': skillIntervalDays,
        'minDailyTarget': minDailyTarget,
        'maxDailyTarget': maxDailyTarget,
        'defaultDailyTarget': defaultDailyTarget,
        'skillsOrder': skillsOrder.map((e) => e.wire).toList(),
        'weeklyReviewPeriodDays': weeklyReviewPeriodDays,
      };
}
