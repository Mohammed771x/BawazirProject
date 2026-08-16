import 'enums.dart';
import 'user.dart';

/// One question as the client sees it.
///
/// The item's difficulty band is deliberately **not** part of this projection:
/// showing a learner that they are being asked a "C1 item" changes how they
/// answer, and the client has no legitimate use for it (rule R1). Options
/// arrive already shuffled by the server (rule R7).
class PlacementItem {
  const PlacementItem({
    required this.id,
    required this.skill,
    required this.type,
    required this.prompt,
    required this.options,
    required this.passage,
    required this.audioText,
  });

  final String id;
  final SkillType skill;
  final PlacementItemType type;
  final String prompt;
  final List<String> options;
  final String? passage;

  /// Text that the client speaks with TTS for listening items.
  final String? audioText;

  factory PlacementItem.fromJson(Map<String, dynamic> json) => PlacementItem(
        id: json['id'] as String,
        skill: SkillType.fromWire(json['skill'] as String?),
        type: PlacementItemType.fromWire(json['type'] as String?),
        prompt: json['prompt'] as String? ?? '',
        options: (json['options'] as List<dynamic>? ?? const []).cast<String>(),
        passage: json['passage'] as String?,
        audioText: json['audioText'] as String?,
      );

  Map<String, dynamic> toJson() => {
        'id': id,
        'skill': skill.wire,
        'type': type.wire,
        'prompt': prompt,
        'options': options,
        'passage': passage,
        'audioText': audioText,
      };
}

/// How far through the adaptive test the learner is.
///
/// [estimatedTotal] is an estimate on purpose — an adaptive test stops as soon
/// as it is confident, so the real total is not known in advance. The UI shows
/// it as an approximation rather than a hard count.
class PlacementProgress {
  const PlacementProgress({
    required this.answered,
    required this.estimatedTotal,
    required this.currentSkill,
    required this.skillIndex,
    required this.skillCount,
  });

  final int answered;
  final int estimatedTotal;
  final SkillType currentSkill;
  final int skillIndex;
  final int skillCount;

  double get ratio =>
      estimatedTotal == 0 ? 0 : (answered / estimatedTotal).clamp(0.0, 1.0);

  factory PlacementProgress.fromJson(Map<String, dynamic> json) =>
      PlacementProgress(
        answered: (json['answered'] as num?)?.toInt() ?? 0,
        estimatedTotal: (json['estimatedTotal'] as num?)?.toInt() ?? 0,
        currentSkill: SkillType.fromWire(json['currentSkill'] as String?),
        skillIndex: (json['skillIndex'] as num?)?.toInt() ?? 0,
        skillCount: (json['skillCount'] as num?)?.toInt() ?? 0,
      );

  Map<String, dynamic> toJson() => {
        'answered': answered,
        'estimatedTotal': estimatedTotal,
        'currentSkill': currentSkill.wire,
        'skillIndex': skillIndex,
        'skillCount': skillCount,
      };
}

/// The state of an adaptive placement test after starting or answering.
///
/// `item == null && isComplete` means the test has finished and the client
/// should ask for the result.
class PlacementStep {
  const PlacementStep({
    required this.sessionId,
    required this.item,
    required this.progress,
    required this.isComplete,
  });

  final String sessionId;
  final PlacementItem? item;
  final PlacementProgress progress;
  final bool isComplete;

  factory PlacementStep.fromJson(Map<String, dynamic> json) => PlacementStep(
        sessionId: json['sessionId'] as String,
        item: json['item'] == null
            ? null
            : PlacementItem.fromJson(json['item'] as Map<String, dynamic>),
        progress: PlacementProgress.fromJson(
            json['progress'] as Map<String, dynamic>? ?? const {}),
        isComplete: json['isComplete'] as bool? ?? false,
      );

  Map<String, dynamic> toJson() => {
        'sessionId': sessionId,
        'item': item?.toJson(),
        'progress': progress.toJson(),
        'isComplete': isComplete,
      };
}

/// What Spelling produces instead of a CEFR band (ADR-008).
class SpellingDiagnostic {
  const SpellingDiagnostic({
    required this.itemsAnswered,
    required this.correct,
    required this.supportMode,
  });

  final int itemsAnswered;
  final int correct;

  /// Which input affordance Spelling sessions should start the learner on.
  final SpellingInputMode supportMode;

  double get accuracy => itemsAnswered == 0 ? 0 : correct / itemsAnswered;

  factory SpellingDiagnostic.fromJson(Map<String, dynamic> json) =>
      SpellingDiagnostic(
        itemsAnswered: (json['itemsAnswered'] as num?)?.toInt() ?? 0,
        correct: (json['correct'] as num?)?.toInt() ?? 0,
        supportMode:
            SpellingInputMode.fromWire(json['supportMode'] as String?),
      );

  Map<String, dynamic> toJson() => {
        'itemsAnswered': itemsAnswered,
        'correct': correct,
        'supportMode': supportMode.wire,
      };
}

/// Levels are computed server-side (ADR-007) — the client only displays them.
class PlacementResult {
  const PlacementResult({
    required this.levels,
    required this.spelling,
    required this.summary,
  });

  final List<SkillLevel> levels;
  final SpellingDiagnostic spelling;
  final String summary;

  /// True when at least one skill was placed with low confidence, in which case
  /// the UI tells the learner the level is provisional rather than pretending
  /// to a precision the test did not reach (demo review §6, question 9).
  bool get hasLowConfidence => levels
      .where((l) => l.carriesCefrLevel)
      .any((l) => l.confidence < 0.5);

  factory PlacementResult.fromJson(Map<String, dynamic> json) => PlacementResult(
        levels: (json['levels'] as List<dynamic>? ?? const [])
            .map((e) => SkillLevel.fromJson(e as Map<String, dynamic>))
            .toList(),
        spelling: SpellingDiagnostic.fromJson(
            json['spelling'] as Map<String, dynamic>? ?? const {}),
        summary: json['summary'] as String? ?? '',
      );

  Map<String, dynamic> toJson() => {
        'levels': levels.map((e) => e.toJson()).toList(),
        'spelling': spelling.toJson(),
        'summary': summary,
      };
}
