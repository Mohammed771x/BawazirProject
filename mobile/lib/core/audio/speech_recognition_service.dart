import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:speech_to_text/speech_to_text.dart';

/// Speech recognition — the learner's side of a spoken conversation.
///
/// The learner talks; this turns it into the text the backend already knows how
/// to handle. Nothing here judges anything: the transcript goes to the server
/// exactly as recognised, and the evaluation happens there (rule R1, rule R2).
///
/// **Pronunciation is deliberately not assessed anywhere in WordOS.** What comes
/// back from a recogniser is a best guess, so a "mispronunciation" is
/// indistinguishable from a recognition error — scoring it would punish the
/// learner for their microphone and their accent (ADR-020).
///
/// Every platform call is guarded. A device with no microphone permission, no
/// recogniser, or (commonly) the iOS Simulator must degrade to typing rather
/// than trap the learner in a session they cannot finish.
class SpeechRecognitionService {
  SpeechRecognitionService({SpeechToText? speech})
      : _speech = speech ?? SpeechToText();

  final SpeechToText _speech;

  bool _initialised = false;
  bool _available = false;
  bool _listening = false;

  /// What has been recognised in the turn currently open.
  String _heard = '';

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

  /// Opens the microphone and leaves it open until [stopAndRead] is called.
  ///
  /// Push-to-talk, deliberately. Ending a turn on silence sounds elegant and is
  /// miserable to use: a learner searching for the next word in a foreign
  /// language pauses constantly, and every pause cut them off mid-sentence.
  /// Nobody can speak "at their own pace" against a three-second timer.
  ///
  /// [onPartial] receives the words as they are recognised, so the learner can
  /// see they are being heard while they talk.
  ///
  /// Returns false when this device cannot listen; the caller offers typing.
  Future<bool> startListening({void Function(String heard)? onPartial}) async {
    if (!await initialise()) return false;
    if (_listening) return true;

    _heard = '';

    try {
      _listening = true;
      await _speech.listen(
        onResult: (result) {
          if (result.recognizedWords.isNotEmpty) {
            _heard = result.recognizedWords;
            onPartial?.call(_heard);
          }
        },
        listenOptions: SpeechListenOptions(
          // Effectively no silence cutoff and no short ceiling: the learner
          // decides when they are finished, not the recogniser. The platform
          // may still end a very long session on its own, which is why the
          // words heard so far are kept rather than discarded.
          pauseFor: const Duration(minutes: 5),
          listenFor: const Duration(minutes: 5),
          localeId: 'en_US',
          partialResults: true,
          cancelOnError: true,
          // Dictation keeps the microphone open through natural pauses instead
          // of ending the turn at the first comma.
          listenMode: ListenMode.dictation,
        ),
      );
      return true;
    } catch (_) {
      _listening = false;
      return false;
    }
  }

  /// Closes the microphone and returns everything that was heard.
  ///
  /// Null when nothing usable was said, which the caller treats as "try again"
  /// rather than as an answer.
  Future<String?> stopAndRead() async {
    await stop();
    _listening = false;

    // The recogniser may deliver its last words just after `stop`; a short
    // wait keeps the end of the learner's sentence instead of clipping it.
    await Future<void>.delayed(const Duration(milliseconds: 400));

    final heard = _heard.trim();
    return heard.isEmpty ? null : heard;
  }

  /// Listens until the learner stops talking, and returns what they said.
  ///
  /// The automatic variant, kept for callers that genuinely want a hands-free
  /// turn. The learner-facing screens use [startListening] / [stopAndRead].
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

final speechRecognitionProvider = Provider<SpeechRecognitionService>((ref) {
  final service = SpeechRecognitionService();
  ref.onDispose(service.cancel);
  return service;
});
