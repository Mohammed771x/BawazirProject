import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../api/api_providers.dart';
import 'cloud_speech_provider.dart';
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
    _provider.onWordBoundary = _handleWordBoundary;
  }

  final SpeechProvider _provider;

  String? _utteranceId;
  Completer<void>? _utterance;
  int _interruptions = 0;
  bool _disposed = false;
  int _spokenOffset = 0;

  /// How far into the current utterance the voice has reached, in characters.
  ///
  /// Zero until the engine reports a word, and zero again for an engine that
  /// never reports one — so a caller reads it as *at least this far*, never as
  /// the whole truth (ADR-082).
  int get spokenOffset => _spokenOffset;

  /// How many word starts the voice has reported, ever.
  ///
  /// A caller timing speech watches this change rather than the clock at
  /// which it asked: a cloud voice is fetched before it plays (ADR-108), and
  /// counting the fetch as speaking made a Listening clip's clock read 0:27
  /// for fifteen seconds of audio.
  int get wordEvents => _wordEvents;
  int _wordEvents = 0;


  /// What is speaking right now, or null when nothing is.
  String? get utteranceId => _utteranceId;

  /// How many times playback has been cut short rather than allowed to end —
  /// an explicit [stop], or a new utterance taking the voice over.
  ///
  /// A caller that speaks a *sequence* cannot do without this. Text-to-speech
  /// reports a cancelled utterance and a finished one through the same
  /// callback, so a loop that waits for one sentence and then starts the next
  /// has no way to tell "that line ended" from "somebody stopped me". The
  /// Listening clip is exactly such a loop, and without this it carried on
  /// reading over the questions it was about to be tested on — which is to say
  /// it read the answers out (ADR-080).
  ///
  /// Read it once the voice is yours, then compare after every sentence: a
  /// change means the clip is no longer yours to continue.
  int get interruptions => _interruptions;

  bool get isSpeaking => _utteranceId != null;

  /// Every call here is asynchronous and every caller is a widget, so a screen
  /// that silences the voice as it goes away lands *after* the scope holding
  /// this service has been torn down. `ChangeNotifier` asserts on a notify
  /// after disposal, which would surface a teardown race as a crash in the
  /// screen that did the right thing. So the service simply stops working
  /// instead: it can no longer speak, and it no longer tells anyone anything.
  void _notify() {
    if (_disposed) return;
    notifyListeners();
  }

  /// One word further in. Notifies rather than calling anyone back: the
  /// service is app-wide and a single callback field would belong to whichever
  /// player registered last — which is not necessarily the one speaking. Every
  /// player is already a listener, and reads this only while
  /// [isSpeakingId] says the voice is its own.
  void _handleWordBoundary(int offset) {
    if (_disposed || _utteranceId == null) return;
    _spokenOffset = offset;
    _wordEvents++;
    _notify();
  }

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
    if (_disposed) return;
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
    if (_disposed) return false;
    await stop();
    if (_disposed) return false;

    _utteranceId = id;
    // A fresh utterance starts at its beginning, whatever the last one
    // reached. Leaving the old offset in place would put a new sentence's
    // playhead wherever the previous sentence happened to stop.
    _spokenOffset = 0;
    _notify();

    // A routed provider chooses the voice by who is speaking (ADR-108).
    final provider = _provider;
    final started = provider is RoutedSpeechProvider
        ? await provider.speakFor(id, text, rate: rate)
        : await provider.speak(text, rate: rate);
    // Stopped, or replaced by another utterance, while the voice was still
    // being fetched: that is not a failure, and not this utterance's to undo.
    if (_utteranceId != id) return started;
    if (!started) {
      // Nothing is playing, so the UI must not claim otherwise.
      _utteranceId = null;
      _notify();
    }
    return started;
  }

  /// [text] will be spoken as [id], a sentence at a time, soon.
  ///
  /// Lets a cloud voice fetch it ahead so no gap opens between sentences
  /// (ADR-108). A no-op for the phone's voice, which needs no warning.
  void prepare(String id, String text) {
    if (_disposed) return;
    final provider = _provider;
    if (provider is RoutedSpeechProvider) provider.prepare(id, text);
  }

  /// Completes once [text] can start speaking at once — or once it is clear
  /// the server's voice will not come in time and the phone will say it
  /// (ADR-116). The tutor's line is shown together with its voice, so the
  /// screen waits on this before drawing it. Immediate for the phone's voice,
  /// which has nothing to fetch.
  Future<void> ready(String id, String text) async {
    if (_disposed) return;
    final provider = _provider;
    if (provider is RoutedSpeechProvider) await provider.ready(id, text);
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
    // Already over — stopped while a cloud voice was being fetched, or
    // finished before this line ran. Waiting now would wait for nothing until
    // the timeout.
    if (_utteranceId != id) return true;

    final provider = _provider;
    final completer = _utterance = Completer<void>();
    try {
      await completer.future.timeout(
        // Roughly reading speed, with a floor for short replies — longer for
        // the slow voice, and longer again for a voice that is fetched before
        // it plays.
        Duration(
              seconds: 8 +
                  (text.length ~/ 10) * (rate == SpeechRate.slow ? 3 : 2) ~/ 2,
            ) +
            (provider is RoutedSpeechProvider
                ? provider.startupAllowance
                : Duration.zero),
      );
      return true;
    } on TimeoutException {
      // A platform that never reported completion — not somebody stopping us.
      // Counting it as an interruption would halt a sequence on the one
      // failure the timeout exists to paper over, so it is not counted.
      await stop(interrupting: false);
      return true;
    } finally {
      _utterance = null;
    }
  }

  /// Silences whatever is speaking.
  ///
  /// [interrupting] is false only where stopping *is* the end of the utterance
  /// rather than something cutting it short — see the completion timeout
  /// above. Everywhere else the default holds, including the [stop] [speak]
  /// performs before taking the voice: replacing an utterance interrupts it.
  Future<void> stop({bool interrupting = true}) async {
    if (_disposed || _utteranceId == null) return;

    if (interrupting) _interruptions++;
    await _provider.stop();
    _handleComplete();
  }

  void _handleComplete() {
    // Completed even when disposed: something is awaiting this, and leaving it
    // hanging is how a torn-down screen keeps a future alive for ever.
    final pending = _utterance;
    if (pending != null && !pending.isCompleted) pending.complete();

    if (_utteranceId != null) {
      _utteranceId = null;
      _notify();
    }
  }

  @override
  void dispose() {
    _disposed = true;
    _utteranceId = null;
    _provider.onWordBoundary = null;
    // Anyone still waiting on the voice is released rather than left hanging.
    final pending = _utterance;
    if (pending != null && !pending.isCompleted) pending.complete();
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
///
/// Against the real backend the tutor and Listening speak in Gemini's voice
/// and everything else in the phone's (ADR-108); against the mock, the
/// phone's alone, as the widget suite expects.
final speechServiceProvider = ChangeNotifierProvider<SpeechService>((ref) {
  final env = ref.watch(appEnvironmentProvider);
  if (env.useMockBackend) return SpeechService();
  return SpeechService(
    provider: HybridSpeechProvider(
      device: DeviceSpeechProvider(),
      // Read when a line is fetched, never watched: rebuilding the voice
      // would cut off whatever it is saying.
      synthesize: (text) => ref.read(wordOsApiProvider).synthesizeSpeech(text),
    ),
  );
});
