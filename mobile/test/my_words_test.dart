import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/features/words/word_widgets.dart';

import 'support/test_harness.dart';

/// My Words (Part 2 §42–§46).
///
/// The screen a learner opens to find a word they added, which is a different
/// job from the one the old three-tab layout did: Learning / Active / Archived
/// are pipeline states, and asking someone to know which tab their word is in
/// means asking them to understand the state machine first.
void main() {
  Future<void> openMyWords(WidgetTester tester) async {
    await bootAndSignIn(tester);
    await tester.tap(find.text('My words').last);
    await tester.pumpAndSettle();
  }

  testWidgets('one list, not a tab per pipeline state', (tester) async {
    await openMyWords(tester);

    expect(find.byType(TabBar), findsNothing,
        reason: 'the pipeline states are not a navigation structure (§42)');

    // Every word the learner owns is here — including the ones the system has
    // filed as Active or Archived. A word is never deleted (R8), and vanishing
    // from this screen would be indistinguishable from deletion.
    expect(find.byType(WordTile), findsWidgets);
    expect(find.text('Archived'), findsNothing,
        reason: 'internal state names are not shown to the learner');
  });

  testWidgets('searching narrows the list to matching words', (tester) async {
    await openMyWords(tester);

    final before = find.byType(WordTile).evaluate().length;
    expect(before, greaterThan(1), reason: 'the demo learner owns several words');

    await tester.enterText(find.byType(TextField).first, 'hardware');
    await tester.pump(const Duration(milliseconds: 400)); // debounce
    await tester.pumpAndSettle();

    final after = find.byType(WordTile).evaluate().length;
    expect(after, lessThan(before));
    expect(after, greaterThan(0), reason: 'the searched word should be found');
  });

  testWidgets('a search with no matches says so rather than looking empty',
      (tester) async {
    await openMyWords(tester);

    await tester.enterText(find.byType(TextField).first, 'zzzznotaword');
    await tester.pump(const Duration(milliseconds: 400));
    await tester.pumpAndSettle();

    // "No words here yet" would be a lie — they have plenty, just none of
    // these.
    expect(find.text('No words match that search'), findsOneWidget);
  });

  testWidgets('the search runs on the server, over meanings too',
      (tester) async {
    await openMyWords(tester);

    // Arabic in, English word out: the meaning is as good a handle on a word as
    // its spelling, and often the one the learner remembers.
    await tester.enterText(find.byType(TextField).first, 'عتاد');
    await tester.pump(const Duration(milliseconds: 400));
    await tester.pumpAndSettle();

    expect(find.byType(WordTile), findsWidgets);
    expect(find.text('hardware'), findsWidgets);
  });
}
