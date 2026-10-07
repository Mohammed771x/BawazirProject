import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/core/api/wordos_api.dart';
import 'package:wordos/features/words/vocabulary_screen.dart';

import 'support/test_harness.dart';

/// My Words loads twenty, then ten at a time, until every word is on it
/// (ADR-127).
///
/// The bug this replaces: the screen read the server's first page and stopped.
/// A learner with 153 words was told "153 words" and could scroll to fifty.
void main() {
  Future<(ProviderContainer, WordOsApi)> signedIn() async {
    final container = ProviderContainer(overrides: testOverrides());
    addTearDown(container.dispose);

    final api = container.read(wordOsApiProvider);
    final auth =
        await api.login(email: 'demo@wordos.app', password: 'wordos123');
    await container
        .read(tokenStoreProvider)
        .save(auth.token, refreshToken: auth.refreshToken);

    // More than the server's default page of fifty, which is where the old
    // screen stopped. Invented words, because the mock dictionary is small; a
    // word it does not know is accepted with whatever meaning is written.
    const letters = 'bdfgklmnprstv';
    for (var i = 0; i < 60; i++) {
      await api.addWordWithMeaning(
        text: 'ze${letters[i ~/ letters.length]}o${letters[i % letters.length]}ia',
        meaning: 'كلمة',
      );
    }
    return (container, api);
  }

  test('every word is reachable, each exactly once', () async {
    final (container, _) = await signedIn();
    // Held open, as the screen holds it: an auto-disposed provider with no
    // listener would be thrown away between reads.
    container.listen(wordsProvider(''), (_, _) {});
    final notifier = container.read(wordsProvider('').notifier);

    var list = await container.read(wordsProvider('').future);
    expect(list.total, greaterThan(50));
    expect(list.items, hasLength(WordListNotifier.firstPageSize));
    expect(list.hasMore, isTrue);

    await notifier.loadMore();
    list = container.read(wordsProvider('')).requireValue;
    expect(list.items,
        hasLength(WordListNotifier.firstPageSize + WordListNotifier.nextPageSize));

    while (list.hasMore) {
      await notifier.loadMore();
      list = container.read(wordsProvider('')).requireValue;
    }

    expect(list.items, hasLength(list.total));
    expect(list.items.map((w) => w.id).toSet(), hasLength(list.total),
        reason: 'no word twice');
  });

  test('a write elsewhere keeps the learner where they had scrolled to',
      () async {
    final (container, api) = await signedIn();
    container.listen(wordsProvider(''), (_, _) {});
    final notifier = container.read(wordsProvider('').notifier);

    await container.read(wordsProvider('').future);
    await notifier.loadMore();
    await notifier.loadMore();
    final before = container.read(wordsProvider('')).requireValue;
    expect(before.items, hasLength(40));

    // Deleting a word bumps the server revision, which refetches the list.
    final victim = before.items[35];
    await api.deleteWord(victim.id);
    notifier.removeLocally(victim.id);
    final after = await container.read(wordsProvider('').future);

    expect(after.total, before.total - 1);
    expect(after.items.length, greaterThanOrEqualTo(39),
        reason: 'the refetch reloads as deep as the learner had gone');
    expect(after.items.map((w) => w.id), isNot(contains(victim.id)));
  });
}
