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

    // Nobody tapped anything — the engine simply reached the end.
    tts.finish();
    await tester.pumpAndSettle();

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
