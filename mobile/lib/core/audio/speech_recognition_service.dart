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

  /// Whether the learner still has the microphone open.
  ///
  /// Distinct from [_listening], which says whether the *platform* is currently
  /// in a recognition session. The two come apart constantly: every recogniser
  /// closes its session on a long pause or after a minute or two of speech, and
  /// the turn is not over just because it did.
  bool _wantsToListen = false;

  /// Segments the recogniser has finished with, joined.
  ///
  /// The platform hands back a *final* result and starts over with an empty
  /// string. Keeping only the latest is what made a five-second pause — or a
  /// fourth sentence — wipe everything said before it: the words did not
  /// disappear, they were overwritten.
  String _committed = '';

  /// The segment being spoken right now, replaced as it is refined.
  String _partial = '';

  void Function(String heard)? _onPartial;

  /// Restarts since the last word was heard, so a recogniser that has stopped
  /// working cannot be reopened for ever.
  int _emptyRestarts = 0;

  static const _maxEmptyRestarts = 40;

  /// Everything heard this turn: the finished segments and the one in progress.
  String get heard => '$_committed $_partial'.trim();

  bool get isListening => _wantsToListen;

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
            onError: (_) {
              _listening = false;
              // An error ends the platform's session, not necessarily the
              // learner's turn — a transient one is recovered by reopening.
              unawaited(_resume());
            },
            onStatus: (status) {
              if (status == 'done' || status == 'notListening') {
                _listening = false;
                unawaited(_resume());
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
    if (_wantsToListen) return true;

    _committed = '';
    _partial = '';
    _emptyRestarts = 0;
    _onPartial = onPartial;
    _wantsToListen = true;

    final started = await _listen();
    if (!started) _wantsToListen = false;
    return started;
  }

  /// Opens one platform recognition session.
  ///
  /// Called again each time the platform closes one of its own accord, which
  /// it does on a long silence and after a minute or so of continuous speech.
  /// The learner notices nothing: the words already heard are held in
  /// [_committed], so the transcript continues rather than starting again.
  Future<bool> _listen() async {
    if (_listening) return true;

    try {
      _listening = true;
      await _speech.listen(
        onResult: (result) {
          final words = result.recognizedWords.trim();

          if (result.finalResult) {
            // The segment is closed. Move it into the transcript so the next
            // session's empty first result cannot take it away.
            if (words.isNotEmpty) {
              _committed = _committed.isEmpty ? words : '$_committed $words';
              _emptyRestarts = 0;
            }
            _partial = '';
          } else {
            _partial = words;
            if (words.isNotEmpty) _emptyRestarts = 0;
          }

          _onPartial?.call(heard);
        },
        listenOptions: SpeechListenOptions(
          // Long, but not relied upon: whatever the platform does with these,
          // a closed session is reopened. `pauseFor` being generous simply
          // means fewer restarts, and a restart is not free — a word spoken
          // exactly across one can be lost.
          pauseFor: const Duration(minutes: 5),
          listenFor: const Duration(minutes: 5),
          localeId: 'en_US',
          partialResults: true,
          // False, deliberately: an error that cancels the session would
          // discard the segment in progress. It is stopped and reopened
          // instead, keeping what was already heard.
          cancelOnError: false,
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

  /// Reopens the microphone after the platform closed its session.
  ///
  /// The guard against a recogniser that has died: reopening a session that
  /// hears nothing, for ever, would leave the learner watching a microphone
  /// that is no longer listening to them.
  Future<void> _resume() async {
    if (!_wantsToListen || _listening) return;

    if (_emptyRestarts >= _maxEmptyRestarts) {
      _wantsToListen = false;
      return;
    }
    _emptyRestarts++;

    // A beat, so a platform mid-teardown is not asked to start again while it
    // is still stopping.
    await Future<void>.delayed(const Duration(milliseconds: 150));
    if (!_wantsToListen || _listening) return;

    await _listen();
  }

  /// Closes the microphone and returns everything that was heard.
  ///
  /// Null when nothing usable was said, which the caller treats as "try again"
  /// rather than as an answer.
  Future<String?> stopAndRead() async {
    // Before `stop`, so the status callback it triggers does not reopen the
    // microphone the learner has just closed.
    _wantsToListen = false;

    await stop();
    _listening = false;

    // The recogniser may deliver its last words just after `stop`; a short
    // wait keeps the end of the learner's sentence instead of clipping it.
    await Future<void>.delayed(const Duration(milliseconds: 400));

    final said = heard;
    return said.isEmpty ? null : said;
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
    _wantsToListen = false;
    try {
      await _speech.cancel();
    } catch (_) {
      // Ignore.
    }
    _listening = false;
    _committed = '';
    _partial = '';
  }
}

final speechRecognitionProvider = Provider<SpeechRecognitionService>((ref) {
  final service = SpeechRecognitionService();
  ref.onDispose(service.cancel);
  return service;
});
