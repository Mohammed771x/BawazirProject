import 'dart:async';
import 'dart:math';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../api/api_providers.dart';
import '../models/models.dart';

/// Collects on-screen events and sends them in batches (ADR-125).
///
/// Three properties matter more than any event it records:
///
/// * **It never costs the learner anything.** Nothing awaits it, every error
///   is swallowed, and a full queue drops its oldest events rather than grow.
/// * **No timers.** A periodic flush would leave a pending timer in every
///   widget test; batches go when they are full, when the app is backgrounded,
///   and when a lesson screen closes — the moments a learner stops.
/// * **No decisions.** It reports what happened; the server decides what it
///   means (rule R1).
class AnalyticsTracker {
  AnalyticsTracker(this._send);

  final Future<void> Function(List<ClientEvent>) _send;
  final List<ClientEvent> _queue = [];

  static const int _batch = 20;
  static const int _cap = 200;

  String _appSession = _newId();
  DateTime? _foregroundSince;

  /// The phone's id for this foreground stretch of the app.
  String get appSessionId => _appSession;

  void track(
    String name, {
    String? sessionId,
    String? wordId,
    SkillType? skill,
    int? attempt,
    String? result,
    int? durationMs,
    String? level,
    String? screen,
    Map<String, Object?>? props,
  }) {
    _queue.add(ClientEvent(
      name: name,
      appSessionId: _appSession,
      sessionId: sessionId,
      wordId: wordId,
      skill: skill,
      attempt: attempt,
      result: result,
      durationMs: durationMs,
      level: level,
      screen: screen,
      props: props,
    ));
    if (_queue.length > _cap) _queue.removeRange(0, _queue.length - _cap);
    if (_queue.length >= _batch) unawaited(flush());
  }

  /// Sends what is queued. Safe to call any time; never throws.
  Future<void> flush() async {
    if (_queue.isEmpty) return;
    final batch = List<ClientEvent>.of(_queue.take(50));
    _queue.removeRange(0, batch.length);
    try {
      await _send(batch);
    } catch (_) {
      // Dropped on purpose: a chart missing a point is better than a retry
      // loop on a phone with no signal.
    }
  }

  /// The app came to the foreground — the start of App Time.
  void appOpened({bool fromNotification = false}) {
    _appSession = _newId();
    _foregroundSince = DateTime.now();
    track(ClientEvents.appOpened, props: {'fromNotification': fromNotification});
  }

  /// The app went to the background — App Time ends here.
  void appBackgrounded() {
    final since = _foregroundSince;
    _foregroundSince = null;
    track(
      ClientEvents.appBackgrounded,
      durationMs: since == null ? null : DateTime.now().difference(since).inMilliseconds,
    );
    unawaited(flush());
  }

  static String _newId() {
    final r = Random();
    return List.generate(16, (_) => r.nextInt(16).toRadixString(16)).join();
  }
}

/// One tracker per app, sending through whichever API is in use.
final analyticsTrackerProvider = Provider<AnalyticsTracker>(
  (ref) {
    final api = ref.watch(wordOsApiProvider);
    return AnalyticsTracker((events) => api.trackEvents(events));
  },
);
