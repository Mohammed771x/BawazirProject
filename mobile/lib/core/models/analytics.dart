import 'enums.dart';

/// One thing the learner did on screen, for the admin area (ADR-125).
///
/// Telemetry, not state: nothing the app shows is ever read back from these,
/// and a batch that never arrives costs a chart one data point. That is why
/// sending them involves no business logic and leaves rule R1 untouched — the
/// phone reports what happened; the server decides what it means.
///
/// Holds no free text the learner typed. [props] is a flat map of flags,
/// counts and short identifiers (a glossary word is a word of the passage,
/// not something the learner wrote).
class ClientEvent {
  ClientEvent({
    required this.name,
    DateTime? at,
    this.appSessionId,
    this.sessionId,
    this.wordId,
    this.skill,
    this.attempt,
    this.result,
    this.durationMs,
    this.level,
    this.screen,
    this.props,
  }) : at = at ?? DateTime.now();

  final String name;
  final DateTime at;
  final String? appSessionId;
  final String? sessionId;
  final String? wordId;
  final SkillType? skill;
  final int? attempt;
  final String? result;
  final int? durationMs;
  final String? level;
  final String? screen;
  final Map<String, Object?>? props;

  Map<String, dynamic> toJson() => {
        'name': name,
        'at': at.toUtc().toIso8601String(),
        if (appSessionId != null) 'appSessionId': appSessionId,
        if (sessionId != null) 'sessionId': sessionId,
        if (wordId != null) 'wordId': wordId,
        if (skill != null) 'skill': skill!.wire,
        if (attempt != null) 'attempt': attempt,
        if (result != null) 'result': result,
        if (durationMs != null) 'durationMs': durationMs,
        if (level != null) 'level': level,
        if (screen != null) 'screen': screen,
        if (props != null && props!.isNotEmpty) 'props': props,
      };
}

/// The names the server accepts from a phone — `AnalyticsEventNames.ClientNames`.
abstract final class ClientEvents {
  static const appOpened = 'app_opened';
  static const appBackgrounded = 'app_backgrounded';
  static const screenViewed = 'screen_viewed';
  static const screenLeft = 'screen_left';
  static const translationOpened = 'translation_opened';
  static const audioPlayed = 'audio_played';
  static const audioReplayed = 'audio_replayed';
  static const audioPaused = 'audio_paused';
  static const audioCompleted = 'audio_completed';
  static const hintUsed = 'hint_used';
  static const feedbackViewed = 'feedback_viewed';
  static const exerciseExited = 'exercise_exited';
  static const notificationOpened = 'notification_opened';
  static const apiError = 'api_error';
  static const emptyStateShown = 'empty_state_shown';
}
