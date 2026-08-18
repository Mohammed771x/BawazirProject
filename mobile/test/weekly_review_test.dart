import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/widgets/app_widgets.dart';
import 'package:wordos/mock_backend/engine/mock_dictionary.dart';

import 'support/test_harness.dart';

/// The weekly review's interaction contract (Part 2 §9–§12).
///
/// The specification rejects `select → see Correct/Incorrect → press Next` by
/// name: it costs two interactions per word, which turns a quick recall drill
/// into a button-pressing exercise. Selecting an answer *is* the interaction;
/// the review evaluates it and moves on by itself.
///
/// Feedback still appears — the learner has to know whether they were right —
/// it simply is not something they have to dismiss.
void main() {
  final meanings = {
    for (final entry in MockDictionary.entries.entries)
      entry.key: entry.value.first.meaning,
  };

  /// Taps the option matching the word on screen; `correct: false` picks any
  /// other option instead.
  Future<void> answerCurrent(WidgetTester tester, {required bool correct}) async {
    final prompt = tester
        .widgetList<Text>(find.byType(Text))
        .map((t) => t.data)
        .firstWhere((t) => t != null && meanings.containsKey(t),
            orElse: () => null);
    expect(prompt, isNotNull, reason: 'a review word should be on screen');

    final wanted = meanings[prompt]!;
    final options = tester
        .widgetList<Text>(find.descendant(
            of: find.byType(OptionTile), matching: find.byType(Text)))
        .map((t) => t.data)
        .whereType<String>()
        .toList();

    final target = correct
        ? wanted
        : options.firstWhere((o) => o != wanted, orElse: () => wanted);
    await tester.tap(find.text(target).last);
    // Past the feedback pause: the review holds a wrong answer on screen a
    // little longer so the learner can read the right one, then advances.
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();
  }

  testWidgets('answering advances the review without a second tap',
      (tester) async {
    await bootAndSignIn(tester);
    await tester.tap(find.text('Weekly Review'));
    await tester.pumpAndSettle();

    final firstWord = tester
        .widgetList<Text>(find.byType(Text))
        .map((t) => t.data)
        .firstWhere((t) => t != null && meanings.containsKey(t));

    await answerCurrent(tester, correct: true);

    // The proof is the absence of the button the spec forbids: there is nothing
    // left to press, and the review has already moved on.
    expect(find.widgetWithText(FilledButton, 'Next'), findsNothing,
        reason: 'the review must not require a second interaction (§9)');

    final nowShowing = tester
        .widgetList<Text>(find.byType(Text))
        .map((t) => t.data)
        .firstWhere((t) => t != null && meanings.containsKey(t),
            orElse: () => null);
    expect(nowShowing, isNot(equals(firstWord)),
        reason: 'it should be on the next word by itself');
  });

  testWidgets('a wrong answer comes back later, never immediately',
      (tester) async {
    await bootAndSignIn(tester);
    await tester.tap(find.text('Weekly Review'));
    await tester.pumpAndSettle();

    final missed = tester
        .widgetList<Text>(find.byType(Text))
        .map((t) => t.data)
        .firstWhere((t) => t != null && meanings.containsKey(t));

    await answerCurrent(tester, correct: false);

    // §11: recorded as incorrect and requeued to the *end* of the session.
    // Repeating it straight away would let the learner grind the same word
    // until it stuck, which measures persistence rather than recall.
    final after = tester
        .widgetList<Text>(find.byType(Text))
        .map((t) => t.data)
        .firstWhere((t) => t != null && meanings.containsKey(t),
            orElse: () => null);
    expect(after, isNot(equals(missed)),
        reason: 'a missed word must not repeat immediately (§11)');

    // ...and it is still owed. Answering everything correctly from here must
    // eventually bring it back before the review will finish.
    var seenAgain = false;
    for (var guard = 0; guard < 40; guard++) {
      if (find.text('Weekly score').evaluate().isNotEmpty) break;
      final current = tester
          .widgetList<Text>(find.byType(Text))
          .map((t) => t.data)
          .firstWhere((t) => t != null && meanings.containsKey(t),
              orElse: () => null);
      if (current == missed) seenAgain = true;
      await answerCurrent(tester, correct: true);

      final finish = find.widgetWithText(FilledButton, 'Finish');
      if (finish.evaluate().isNotEmpty) {
        await tester.tap(finish);
        await tester.pumpAndSettle();
      }
    }

    expect(seenAgain, isTrue, reason: 'the missed word should return (§11)');
    expect(find.text('Weekly score'), findsOneWidget);
  });
}
