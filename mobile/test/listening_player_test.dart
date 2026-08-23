import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/audio/speech_provider.dart';
import 'package:wordos/core/audio/speech_service.dart';

import 'support/test_harness.dart';

/// The Listening clip's controls (Part 2 §22–§23).
///
/// Two requirements, both about the control telling the truth: the clip starts
/// on its own rather than spending the learner's first tap on "play", and once
/// it is playing the same control stops it — immediately, and only while there
/// is actually something to stop.
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

  testWidgets('the clip plays without being asked, then offers to stop',
      (tester) async {
    await openListening(tester);

    expect(tts.spoken, hasLength(1),
        reason: 'a listening exercise should not open silently (§22)');
    expect(find.byIcon(Icons.stop_rounded), findsOneWidget,
        reason: 'while it is playing, the control stops it (§23)');

    await tester.tap(find.byIcon(Icons.stop_rounded));
    await tester.pumpAndSettle();

    expect(tts.stops, 1);
    // Stopped, so the control offers the clip again rather than another stop.
    expect(find.byIcon(Icons.replay_rounded), findsOneWidget);
    expect(find.byIcon(Icons.stop_rounded), findsNothing);
  });

  testWidgets('a clip that ends on its own returns the control to replay',
      (tester) async {
    await openListening(tester);
    expect(find.byIcon(Icons.stop_rounded), findsOneWidget);

    // Nobody tapped anything — the engine simply reached the end. The clip is
    // spoken one sentence at a time now, so "the end" is the last of them
    // (ADR-068): finishing one utterance hands the voice the next.
    for (var guard = 0; guard < 60; guard++) {
      if (find.byIcon(Icons.stop_rounded).evaluate().isEmpty) break;
      tts.finish();
      await tester.pumpAndSettle();
    }

    expect(find.byIcon(Icons.stop_rounded), findsNothing,
        reason: 'a stop button with nothing to stop is a lie about the audio');
    expect(find.byIcon(Icons.replay_rounded), findsOneWidget);
  });

  testWidgets('the slow speed replays the clip at the slower rate',
      (tester) async {
    await openListening(tester);

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

  testWidgets('the position is reported in sentences', (tester) async {
    await openListening(tester);

    expect(find.textContaining('Sentence 1 of'), findsOneWidget);

    tts.finish();
    await tester.pumpAndSettle();

    expect(find.textContaining('Sentence 2 of'), findsOneWidget);
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
