import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/models/models.dart';
import 'package:wordos/mock_backend/engine/mock_dictionary.dart';

import 'support/test_harness.dart';

/// Drives the real screens the way a learner does, against the mock backend.
/// This is the closest thing to a device walkthrough that runs in CI.
void main() {
  testWidgets('a learner can add a word and see it enter the pipeline',
      (tester) async {
    await bootAndSignIn(tester);

    await tester.tap(find.widgetWithText(FloatingActionButton, 'Add word'));
    await tester.pumpAndSettle();

    // A word the seeded learner does not already own — the same word with the
    // same meaning cannot be added twice (demo review §19).
    await tester.enterText(find.byType(TextField).first, 'allocate');
    await tester.pump(const Duration(milliseconds: 400)); // debounce
    await tester.pumpAndSettle();

    expect(find.text('Which meaning do you mean?'), findsOneWidget);
    expect(find.text('يُخصّص'), findsOneWidget);

    await tester.tap(find.text('يُخصّص'));
    await tester.pumpAndSettle();

    // The word is added and the server says its first skill is Reading.
    expect(find.textContaining('Added to the learning pipeline'), findsOneWidget);
    expect(find.textContaining('Reading'), findsWidgets);
  });

  testWidgets('a reading session runs from passage to result', (tester) async {
    await bootAndSignIn(tester);

    await tester.tap(find.text('Reading'));
    await tester.pumpAndSettle();

    // Phase 1 — the generated passage with highlighted target words.
    expect(find.text('Read the passage'), findsOneWidget);
    await tester.tap(find.widgetWithText(FilledButton, 'I finished reading'));
    await tester.pumpAndSettle();

    // Phase 2 — comprehension then target-word questions, one at a time.
    expect(find.textContaining('Question 1 of'), findsOneWidget);

    for (var guard = 0; guard < 30; guard++) {
      if (find.text('Session complete').evaluate().isNotEmpty) break;

      // Answer with the first option — correctness does not matter here; what
      // matters is that every item type renders and advances.
      final options = find.byType(InkWell);
      await tester.tap(options.at(0), warnIfMissed: false);
      await tester.pumpAndSettle();

      final next = find.widgetWithText(FilledButton, 'Next');
      final finish = find.widgetWithText(FilledButton, 'Finish');
      if (next.evaluate().isNotEmpty) {
        await tester.tap(next);
      } else if (finish.evaluate().isNotEmpty) {
        await tester.tap(finish);
      } else {
        break;
      }
      await tester.pumpAndSettle();
    }

    // Phase 3 — the server-computed outcome per word.
    expect(find.text('Session complete'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Back to Skills Hub'),
        findsOneWidget);
  });

  testWidgets('the weekly review runs and reports a score', (tester) async {
    await bootAndSignIn(tester);

    await tester.tap(find.text('Weekly Review'));
    await tester.pumpAndSettle();

    expect(find.textContaining('Remaining'), findsOneWidget);

    // Meaning lookup: answering correctly is what empties the queue — a wrong
    // answer is requeued by design, so a random tapper would never finish.
    final meanings = {
      for (final entry in MockDictionary.entries.entries)
        entry.key: entry.value.first.meaning,
    };

    for (var guard = 0; guard < 30; guard++) {
      if (find.text('Weekly score').evaluate().isNotEmpty) break;

      final prompt = tester
          .widgetList<Text>(find.byType(Text))
          .map((t) => t.data)
          .firstWhere((t) => t != null && meanings.containsKey(t),
              orElse: () => null);
      expect(prompt, isNotNull, reason: 'a review word should be on screen');

      await tester.tap(find.text(meanings[prompt]!).last);
      await tester.pumpAndSettle();

      final finish = find.widgetWithText(FilledButton, 'Finish');
      final next = find.widgetWithText(FilledButton, 'Next');
      await tester.tap(finish.evaluate().isNotEmpty ? finish : next);
      await tester.pumpAndSettle();
    }

    expect(find.text('Weekly score'), findsOneWidget);
    expect(find.textContaining('Correct on first attempt'), findsOneWidget);
  });

  testWidgets('settings shows both levels and never conflates them',
      (tester) async {
    await bootAndSignIn(tester);

    await tester.tap(find.byIcon(Icons.settings_outlined));
    await tester.pumpAndSettle();

    // Let the settings/config and interest-catalogue fetches land before
    // asserting — pumpAndSettle alone does not advance a pending timer that has
    // no animation scheduled.
    await tester.pump(const Duration(seconds: 1));
    await tester.pumpAndSettle();

    expect(find.text('Skill levels'), findsOneWidget);
    expect(find.textContaining('System-validated'), findsWidgets);

    await tester.scrollUntilVisible(find.text('Daily word targets'), 250);
    expect(find.text('Daily word targets'), findsOneWidget);

    // Spelling is measured but never levelled, so it shows no CEFR dropdown
    // while the other four skills do (ADR-008).
    expect(find.text('Measured, but not a CEFR level'), findsOneWidget);
    expect(find.byType(DropdownButton<CefrLevel>), findsNWidgets(4));

    // Developer tooling is NOT part of a learner's settings (demo review §13).
    expect(find.text('Skip 2 days'), findsNothing);
    expect(find.text('Developer Dashboard'), findsNothing);
  });
}
