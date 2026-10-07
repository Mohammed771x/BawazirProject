import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:wordos/features/words/vocabulary_screen.dart';
import 'package:wordos/features/words/word_widgets.dart';

import 'support/journey.dart';

/// My Words reaches every word a large vocabulary holds, against the real API
/// (ADR-127).
///
/// The bug: a learner with 153 words was told "153 words" and could scroll to
/// fifty. This needs an account that already owns well over fifty words, so it
/// is given one rather than building it through the UI:
///
/// ```bash
/// flutter test integration_test/my_words_paging_test.dart -d <simulator> \
///   --dart-define=WORDOS_TEST_EMAIL=... --dart-define=WORDOS_TEST_PASSWORD=...
/// ```
///
/// It deletes one of the account's newest words on the way down.
const _email = String.fromEnvironment('WORDOS_TEST_EMAIL');
const _password = String.fromEnvironment('WORDOS_TEST_PASSWORD');

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('scrolling My Words reaches every word, each once',
      (tester) async {
    if (_email.isEmpty || _password.isEmpty) {
      markTestSkipped('needs WORDOS_TEST_EMAIL and WORDOS_TEST_PASSWORD');
      return;
    }

    await boot(tester);
    final fields = find.byType(TextFormField);
    await tester.enterText(fields.at(0), _email);
    await tester.enterText(fields.at(1), _password);
    await tester.tap(find.widgetWithText(FilledButton, 'Sign in'));
    await waitFor(tester, () => isOnHub(tester),
        total: const Duration(seconds: 30));

    await tapAny(tester, ['My words']);
    await waitFor(tester, () => find.byType(WordTile).evaluate().isNotEmpty);
    await settle(tester);

    WordList list() => ProviderScope.containerOf(
          tester.element(find.byType(VocabularyScreen)),
        ).read(wordsProvider('')).requireValue;

    final total = list().total;
    expect(total, greaterThan(50),
        reason: 'the account must own more than the old fifty-row ceiling');
    expect(find.text('$total words'), findsOneWidget);
    expect(list().items, hasLength(WordListNotifier.firstPageSize),
        reason: 'opening the list reads twenty rows, not fifty');
    debugPrint('✓ PAGING · opened with ${list().items.length} of $total');

    final seen = <String>{};
    void collect() => seen.addAll(tester
        .widgetList<WordTile>(find.byType(WordTile))
        .map((t) => t.word.id));

    String? deleted;
    final scrollable = find.byType(Scrollable).last;

    for (var step = 0; step < 400; step++) {
      collect();
      final current = list();
      if (!current.hasMore && seen.length >= current.items.length) break;

      // Partway down — among the newest words — delete one by swiping it,
      // and check the list keeps the learner where they were.
      if (deleted == null && current.items.length >= 60) {
        final tile = find.byType(WordTile).first;
        final victim = tester.widget<WordTile>(tile).word;
        final loadedBefore = current.items.length;

        await tester.drag(tile, const Offset(-500, 0));
        await tester.pumpAndSettle();
        await tester.tap(find.widgetWithText(FilledButton, 'Delete word'));
        await waitFor(
            tester, () => find.text('${total - 1} words').evaluate().isNotEmpty,
            total: const Duration(seconds: 20));
        await settle(tester);

        deleted = victim.id;
        seen.remove(deleted);
        expect(list().items.map((w) => w.id), isNot(contains(deleted)));
        expect(list().items.length, greaterThanOrEqualTo(loadedBefore - 1),
            reason: 'a delete must not throw the learner back to the top');
        debugPrint('✓ PAGING · deleted "${victim.text}" at depth '
            '$loadedBefore; ${list().items.length} still loaded');
      }

      await tester.drag(scrollable, const Offset(0, -400));
      await tester.pump(const Duration(milliseconds: 100));
      await settle(tester, total: const Duration(seconds: 15));
    }

    final expected = deleted == null ? total : total - 1;
    final ids = list().items.map((w) => w.id).toList();

    expect(list().hasMore, isFalse, reason: 'scrolled to the very end');
    expect(ids, hasLength(expected));
    expect(ids.toSet(), hasLength(expected), reason: 'no word twice');
    expect(seen, hasLength(expected),
        reason: 'every word was actually drawn on screen');

    // The count heads the list, so it is back at the top.
    for (var i = 0;
        i < 40 && find.text('$expected words').evaluate().isEmpty;
        i++) {
      await tester.fling(scrollable, const Offset(0, 2000), 4000);
      await tester.pumpAndSettle();
    }
    expect(find.text('$expected words'), findsOneWidget);
    expect(list().items, hasLength(expected),
        reason: 'scrolling back up refetches nothing and drops nothing');
    debugPrint('✓ PAGING · scrolled through all $expected words, each once');
  }, timeout: const Timeout(Duration(minutes: 10)));
}
