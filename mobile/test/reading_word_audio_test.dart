import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/audio/speech_provider.dart';
import 'package:wordos/core/audio/speech_service.dart';
import 'package:wordos/core/models/models.dart';
import 'package:wordos/core/widgets/app_widgets.dart';
import 'package:wordos/core/widgets/speaker_button.dart';
import 'package:wordos/features/session/session_widgets.dart';

import 'support/test_harness.dart';

/// Reading's word questions offer the word to be heard, normally and slowly
/// (ADR-119). The product owner's request: seeing a word spelled is not
/// knowing how it sounds.
void main() {
  late _FakeTts tts;

  Future<void> openReading(WidgetTester tester) async {
    tts = _FakeTts();
    tester.view.physicalSize = const Size(1200, 6000);
    tester.view.devicePixelRatio = 3;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(ProviderScope(
      overrides: [
        ...testOverrides(),
        speechServiceProvider
            .overrideWith((ref) => SpeechService(provider: tts)),
      ],
      child: const WordOsApp(),
    ));
    await tester.pumpAndSettle();
    await tester.tap(find.byType(FilledButton).first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Reading'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'I finished reading'));
    await tester.pumpAndSettle();
  }

  /// Answers questions until one shows [finder].
  Future<void> walkTo(WidgetTester tester, Finder finder) async {
    for (var guard = 0; guard < 30 && finder.evaluate().isEmpty; guard++) {
      if (find.byType(OptionTile).evaluate().isEmpty) break;
      await tester.tap(find.byType(OptionTile).first);
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Check'));
      await tester.pumpAndSettle();
      final next = find.widgetWithText(FilledButton, 'Next');
      final finish = find.widgetWithText(FilledButton, 'Finish');
      await tester.tap(next.evaluate().isNotEmpty ? next : finish);
      await tester.pumpAndSettle();
    }
  }

  testWidgets('a word question can be heard, at both speeds', (tester) async {
    await openReading(tester);

    // The questions about the passage as a whole carry no word of their own.
    expect(find.byType(OptionTile), findsWidgets);
    expect(find.byType(WordSpeakerButtons), findsNothing);

    await walkTo(tester, find.byType(WordSpeakerButtons));
    final control =
        tester.widget<WordSpeakerButtons>(find.byType(WordSpeakerButtons));
    expect(control.text, isNotEmpty);
    // Beside the question, compact — not Listening's card, which pushed the
    // options below the fold on a phone.
    expect(find.byType(WordPronunciation), findsNothing);
    // And it is the word the question asks about.
    expect(find.textContaining('"${control.text}"'), findsOneWidget);

    final speakers = find.descendant(
        of: find.byType(WordSpeakerButtons), matching: find.byType(IconButton));
    await tester.tap(speakers.first);
    await tester.pumpAndSettle();
    expect(tts.spoken.last, control.text);
    expect(tts.lastRate, SpeechRate.normal);

    await tester.tap(speakers.last);
    await tester.pumpAndSettle();
    expect(tts.spoken.last, control.text);
    expect(tts.lastRate, SpeechRate.slow);
  });

  test('the word is marked where it stands on its own', () {
    // Seen on a phone: "The delivery driver will deliver our food" marked the
    // front of "delivery" and left the word being asked about plain.
    const sentence = 'The delivery driver will deliver our food order.';
    expect(wholeWordIndex(sentence, 'deliver'), sentence.indexOf('deliver our'));
    expect(wholeWordIndex('Deliver it now.', 'deliver'), 0);
    // Only inside a longer word: that is still better than nothing.
    expect(wholeWordIndex('The delivery came.', 'deliver'), 4);
    expect(wholeWordIndex('Nothing here.', 'deliver'), -1);
  });

  testWidgets('the sentences around the word read left to right in Arabic',
      (tester) async {
    // Seen on a phone: inheriting the Arabic direction put every sentence's
    // full stop at its start — ".I walk to the local market".
    await tester.pumpWidget(ProviderScope(
      overrides: testOverrides(locale: const Locale('ar')),
      child: const MaterialApp(
        home: Directionality(
          textDirection: TextDirection.rtl,
          child: Scaffold(
            body: ContextPassage(
              context: WordContext(
                before: 'I walk to the local market.',
                sentence: 'The shopkeeper will deliver the bags.',
                after: 'I watch him load the van.',
              ),
              highlight: 'deliver',
              color: Colors.indigo,
            ),
          ),
        ),
      ),
    ));
    await tester.pumpAndSettle();

    for (final line in ['I walk to the local market.', 'I watch him load the van.']) {
      final element = tester.element(find.text(line));
      expect(Directionality.of(element), TextDirection.ltr, reason: line);
    }
    final sentence = tester.element(find.byType(RichText).first);
    expect(Directionality.of(sentence), TextDirection.ltr);
  });
}

class _FakeTts implements SpeechProvider {
  final List<String> spoken = [];
  SpeechRate? lastRate;

  @override
  bool get isAvailable => true;

  @override
  String? get voiceDescription => 'fake';

  @override
  set onComplete(VoidCallback? callback) {}

  @override
  set onWordBoundary(void Function(int start)? callback) {}

  @override
  Future<void> initialise() async {}

  @override
  Future<bool> speak(String text, {SpeechRate rate = SpeechRate.normal}) async {
    spoken.add(text);
    lastRate = rate;
    return true;
  }

  @override
  Future<void> stop() async {}

  @override
  Future<void> dispose() async {}
}
