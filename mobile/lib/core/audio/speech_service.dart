import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'speech_provider.dart';

/// The one place spoken English comes from.
///
/// Every speaker button, every listening player, every tutor turn goes through
/// this service — so play/stop behaves identically everywhere and there is a
/// single thing to improve when the voice provider changes.
///
/// It owns two facts the UI needs and cannot derive for itself:
///
/// * **what is speaking**, identified by an [utteranceId] the caller chooses,
///   so one speaker button can show "playing" while every other stays idle;
/// * **when it stops**, whether that was the end of the sentence, another
///   utterance taking over, or the learner tapping again.
///
/// Only one utterance ever plays. Starting a new one stops the old one first —
/// two voices talking over each other is never what anyone wanted.
class SpeechService extends ChangeNotifier {
  SpeechService({SpeechProvider? provider})
      : _provider = provider ?? DeviceSpeechProvider() {
    _provider.onComplete = _handleComplete;
  }

  final SpeechProvider _provider;

  String? _utteranceId;
  Completer<void>? _utterance;

  /// What is speaking right now, or null when nothing is.
  String? get utteranceId => _utteranceId;

  bool get isSpeaking => _utteranceId != null;

  /// True when [id] is the utterance currently playing.
  ///
  /// This is what a speaker button binds to, so its icon can never disagree
  /// with what the speakers are actually doing.
  bool isSpeakingId(String id) => _utteranceId == id;

  bool get isAvailable => _provider.isAvailable;

  String? get voiceDescription => _provider.voiceDescription;

  Future<void> initialise() => _provider.initialise();

  /// Speaks [text], or stops it if that same utterance is already playing.
  ///
  /// The behaviour §10 asks for: first tap plays, second tap stops immediately
  /// rather than making the learner wait out the sentence.
  Future<void> toggle(
    String id,
    String text, {
    SpeechRate rate = SpeechRate.normal,
  }) async {
    if (_utteranceId == id) {
      await stop();
      return;
    }
    await speak(id, text, rate: rate);
  }

  /// Starts an utterance, replacing anything already speaking.
  Future<bool> speak(
    String id,
    String text, {
    SpeechRate rate = SpeechRate.normal,
  }) async {
    await stop();

    _utteranceId = id;
    notifyListeners();

    final started = await _provider.speak(text, rate: rate);
    if (!started) {
      // Nothing is playing, so the UI must not claim otherwise.
      _utteranceId = null;
      notifyListeners();
    }
    return started;
  }

  /// Speaks and returns only once the voice has actually stopped.
  ///
  /// Used by the hands-free conversation, where the microphone must open on
  /// completion rather than after a guessed delay — a guess either cuts the
  /// tutor off or records its own voice.
  ///
  /// The wait is bounded: a platform that never reports completion would
  /// otherwise hang the conversation for good.
  Future<bool> speakToCompletion(
    String id,
    String text, {
    SpeechRate rate = SpeechRate.normal,
  }) async {
    final started = await speak(id, text, rate: rate);
    if (!started) return false;

    final completer = _utterance = Completer<void>();
    try {
      await completer.future.timeout(
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

  Future<void> stop() async {
    if (_utteranceId == null) return;

    await _provider.stop();
    _handleComplete();
  }

  void _handleComplete() {
    final pending = _utterance;
    if (pending != null && !pending.isCompleted) pending.complete();

    if (_utteranceId != null) {
      _utteranceId = null;
      notifyListeners();
    }
  }

  @override
  void dispose() {
    _provider.onComplete = null;
    unawaited(_provider.dispose());
    super.dispose();
  }
}

/// App-wide. One voice, one playback state, one thing to replace later.
///
/// No `ref.onDispose` here: `ChangeNotifierProvider` already disposes the
/// notifier it creates, and registering it a second time disposes it twice —
/// which `ChangeNotifier` asserts against.
final speechServiceProvider = ChangeNotifierProvider<SpeechService>(
  (ref) => SpeechService(),
);
