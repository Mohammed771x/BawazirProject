import 'package:flutter/foundation.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/audio/speech_provider.dart';
import 'package:wordos/core/audio/speech_service.dart';

/// The audio state model every screen shares (§10, §11, Part 3 §32).
///
/// The rule that matters is not "a button plays sound" — it is that the UI can
/// never claim something is playing when it is not. Every test here is about
/// that agreement between the service and what a speaker icon would draw.
void main() {
  late _RecordingProvider provider;
  late SpeechService speech;

  setUp(() {
    provider = _RecordingProvider();
    speech = SpeechService(provider: provider);
  });

  tearDown(() => speech.dispose());

  test('the first tap plays and the second stops it', () async {
    await speech.toggle('word:1', 'research');
    expect(speech.isSpeakingId('word:1'), isTrue);

    // Immediately — the learner must not have to wait out the sentence.
    await speech.toggle('word:1', 'research');
    expect(speech.isSpeakingId('word:1'), isFalse);
    expect(provider.stops, 1);
  });

  test('only one utterance plays at a time', () async {
    await speech.speak('word:1', 'research');
    await speech.speak('word:2', 'theory');

    // Two voices talking over each other is never what anyone wanted.
    expect(speech.isSpeakingId('word:1'), isFalse);
    expect(speech.isSpeakingId('word:2'), isTrue);
  });

  test('audio finishing on its own returns the UI to idle', () async {
    await speech.speak('word:1', 'research');
    expect(speech.isSpeaking, isTrue);

    // The engine reports completion; nobody tapped anything.
    provider.finish();

    expect(speech.isSpeaking, isFalse,
        reason: 'the icon must not stay in the playing state');
  });

  test('a failed engine never reports something as playing', () async {
    provider.fails = true;

    final started = await speech.speak('word:1', 'research');

    expect(started, isFalse);
    expect(speech.isSpeaking, isFalse,
        reason: 'a device with no speech engine must not show a stop button');
  });

  test('listeners are notified so every speaker button stays in step',
      () async {
    var notifications = 0;
    speech.addListener(() => notifications++);

    await speech.speak('word:1', 'research');
    provider.finish();

    expect(notifications, greaterThanOrEqualTo(2),
        reason: 'start and stop must both repaint the controls');
  });

  test('speakToCompletion waits for the voice, not for a guess', () async {
    var completed = false;
    final future = speech
        .speakToCompletion('tutor:1', 'Hello there.')
        .then((_) => completed = true);

    // Still talking: opening the microphone here would record the tutor.
    await Future<void>.delayed(Duration.zero);
    expect(completed, isFalse);

    provider.finish();
    await future;

    expect(completed, isTrue);
  });

  test('the slow rate reaches the provider', () async {
    await speech.speak('listen:1', 'research', rate: SpeechRate.slow);
    expect(provider.lastRate, SpeechRate.slow);
  });

  // ── Telling a cancelled utterance from a finished one (ADR-080) ──────────
  //
  // Text-to-speech reports both through the same callback, so a caller that
  // speaks a *sequence* — the Listening clip, sentence by sentence — cannot
  // tell "that line ended, start the next" from "somebody stopped me". It read
  // on over the questions it was about to be tested on. [interruptions] is the
  // one fact that separates them.

  test('a sentence allowed to finish is not counted as an interruption',
      () async {
    final before = speech.interruptions;

    await speech.speak('clip:1', 'The shop opens at nine.');
    provider.finish();
    // The next line in the sequence, started once the previous one ended.
    await speech.speak('clip:1', 'It closes at five.');

    expect(speech.interruptions, before,
        reason: 'a clip playing straight through must look uninterrupted');
  });

  test('being stopped from outside is counted', () async {
    await speech.speak('clip:1', 'The shop opens at nine.');
    final before = speech.interruptions;

    // The learner left the passage: some other part of the app silenced it.
    await speech.stop();

    expect(speech.interruptions, before + 1);
  });

  test('another utterance taking the voice is counted', () async {
    await speech.speak('clip:1', 'The shop opens at nine.');
    final before = speech.interruptions;

    await speech.speak('word:1', 'research');

    expect(speech.interruptions, before + 1,
        reason: 'being replaced is being cut short');
  });

  test('stopping nothing counts nothing', () async {
    final before = speech.interruptions;
    await speech.stop();
    expect(speech.interruptions, before,
        reason: 'a no-op stop must not look like somebody cutting a clip off');
  });

  // ── Teardown (ADR-080) ──────────────────────────────────────────────────
  //
  // The screen that silences the voice on its way out is doing exactly the
  // right thing, and its stop lands after the scope holding this service has
  // gone. That must not read as a fault in the screen.

  test('a stop that lands after disposal is harmless', () async {
    final late = SpeechService(provider: _RecordingProvider());
    await late.speak('clip:1', 'The shop opens at nine.');

    final pending = late.stop();
    late.dispose();

    await expectLater(pending, completes);
    expect(late.isSpeaking, isFalse);
  });

  test('nothing speaks after disposal', () async {
    final late = SpeechService(provider: provider);
    late.dispose();

    expect(await late.speak('clip:1', 'anything at all'), isFalse);
    expect(late.isSpeaking, isFalse);
  });

  test('a caller waiting on the voice is released by disposal, not stranded',
      () async {
    final late = SpeechService(provider: _RecordingProvider());
    final pending = late.speakToCompletion('tutor:1', 'Hello there.');

    late.dispose();

    // Left hanging, this future never completes and whatever awaited it —
    // a conversation waiting to open the microphone — waits for ever.
    await expectLater(pending, completes);
  });
}

/// A provider that records what it was asked to do and reports completion only
/// when the test says so.
class _RecordingProvider implements SpeechProvider {
  final List<String> spoken = [];
  int stops = 0;
  bool fails = false;
  SpeechRate? lastRate;
  VoidCallback? _onComplete;

  @override
  bool get isAvailable => !fails;

  @override
  String? get voiceDescription => 'recording';

  @override
  set onComplete(VoidCallback? callback) => _onComplete = callback;

  /// Word-by-word progress, which a fake voice reports only when a test asks
  /// it to — see [speakWord] where one does.
  @override
  set onWordBoundary(void Function(int start)? callback) =>
      _onWordBoundary = callback;

  void Function(int start)? _onWordBoundary;

  /// Pretends the engine reached a word at [offset] in the current utterance.
  void speakWord(int offset) => _onWordBoundary?.call(offset);

  @override
  Future<void> initialise() async {}

  @override
  Future<bool> speak(String text, {SpeechRate rate = SpeechRate.normal}) async {
    if (fails) return false;
    spoken.add(text);
    lastRate = rate;
    return true;
  }

  @override
  Future<void> stop() async => stops++;

  /// Stands in for the engine's completion callback.
  void finish() => _onComplete?.call();

  @override
  Future<void> dispose() async {}
}
