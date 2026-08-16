import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_tts/flutter_tts.dart';

/// Text-to-speech for Listening sessions and audio placement items.
///
/// The documents deliberately keep audio generation on the device for the MVP:
/// the AI returns text, the app speaks it (System Architecture §14).
///
/// **Every platform call here can fail** — no engine installed, the English
/// voice missing, the channel unavailable on a desktop build, audio focus lost
/// to a phone call. None of that may crash a session, so each call is guarded
/// and failure is reported as state rather than thrown. The caller decides what
/// to do about it; Listening falls back to showing the transcript so the
/// learner is never trapped in a session they cannot finish.
class TtsService {
  TtsService({FlutterTts? tts}) : _tts = tts ?? FlutterTts() {
    unawaited(_configure());
  }

  static const double normalRate = 0.48;
  static const double slowRate = 0.30;

  final FlutterTts _tts;

  bool _speaking = false;
  bool _available = true;
  void Function()? _onStateChanged;

  /// Completes when the current utterance stops, however it stops.
  Completer<void>? _utterance;

  bool get isSpeaking => _speaking;

  /// False once a platform call has failed. The UI uses this to offer the
  /// transcript instead of leaving a dead play button.
  bool get isAvailable => _available;

  set onStateChanged(void Function()? callback) => _onStateChanged = callback;

  Future<void> _configure() async {
    // No timeout: configuration is fire-and-forget, and on a platform with no
    // speech engine these calls simply never complete. A pending timer here
    // would outlive every widget tree that ever built a listening screen.
    await _guard(timeout: null, () async {
      await _tts.setLanguage('en-US');
      await _tts.setSpeechRate(normalRate);
      await _tts.setVolume(1.0);
      await _tts.setPitch(1.0);

      _tts.setCompletionHandler(_finished);
      _tts.setCancelHandler(_finished);
      _tts.setErrorHandler((dynamic _) {
        // A synthesis error is a real failure, not just an early stop.
        _available = false;
        _finished();
      });
    });
  }

  void _finished() {
    _speaking = false;
    final pending = _utterance;
    if (pending != null && !pending.isCompleted) pending.complete();
    _onStateChanged?.call();
  }

  /// Speaks [text] and returns only once the voice has actually stopped.
  ///
  /// This is what makes a hands-free conversation possible: the microphone
  /// opens on completion, not on a guessed delay. Guessing would either cut the
  /// tutor off mid-sentence or leave the learner staring at a silent screen —
  /// and worse, an open microphone during playback records the tutor's own
  /// voice.
  ///
  /// A missing completion callback would hang the conversation, so the wait is
  /// bounded: the timeout is generous but finite, and returning early is far
  /// better than a session that can never continue.
  Future<bool> speakToCompletion(String text) async {
    final started = await speak(text);
    if (!started) return false;

    _utterance = Completer<void>();
    try {
      await _utterance!.future.timeout(
        // Roughly reading speed, with a floor for short replies.
        Duration(seconds: 8 + (text.length ~/ 10)),
      );
      return true;
    } on TimeoutException {
      await stop();
      return true;
    } finally {
      _utterance = null;
    }
  }

  /// Speaks [text]. Returns false when audio could not be produced.
  Future<bool> speak(String text, {bool slow = false}) async {
    if (text.trim().isEmpty) return false;

    await stop();
    final ok = await _guard(() async {
      await _tts.setSpeechRate(slow ? slowRate : normalRate);
      _speaking = true;
      _onStateChanged?.call();
      await _tts.speak(text);
    });

    if (!ok) {
      _speaking = false;
      _available = false;
      _onStateChanged?.call();
    }
    return ok;
  }

  Future<void> stop() async {
    if (!_speaking) return;
    await _guard(() => _tts.stop());
    _speaking = false;
  }

  void dispose() {
    _onStateChanged = null;
    // No timeout here: teardown must not leave a pending timer behind, and
    // there is nobody left to care whether the stop succeeded.
    unawaited(_guard(() => _tts.stop(), timeout: null));
  }

  /// Runs a platform call, swallowing the failure and reporting it as `false`.
  ///
  /// Deliberately catches everything: `flutter_tts` surfaces
  /// `MissingPluginException`, `PlatformException` and plain errors depending
  /// on the platform, and an unhandled one here would take down a session.
  Future<bool> _guard(
    Future<void> Function() call, {
    Duration? timeout = const Duration(seconds: 5),
  }) async {
    try {
      final future = call();
      await (timeout == null ? future : future.timeout(timeout));
      return true;
    } catch (_) {
      return false;
    }
  }
}

final ttsServiceProvider = Provider<TtsService>((ref) {
  final service = TtsService();
  ref.onDispose(service.dispose);
  return service;
});
