import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/core/storage/app_preferences.dart';
import 'package:wordos/core/storage/preferences_providers.dart';
import 'package:wordos/core/widgets/app_widgets.dart';
import 'package:wordos/features/session/session_widgets.dart';

import 'support/test_harness.dart';

/// The Reading section, as reviewed on the device.
///
/// Four separate complaints, all about the same screen: the header was too
/// small to read at a glance, the English passage was laid out right-to-left
/// inside the Arabic app, tapping a word offered every meaning it has ever had
/// instead of the one in front of the learner, and a passage that turned out
/// too hard could only be abandoned.
void main() {
  Future<void> openReading(
    WidgetTester tester, {
    Locale locale = const Locale('en'),
  }) async {
    tester.view.physicalSize = const Size(1200, 6000);
    tester.view.devicePixelRatio = 3;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(ProviderScope(
      overrides: [
        appPreferencesProvider
            .overrideWithValue(InMemoryAppPreferences(locale: locale)),
        tokenStoreProvider.overrideWith((ref) => FakeTokenStore()),
      ],
      child: const WordOsApp(),
    ));
    await tester.pumpAndSettle();
    await tester.tap(find.byType(FilledButton).first);
    await tester.pumpAndSettle();
    await tester.tap(find.text(locale.languageCode == 'ar' ? 'القراءة' : 'Reading'));
    await tester.pumpAndSettle();
  }

  testWidgets('the English passage reads left-to-right in the Arabic app',
      (tester) async {
    await openReading(tester, locale: const Locale('ar'));

    // The interface is Arabic; the learning content is English. Without this
    // the passage inherits RTL and every line is right-aligned and starts from
    // the wrong end — still legible, and still wrong.
    final passage = find.byType(HighlightedPassage);
    final text = tester.widget<Text>(
        find.descendant(of: passage, matching: find.byType(Text)));

    expect(
      Directionality.of(tester.element(
          find.descendant(of: passage, matching: find.byType(Text)))),
      TextDirection.ltr,
      reason: 'English content must not follow the interface direction',
    );
    expect(text.textAlign, TextAlign.left);
  });

  testWidgets('the header and the level are readable at a glance',
      (tester) async {
    await openReading(tester);

    final title = tester.widget<Text>(
        find.descendant(of: find.byType(AppBar), matching: find.text('Reading')));

    // Was `titleLarge`; a learner glances at this to know where they are.
    expect(title.style?.fontSize, greaterThanOrEqualTo(22));

    final badge = tester.widget<LevelBadge>(find.byType(LevelBadge).first);
    expect(badge.size, greaterThanOrEqualTo(14),
        reason: 'the level was set in the smallest label style in the system');
  });

  testWidgets('tapping a word gives the meaning it has here, and its type',
      (tester) async {
    await openReading(tester);

    // A word the generator glossed. The sheet must answer with that one
    // meaning — not with every sense the dictionary holds.
    await tester.tapOnText(find.textRange.ofSubstring('student').first);
    await tester.pumpAndSettle();

    expect(find.text('Meaning here'), findsOneWidget);
    expect(find.text('طالب'), findsOneWidget);

    // And what kind of word it is: "will" as an auxiliary is a different thing
    // to learn than "will" as a noun.
    expect(find.text('noun'), findsWidgets);
    expect(find.text('Add to my words'), findsWidgets);
  });

  testWidgets('the level can be changed before the questions, not after',
      (tester) async {
    await openReading(tester);

    // Offered while the passage is still unanswered.
    expect(find.byIcon(Icons.expand_more_rounded), findsOneWidget,
        reason: 'the level badge should offer to change the level');

    await tester.tap(find.byType(LevelBadge).first);
    await tester.pumpAndSettle();

    expect(find.text('Change the level'), findsOneWidget);
    expect(find.textContaining('The same passage'), findsOneWidget,
        reason: 'a learner must know the story survives the change');

    await tester.tap(find.text('A2').last);
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();

    expect(find.byType(LevelBadge), findsWidgets);
    expect(tester.takeException(), isNull);
  });

  testWidgets('a missed comprehension question is not asked again',
      (tester) async {
    await openReading(tester);
    await tester.tap(find.widgetWithText(FilledButton, 'I finished reading'));
    await tester.pumpAndSettle();

    // Deliberately wrong on the first question.
    final firstPrompt = tester
        .widgetList<Text>(find.byType(Text))
        .map((t) => t.data)
        .firstWhere((t) => t != null && t.endsWith('?'));

    await tester.tap(find.byType(OptionTile).first);
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Next'));
    await tester.pumpAndSettle();

    // Answer the rest however they come, and watch for the missed question
    // returning. It measured whether the passage was pitched right, and it
    // has — asking it again teaches nothing.
    var reappeared = false;
    for (var guard = 0; guard < 30; guard++) {
      if (find.text('Session complete').evaluate().isNotEmpty) break;

      final prompt = tester
          .widgetList<Text>(find.byType(Text))
          .map((t) => t.data)
          .firstWhere((t) => t != null && t.endsWith('?'), orElse: () => null);
      if (prompt == firstPrompt) reappeared = true;

      if (find.byType(OptionTile).evaluate().isEmpty) break;
      await tester.tap(find.byType(OptionTile).first);
      await tester.pumpAndSettle();

      final next = find.widgetWithText(FilledButton, 'Next');
      final finish = find.widgetWithText(FilledButton, 'Finish');
      await tester.tap(next.evaluate().isNotEmpty ? next : finish);
      await tester.pumpAndSettle();
    }

    expect(reappeared, isFalse,
        reason: 'a comprehension question is asked once — only words repeat');
  });

  testWidgets('the level control disappears once the questions begin',
      (tester) async {
    await openReading(tester);

    await tester.tap(find.widgetWithText(FilledButton, 'I finished reading'));
    await tester.pumpAndSettle();

    // Re-telling now would replace the items the learner's answers belong to.
    expect(find.byIcon(Icons.expand_more_rounded), findsNothing,
        reason: 'the level must lock once the questions start');
  });
}
