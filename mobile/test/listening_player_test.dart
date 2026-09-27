import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/audio/speech_provider.dart';
import 'package:wordos/core/audio/speech_service.dart';

import 'support/test_harness.dart';

/// The Listening clip's controls (Part 2 §22–§23, ADR-068, ADR-080).
///
/// Everything here is one requirement: **the control tells the truth about
/// what the next tap will do.**
///
/// That reading replaced two earlier ones. The clip used to start by itself,
/// so the learner's first act was to silence it; and pausing left a replay
/// face on the button, which promised to start over when in fact the next tap
/// continued. Both are pinned below as the behaviour they became.
void main() {
  late _FakeTts tts;

  Future<void> openListening(WidgetTester tester) async {
    tts = _FakeTts();
    tester.view.physicalSize = const Size(1200, 2600);
    tester.view.devicePixelRatio = 3;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          ...testOverrides(),
          speechServiceProvider
              .overrideWith((ref) => SpeechService(provider: tts)),
        ],
        child: const WordOsApp(),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Sign in'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Listening'));
    await tester.pumpAndSettle();
  }

  /// Starts the clip the way a learner does.
  Future<void> play(WidgetTester tester) async {
    await tester.tap(find.byIcon(Icons.play_arrow_rounded));
    await tester.pumpAndSettle();
  }

  testWidgets('the clip waits to be asked, and says so', (tester) async {
    await openListening(tester);

    // Nothing plays on arrival. The screen has just printed the clip's title,
    // and a voice talking over it is the app taking the learner's first
    // interaction for something they never asked for (ADR-080).
    expect(tts.spoken, isEmpty,
        reason: 'the learner starts the clip, not the screen');
    expect(find.byIcon(Icons.play_arrow_rounded), findsOneWidget,
        reason: 'and the control offers exactly that');
    expect(find.text('Play audio'), findsOneWidget);

    await play(tester);

    expect(tts.spoken, hasLength(1));
    expect(find.byIcon(Icons.pause_rounded), findsOneWidget,
        reason: 'while it is playing, the control pauses it (§23)');
    // The words under the control say the *state*. The icon is the verb, and
    // a learner who had just pressed pause used to read "Continue" and take
    // the clip to be running (ADR-082).
    expect(find.text('Playing'), findsOneWidget);
  });

  testWidgets('pausing offers to continue, not to start over', (tester) async {
    await openListening(tester);
    await play(tester);

    // Move off the first line so "continued" and "started over" differ.
    tts.finish();
    await tester.pumpAndSettle();
    final interrupted = tts.spoken.last;
    expect(interrupted, isNot(tts.spoken.first));

    await tester.tap(find.byIcon(Icons.pause_rounded));
    await tester.pumpAndSettle();

    expect(tts.stops, greaterThanOrEqualTo(1));
    // The heart of it. A replay face here told the learner who paused to think
    // about a line that the only way back was from the top — so the paused
    // control wears the ordinary play triangle, because the next tap continues.
    expect(find.byIcon(Icons.play_arrow_rounded), findsOneWidget);
    expect(find.byIcon(Icons.replay_rounded), findsNothing,
        reason: 'pausing is not the end of the clip');
    expect(find.byIcon(Icons.pause_rounded), findsNothing);
    expect(find.text('Paused'), findsOneWidget,
        reason: 'the line under the control reports the state, not the action');

    await play(tester);

    // Continued from the line it was on. A text-to-speech voice has no
    // playhead inside a sentence, so that line starts again from its
    // beginning — but the clip does not go back to its own (ADR-068).
    expect(tts.spoken.last, interrupted,
        reason: 'it must pick up where it stopped, not at the start');
  });

  testWidgets('a clip that ends on its own returns the control to replay',
      (tester) async {
    await openListening(tester);
    await play(tester);
    expect(find.byIcon(Icons.pause_rounded), findsOneWidget);

    // Nobody tapped anything — the engine simply reached the end. The clip is
    // spoken one sentence at a time now, so "the end" is the last of them
    // (ADR-068): finishing one utterance hands the voice the next.
    for (var guard = 0; guard < 60; guard++) {
      if (find.byIcon(Icons.pause_rounded).evaluate().isEmpty) break;
      tts.finish();
      await tester.pumpAndSettle();
    }

    expect(find.byIcon(Icons.pause_rounded), findsNothing,
        reason: 'a pause button with nothing to pause is a lie about the audio');
    // Here — and only here — replay is the honest promise: there is nothing
    // left to continue.
    expect(find.byIcon(Icons.replay_rounded), findsOneWidget);
    expect(find.text('Finished'), findsOneWidget);

    final ended = tts.spoken.length;
    await tester.tap(find.byIcon(Icons.replay_rounded));
    await tester.pumpAndSettle();

    expect(tts.spoken.length, ended + 1);
    expect(tts.spoken.last, tts.spoken.first,
        reason: 'replay means the first line again');
  });

  testWidgets('the slow speed replays the clip at the slower rate',
      (tester) async {
    await openListening(tester);
    await play(tester);

    await tester.tap(find.text('Slow'));
    await tester.pumpAndSettle();

    // Slow is an accessibility aid, so it takes effect on the spot rather than
    // waiting for the learner to press play again.
    expect(tts.lastRate, SpeechRate.slow);
    expect(tts.spoken.length, greaterThan(1));
  });

  // ── Moving around inside the clip (ADR-068) ────────────────────────────
  //
  // Text-to-speech has no playhead, so the clip is spoken sentence by sentence
  // and those boundaries are the seek points. What a learner asked for is the
  // ability to go back over a line they missed, jump about, and switch to the
  // slow voice without being sent back to the beginning.

  testWidgets('the clip can be dragged to a later point and plays from there',
      (tester) async {
    await openListening(tester);
    await play(tester);

    final firstSentence = tts.spoken.single;

    // Drag the scrubber to the far end.
    await tester.drag(find.byType(Slider), const Offset(500, 0));
    await tester.pumpAndSettle();

    expect(tts.spoken.length, greaterThan(1),
        reason: 'seeking should start speaking from where it landed');
    expect(tts.spoken.last, isNot(firstSentence),
        reason: 'it should not be the same line over again');
  });

  testWidgets('the clip can be sent back to the start', (tester) async {
    await openListening(tester);
    await play(tester);

    await tester.drag(find.byType(Slider), const Offset(500, 0));
    await tester.pumpAndSettle();
    final afterSeek = tts.spoken.last;

    await tester.tap(find.byIcon(Icons.first_page_rounded));
    await tester.pumpAndSettle();

    expect(tts.spoken.last, isNot(afterSeek));
    expect(tts.spoken.last, tts.spoken.first,
        reason: 'back to the start means the first line again');
  });

  testWidgets('switching to the slow voice keeps the learner\'s place',
      (tester) async {
    await openListening(tester);
    await play(tester);

    // Move off the first line, so "kept its place" means something.
    tts.finish();
    await tester.pumpAndSettle();
    final current = tts.spoken.last;
    expect(current, isNot(tts.spoken.first));

    await tester.tap(find.text('Slow'));
    await tester.pumpAndSettle();

    expect(tts.lastRate, SpeechRate.slow);
    expect(tts.spoken.last, current,
        reason: 'a learner switches to the slow voice because of the line they '
            'are on — sending them back to the beginning answers the wrong '
            'request');
  });

  // ── The playhead, and the clock over it (ADR-082) ───────────────────────
  //
  // The bar used to move a sentence at a time and the position was printed as
  // "Sentence 2 of 11", which is not how anyone reads a recording. It is now a
  // clock, and the place it marks is the word.

  testWidgets('the clip carries a clock rather than a sentence count',
      (tester) async {
    await openListening(tester);

    expect(find.textContaining('Sentence 1 of'), findsNothing,
        reason: 'a listener counts seconds, not sentences');
    // Nothing has played, so the left-hand figure is the start of the clip.
    expect(find.text('0:00'), findsOneWidget);

    await play(tester);

    // The right-hand figure is how long the whole clip runs — estimated, since
    // text-to-speech has no duration to report, but it must be a real time and
    // not zero.
    final total = tester
        .widgetList<Text>(find.byType(Text))
        .map((t) => t.data)
        .whereType<String>()
        .where((d) => RegExp(r'^\d+:\d\d$').hasMatch(d) && d != '0:00')
        .toList();
    expect(total, isNotEmpty,
        reason: 'the learner should be told how long the clip is');
  });

  testWidgets('continuing resumes at the word, not the start of the sentence',
      (tester) async {
    await openListening(tester);
    await play(tester);

    final sentence = tts.spoken.single;
    // The engine reports where it has got to; here, a few words in.
    final partWay = sentence.indexOf(' ', sentence.indexOf(' ') + 1) + 1;
    expect(partWay, greaterThan(1),
        reason: 'the first sentence should have several words');
    tts.speakWord(partWay);
    await tester.pumpAndSettle();

    await tester.tap(find.byIcon(Icons.pause_rounded));
    await tester.pumpAndSettle();
    await play(tester);

    // The tail of that sentence, not the whole of it again. Chopping the clip
    // into words would give the same precision and ruin the audio, so the
    // sentence is kept whole and only *entered* part-way (ADR-082).
    expect(tts.spoken.last, sentence.substring(partWay),
        reason: 'it must pick up at the word it had reached');
    expect(tts.spoken.last, isNot(sentence));
  });

  testWidgets('a resumed word is never cut in half', (tester) async {
    await openListening(tester);
    await play(tester);

    final sentence = tts.spoken.single;
    // Mid-word on purpose: the engine reports word starts, but a dragged
    // scrubber lands anywhere.
    final midWord = sentence.indexOf(' ') + 3;
    tts.speakWord(midWord);
    await tester.pumpAndSettle();

    await tester.tap(find.byIcon(Icons.pause_rounded));
    await tester.pumpAndSettle();
    await play(tester);

    expect(sentence.endsWith(tts.spoken.last), isTrue);
    expect(tts.spoken.last.startsWith(' '), isFalse,
        reason: 'resuming inside a word would have the voice say half of it');
    // It began at a word boundary: the character before it is a space.
    final resumedAt = sentence.length - tts.spoken.last.length;
    expect(sentence[resumedAt - 1], ' ');
  });
}

class _FakeTts implements SpeechProvider {
  final List<String> spoken = [];
  int stops = 0;
  SpeechRate? lastRate;
  VoidCallback? _onComplete;

  @override
  bool get isAvailable => true;

  @override
  String? get voiceDescription => 'fake';

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
    spoken.add(text);
    lastRate = rate;
    return true;
  }

  @override
  Future<void> stop() async => stops++;

  void finish() => _onComplete?.call();

  @override
  Future<void> dispose() async {}
}
