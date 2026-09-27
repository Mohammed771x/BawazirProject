import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/core/api/server_revision.dart';
import 'package:wordos/core/api/wordos_api.dart';
import 'package:wordos/core/l10n/app_strings.dart';
import 'package:wordos/core/widgets/app_widgets.dart';
import 'package:wordos/features/hub/hub_screen.dart';
import 'package:wordos/features/words/vocabulary_screen.dart';
import 'package:wordos/mock_backend/engine/mock_dictionary.dart';

import 'support/test_harness.dart';

/// Screens that are never out of date, and errors that clear themselves
/// (ADR-093, ADR-094).
///
/// Both of these were the same shape of bug: the app knew something had
/// changed and the screen did not, because knowing was somebody's job to
/// remember. Now it is nobody's job.
void main() {
  group('a write refreshes what is on screen', () {
    test('every reader refetches, not just the one the writer knew about',
        () async {
      // The bug this replaces: adding a word invalidated the hub and not the
      // word list, so the learner added a word, opened My Words, and it was
      // not there.
      final container = ProviderContainer(overrides: testOverrides());
      addTearDown(container.dispose);

      final api = container.read(wordOsApiProvider);
      // The session controller normally does this; here the container is the
      // whole app, so the token is put where the API reads it from.
      final auth =
          await api.login(email: 'demo@wordos.app', password: 'wordos123');
      await container
          .read(tokenStoreProvider)
          .save(auth.token, refreshToken: auth.refreshToken);

      final before = await container.read(hubProvider.future);
      final wordsBefore = await container.read(wordsProvider('').future);
      final revision = container.read(serverRevisionProvider);

      await api.addWord(MockDictionary.entries['achieve']!.first);

      expect(container.read(serverRevisionProvider), greaterThan(revision),
          reason: 'a write announces itself');

      // Both refetch — neither had to be named by whoever did the writing.
      final after = await container.read(hubProvider.future);
      final wordsAfter = await container.read(wordsProvider('').future);

      expect(after.vocabulary.learning, before.vocabulary.learning + 1);
      expect(wordsAfter.items.length, wordsBefore.items.length + 1);
    });

    test('a read announces nothing', () async {
      // Otherwise every refetch would trigger another one, for ever.
      final container = ProviderContainer(overrides: testOverrides());
      addTearDown(container.dispose);

      final api = container.read(wordOsApiProvider);
      // The session controller normally does this; here the container is the
      // whole app, so the token is put where the API reads it from.
      final auth =
          await api.login(email: 'demo@wordos.app', password: 'wordos123');
      await container
          .read(tokenStoreProvider)
          .save(auth.token, refreshToken: auth.refreshToken);

      final revision = container.read(serverRevisionProvider);
      await api.hub();
      await api.words();
      await api.wordDetail((await api.words()).items.first.id);

      expect(container.read(serverRevisionProvider), revision);
    });

    test('a failed write announces nothing', () async {
      // Telling every screen to refetch because a request was refused turns
      // one error into a burst of requests.
      final container = ProviderContainer(overrides: testOverrides());
      addTearDown(container.dispose);

      final api = container.read(wordOsApiProvider);
      // The session controller normally does this; here the container is the
      // whole app, so the token is put where the API reads it from.
      final auth =
          await api.login(email: 'demo@wordos.app', password: 'wordos123');
      await container
          .read(tokenStoreProvider)
          .save(auth.token, refreshToken: auth.refreshToken);

      final revision = container.read(serverRevisionProvider);

      await expectLater(
        api.deleteWord('not-a-word-id'),
        throwsA(isA<ApiException>()),
      );

      expect(container.read(serverRevisionProvider), revision);
    });

    testWidgets('a list updates under the learner, with no gesture at all',
        (tester) async {
      // The sharpest form of the rule. The learner is *standing on* My Words,
      // a word is added by something else entirely, and the list changes
      // without anyone pulling it down or navigating away and back.
      await bootAndSignIn(tester);

      await tester.tap(find.text('My words').last);
      await tester.pumpAndSettle();

      expect(find.text('achieve'), findsNothing);

      final container = ProviderScope.containerOf(
        tester.element(find.byType(Scaffold).first),
      );
      await container
          .read(wordOsApiProvider)
          .addWord(MockDictionary.entries['achieve']!.first);

      await tester.pumpAndSettle();

      expect(find.text('achieve'), findsWidgets,
          reason: 'the list refetched itself because the server changed');
    });
  });

  group('an error clears itself when the world recovers', () {
    testWidgets('it retries on its own, without being pressed', (tester) async {
      var retries = 0;

      await tester.pumpWidget(MaterialApp(
        home: Scaffold(
          body: ErrorView.from(
            const ApiException('NETWORK', 'No connection.'),
            const AppStrings(Locale('en')),
            onRetry: () => retries++,
          ),
        ),
      ));

      expect(retries, 0);

      // A learner walking out of a tunnel is not going to press anything.
      await tester.pump(const Duration(seconds: 3));
      expect(retries, 1);

      await tester.pump(const Duration(seconds: 5));
      expect(retries, 2);
    });

    testWidgets('it backs off rather than hammering', (tester) async {
      var retries = 0;

      await tester.pumpWidget(MaterialApp(
        home: Scaffold(
          body: ErrorView.from(
            const ApiException('NETWORK', 'No connection.'),
            const AppStrings(Locale('en')),
            onRetry: () => retries++,
          ),
        ),
      ));

      await tester.pump(const Duration(minutes: 1));

      // A minute of being offline is a handful of attempts, not thirty.
      expect(retries, lessThan(8));
      expect(retries, greaterThan(2));
    });

    testWidgets('a failure that cannot heal is not retried', (tester) async {
      // A word that does not exist will refuse identically for ever. Retrying
      // it is a request every few seconds for as long as the screen is open,
      // and it can never do anything.
      var retries = 0;

      await tester.pumpWidget(MaterialApp(
        home: Scaffold(
          body: ErrorView.from(
            const ApiException('WORD_NOT_FOUND', 'Word not found.',
                statusCode: 404),
            const AppStrings(Locale('en')),
            onRetry: () => retries++,
          ),
        ),
      ));

      await tester.pump(const Duration(minutes: 1));

      expect(retries, 0);
    });

    testWidgets('it says what actually happened', (tester) async {
      // "Something went wrong" is what every one of these used to say, to a
      // learner holding a phone with no signal.
      await tester.pumpWidget(MaterialApp(
        home: Scaffold(
          body: ErrorView.from(
            const ApiException('NETWORK', 'No connection.'),
            const AppStrings(Locale('en')),
            onRetry: () {},
          ),
        ),
      ));

      expect(find.text('Something went wrong.'), findsNothing);
      expect(find.textContaining('connection'), findsOneWidget);

      await tester.pump(const Duration(minutes: 1));
      await tester.pumpWidget(const SizedBox());
    });

    testWidgets('the button still works for anyone who presses it',
        (tester) async {
      var retries = 0;

      await tester.pumpWidget(MaterialApp(
        home: Scaffold(
          body: ErrorView.from(
            const ApiException('NETWORK', 'No connection.'),
            const AppStrings(Locale('en')),
            onRetry: () => retries++,
          ),
        ),
      ));

      await tester.tap(find.byType(OutlinedButton));
      await tester.pump();

      expect(retries, 1, reason: 'waiting for a timer nobody can see is its '
          'own kind of stuck');

      await tester.pumpWidget(const SizedBox());
    });
  });
}
