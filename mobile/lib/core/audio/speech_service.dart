import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:speech_to_text/speech_to_text.dart';

/// Speech recognition for the Speaking session.
///
/// The learner talks; this turns it into the text the backend already knows how
/// to handle. Nothing here judges anything — the transcript is sent to the
/// server exactly as recognised, and the evaluation happens there
/// (rule R1, rule R2).
///
/// **Pronunciation is deliberately not assessed anywhere in WordOS.** What comes
/// back from a recogniser is a best guess, so a "mispronunciation" is
/// indistinguishable from a recognition error — scoring it would punish the
/// learner for their microphone and their accent.
///
/// Like [TtsService], every platform call is guarded. A device with no
/// microphone permission, no recogniser, or (commonly) the iOS Simulator must
/// degrade to typing rather than trap the learner in a session they cannot
/// finish.
class SpeechService {
  SpeechService({SpeechToText? speech}) : _speech = speech ?? SpeechToText();

  final SpeechToText _speech;

  bool _initialised = false;
  bool _available = false;
  bool _listening = false;

  bool get isListening => _listening;

  /// False when this device cannot listen at all — no permission, no
  /// recogniser, or a simulator. The UI offers typing instead.
  bool get isAvailable => _available;

  /// Asks for permission and checks a recogniser exists.
  ///
  /// Called before the first listen rather than at construction, so the
  /// permission prompt appears when the learner opens a Speaking session and
  /// can see why it is being asked.
  Future<bool> initialise() async {
    if (_initialised) return _available;
    _initialised = true;

    try {
      _available = await _speech
          .initialize(
            onError: (_) => _listening = false,
            onStatus: (status) {
              if (status == 'done' || status == 'notListening') {
                _listening = false;
              }
            },
          )
          .timeout(const Duration(seconds: 10), onTimeout: () => false);
    } catch (_) {
      // No plugin, no permission, no recogniser — all the same to the caller.
      _available = false;
    }
    return _available;
  }

  /// Listens until the learner stops talking, and returns what they said.
  ///
  /// The stop is automatic: [pauseFor] silence ends the turn, so the learner
  /// never presses anything. [listenFor] is the hard ceiling that stops an open
  /// microphone running forever if the silence detector never fires.
  ///
  /// Returns null when nothing usable was heard, which the caller treats as
  /// "ask again" rather than as an answer.
  Future<String?> listenOnce({
    Duration pauseFor = const Duration(seconds: 3),
    Duration listenFor = const Duration(seconds: 45),
  }) async {
    if (!await initialise()) return null;

    final completer = Completer<String?>();
    var best = '';

    try {
      _listening = true;
      await _speech.listen(
        onResult: (result) {
          if (result.recognizedWords.isNotEmpty) best = result.recognizedWords;
          if (result.finalResult && !completer.isCompleted) {
            completer.complete(best.trim().isEmpty ? null : best.trim());
          }
        },
        listenOptions: SpeechListenOptions(
          pauseFor: pauseFor,
          listenFor: listenFor,
          localeId: 'en_US',
          // Partial results are what make the learner's words appear as they
          // speak; without them the screen looks frozen for the whole turn.
          partialResults: true,
          cancelOnError: true,
          // Dictation keeps the microphone open through natural pauses instead
          // of ending the turn at the first comma.
          listenMode: ListenMode.dictation,
        ),
      );

      // The hard ceiling. `listenFor` should end it first; this is the backstop
      // for a recogniser that stops reporting.
      final heard = await completer.future.timeout(
        listenFor + const Duration(seconds: 5),
        onTimeout: () => best.trim().isEmpty ? null : best.trim(),
      );

      return heard;
    } catch (_) {
      return null;
    } finally {
      _listening = false;
      await stop();
    }
  }

  Future<void> stop() async {
    try {
      await _speech.stop();
    } catch (_) {
      // Nothing useful to do; the turn is over either way.
    }
  }

  Future<void> cancel() async {
    try {
      await _speech.cancel();
    } catch (_) {
      // Ignore.
    }
    _listening = false;
  }
}

final speechServiceProvider = Provider<SpeechService>((ref) {
  final service = SpeechService();
  ref.onDispose(service.cancel);
  return service;
});
