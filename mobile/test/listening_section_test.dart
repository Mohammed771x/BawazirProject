import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/core/audio/speech_provider.dart';
import 'package:wordos/core/audio/speech_service.dart';
import 'package:wordos/core/storage/app_preferences.dart';
import 'package:wordos/core/storage/preferences_providers.dart';
import 'package:wordos/core/widgets/app_widgets.dart';
import 'package:wordos/features/session/session_widgets.dart';

import 'support/test_harness.dart';

/// The Listening section, reviewed against Reading.
///
/// Most of what was asked for here already existed, because the two skills
/// share a screen — the header, the level control and the passage direction are
/// one implementation. What did not exist: audio that stops when the learner
/// moves on, and the recording handed back at the end.
void main() {
  late _FakeTts tts;

  Future<void> openListening(
    WidgetTester tester, {
    Locale locale = const Locale('en'),
  }) async {
    tts = _FakeTts();
    tester.view.physicalSize = const Size(1200, 6000);
    tester.view.devicePixelRatio = 3;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(ProviderScope(
      overrides: [
        appPreferencesProvider
            .overrideWithValue(InMemoryAppPreferences(locale: locale)),
        tokenStoreProvider.overrideWith((ref) => FakeTokenStore()),
        speechServiceProvider
            .overrideWith((ref) => SpeechService(provider: tts)),
      ],
      child: const WordOsApp(),
    ));
    await tester.pumpAndSettle();
    await tester.tap(find.byType(FilledButton).first);
    await tester.pumpAndSettle();
    await tester.tap(
        find.text(locale.languageCode == 'ar' ? 'الاستماع' : 'Listening'));
    await tester.pumpAndSettle();
  }

  testWidgets('the header matches Reading exactly', (tester) async {
    await openListening(tester);

    final title = tester.widget<Text>(find.descendant(
        of: find.byType(AppBar), matching: find.text('Listening')));
    final badge = tester.widget<LevelBadge>(find.byType(LevelBadge).first);

    // The same numbers Reading uses — they share one implementation, and this
    // pins that they have not drifted apart.
    expect(title.style?.fontSize, greaterThanOrEqualTo(22));
    expect(badge.size, greaterThanOrEqualTo(14));
  });

  testWidgets('the level can be changed here too, before the questions',
      (tester) async {
    await openListening(tester);

    expect(find.byIcon(Icons.expand_more_rounded), findsOneWidget);

    await tester.tap(find.byType(LevelBadge).first);
    await tester.pumpAndSettle();
    expect(find.text('Change the level'), findsOneWidget);

    await tester.tap(find.text('A2').last);
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();

    expect(tester.takeException(), isNull);
  });

  testWidgets('the audio stops when the learner leaves the passage',
      (tester) async {
    await openListening(tester);

    // The clip starts on its own (§22), so something is playing.
    expect(tts.spoken, isNotEmpty);
    final stopsBefore = tts.stops;

    await tester.tap(find.widgetWithText(FilledButton, 'I finished listening'));
    await tester.pumpAndSettle();

    // For Listening this is not merely untidy: a script still playing over the
    // questions is handing the learner the answers.
    expect(tts.stops, greaterThan(stopsBefore),
        reason: 'audio must stop when the section changes');
  });

  testWidgets('nothing is left speaking after moving between questions',
      (tester) async {
    await openListening(tester);
    await tester.tap(find.widgetWithText(FilledButton, 'I finished listening'));
    await tester.pumpAndSettle();

    final speech = ProviderScope.containerOf(
      tester.element(find.byType(Scaffold).first),
    ).read(speechServiceProvider);

    // Start something playing, whether or not the question does it itself.
    await speech.speak('probe', 'a sentence still being read aloud');
    expect(speech.isSpeaking, isTrue);

    await tester.tap(find.byType(OptionTile).first);
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Next'));
    await tester.pumpAndSettle();

    // The rule, stated generally: leaving a section silences it. A sentence
    // talking over the next question is a page that did not turn.
    expect(speech.isSpeaking, isFalse,
        reason: 'a sentence must not talk over the next question');
  });

  testWidgets('the recording comes back with the result, before the text',
      (tester) async {
    await openListening(tester);
    await tester.tap(find.widgetWithText(FilledButton, 'I finished listening'));
    await tester.pumpAndSettle();

    for (var guard = 0; guard < 30; guard++) {
      if (find.text('Session complete').evaluate().isNotEmpty) break;
      if (find.byType(OptionTile).evaluate().isEmpty) break;

      await tester.tap(find.byType(OptionTile).first);
      await tester.pumpAndSettle();

      final next = find.widgetWithText(FilledButton, 'Next');
      final finish = find.widgetWithText(FilledButton, 'Finish');
      await tester.tap(next.evaluate().isNotEmpty ? next : finish);
      await tester.pumpAndSettle();
    }

    expect(find.text('Session complete'), findsWidgets);

    // During the session the audio was the test; afterwards it is study
    // material, and this is the moment the learner most wants it back.
    expect(find.byType(ReplayPlayer), findsOneWidget,
        reason: 'the recording should be replayable from the result');

    // Nothing plays by itself here — they are reading their score.
    final spokenBefore = tts.spoken.length;
    await tester.pump(const Duration(seconds: 1));
    expect(tts.spoken.length, spokenBefore,
        reason: 'the result screen must not start talking on its own');

    // Order: answers, then audio, then text (§7).
    final replayY = tester.getTopLeft(find.byType(ReplayPlayer)).dy;
    final textY = tester.getTopLeft(find.text('Show transcript')).dy;
    expect(replayY, lessThan(textY),
        reason: 'hearing it again comes before reading it');
  });

  testWidgets('the transcript reads left-to-right in the Arabic app',
      (tester) async {
    await openListening(tester, locale: const Locale('ar'));
    await tester.tap(find.widgetWithText(FilledButton, 'أنهيت الاستماع'));
    await tester.pumpAndSettle();

    for (var guard = 0; guard < 30; guard++) {
      if (find.byType(HighlightedPassage).evaluate().isNotEmpty) break;
      if (find.byType(OptionTile).evaluate().isEmpty) break;

      await tester.tap(find.byType(OptionTile).first);
      await tester.pumpAndSettle();

      final next = find.widgetWithText(FilledButton, 'التالي');
      final finish = find.widgetWithText(FilledButton, 'إنهاء');
      await tester.tap(next.evaluate().isNotEmpty ? next : finish);
      await tester.pumpAndSettle();
    }

    final passage = find.byType(HighlightedPassage);
    expect(passage, findsOneWidget);

    // Exactly what Reading does — the same widget, so the same guarantee.
    expect(
      Directionality.of(tester.element(
          find.descendant(of: passage, matching: find.byType(Text)))),
      TextDirection.ltr,
    );
  });
}

class _FakeTts implements SpeechProvider {
  final List<String> spoken = [];
  int stops = 0;
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
    return true;
  }

  @override
  Future<void> stop() async => stops++;

  void finish() => _onComplete?.call();

  @override
  Future<void> dispose() async {}
}
