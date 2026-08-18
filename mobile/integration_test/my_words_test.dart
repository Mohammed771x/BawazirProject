import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:wordos/features/words/word_widgets.dart';

import 'support/journey.dart';

/// My Words against the real API (Part 2 §42–§46).
///
/// The widget tests prove the screen; this proves the query behind it — the
/// search runs in PostgreSQL, over the learner's own rows only, and matches
/// Arabic meanings as readily as English spellings.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('the learner can find their own words by either language',
      (tester) async {
    await bootFreshLearner(tester, prefix: 'mywords');
    final meaning = await addWord(tester, 'research');
    await addWord(tester, 'evidence');

    await tapAny(tester, ['My words']);
    await settle(tester, total: const Duration(seconds: 30));

    expect(find.byType(WordTile), findsNWidgets(2));
    expect(find.byType(TabBar), findsNothing,
        reason: 'pipeline states are not a navigation structure (§42)');

    // English spelling, partially typed. The search is debounced and then runs
    // on the server, so the list is briefly a spinner — waiting on the result
    // rather than on the widget tree settling is the honest way to see it.
    await search(tester, 'resea');
    expect(find.byType(WordTile), findsOneWidget,
        reason: 'Visible: ${visibleText(tester).take(8)}');
    expect(find.text('research'), findsWidgets);

    // The Arabic meaning the learner chose when they added it — often the
    // handle they actually remember.
    await search(tester, meaning);
    expect(find.byType(WordTile), findsWidgets,
        reason: 'searching by meaning should find the word. '
            'Searched "$meaning", visible: ${visibleText(tester).take(8)}');
    debugPrint('✓ MY WORDS · found by meaning "$meaning"');

    await search(tester, 'zzzznotaword');
    expect(find.text('No words match that search'), findsOneWidget);
    debugPrint('✓ MY WORDS · an empty search result says so');
  }, timeout: const Timeout(Duration(minutes: 10)));
}

/// Types a search term and waits for the server to answer it.
///
/// The previous results stay on screen while the new query is in flight, so
/// waiting for "some tiles" would pass instantly on the old list. What marks
/// the new answer is the list changing — a different number of rows, or the
/// empty-result message.
Future<void> search(WidgetTester tester, String term) async {
  final before = find.byType(WordTile).evaluate().length;
  await tester.enterText(find.byType(TextField).first, term);
  await waitFor(
      tester,
      () =>
          // ...and settled: the spinner replacing the list is also "different".
          find.textContaining('Loading').evaluate().isEmpty &&
          (find.byType(WordTile).evaluate().length != before ||
              find.textContaining('No words match').evaluate().isNotEmpty),
      total: const Duration(seconds: 30));
}
