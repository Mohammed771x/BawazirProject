import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/core/api/server_revision.dart';
import 'package:wordos/core/models/models.dart';
import 'package:wordos/core/api/wordos_api.dart';
import 'package:wordos/core/widgets/app_widgets.dart';
import 'package:wordos/mock_backend/engine/mock_dictionary.dart';
import 'package:wordos/mock_backend/engine/mock_engine.dart';
import 'package:wordos/mock_backend/mock_wordos_api.dart';

import 'support/test_harness.dart';

/// Practice after the weekly challenge (ADR-120).
///
/// The product owner's request: a learner who has finished this week's review
/// and comes back later in the week can go over its words again. It is not the
/// weekly review — that stays one measurement a week (rule R9) — so nothing a
/// practice does may reach anything the challenge reads.
void main() {
  late MockEngine engine;
  late MockUser user;
  final missed = <String>{};

  setUp(() {
    engine = MockEngine();
    final auth = engine.register('learner@test.dev', 'wordos123', 'Learner',
        phoneCountryCode: '967', phoneNumber: '770000031');
    user = engine.requireUser(auth.token);
  });

  WordCandidate candidate(String key) => MockDictionary.entries[key]!.first;

  void ripen() => engine.advanceClock(
        const Duration(days: MockEngine.reviewMaturityDays),
      );

  /// Finishes the open challenge: the first word right, every other word
  /// missed once and then named.
  void finishChallenge() {
    final review = engine.startWeeklyReview(user);
    final meanings = {for (final w in user.words) w.id: w.meaning};
    var item = review.queue.isEmpty ? null : review.queue.first;
    var first = true;
    for (var guard = 0; item != null && guard < 200; guard++) {
      final right = meanings[item.wordId]!;
      final wrong = item.options.firstWhere((o) => o != right);
      // The first word named; every other word missed once, then named.
      final missFirst = !first && !missed.contains(item.id);
      if (missFirst) missed.add(item.id);
      first = false;
      item = engine
          .answerWeeklyReview(
              user, review.id, item.id, missFirst ? wrong : right)
          .nextItem;
    }
    engine.completeWeeklyReview(user, review.id);
  }

  /// Everything the weekly challenge reads, for every word.
  String trace() => [
        for (final w in user.words)
          '${w.text}:${w.reviewPassedAt}:${w.lastReviewedAt}',
        '${user.lastWeeklyReviewAt}',
        '${engine.hub(user).weeklyReview.nextAvailableAt}',
      ].join('|');

  group('when practice is offered', () {
    test('not before the first review is finished', () {
      engine.addWord(user, candidate('research'));

      expect(engine.hub(user).weeklyReview.practiceAvailable, isFalse);
      expect(
        () => engine.startWeeklyReviewPractice(user),
        throwsA(isA<ApiException>().having(
            (e) => e.code, 'code', 'PRACTICE_NOTHING_TO_PRACTISE')),
      );
    });

    test('once the challenge is done, over the same words', () {
      engine.addWord(user, candidate('research'));
      engine.addWord(user, candidate('book'));
      ripen();
      finishChallenge();

      final card = engine.hub(user).weeklyReview;
      expect(card.available, isFalse);
      expect(card.practiceAvailable, isTrue,
          reason: 'the card must not simply lock for the rest of the week');
      expect(card.practiceWordCount, 2);

      final practice = engine.startWeeklyReviewPractice(user);
      expect(practice.isPractice, isTrue);
      expect(practice.queue.map((i) => i.prompt).toSet(),
          {'research', 'book'});
    });

    test('not while a challenge is open: then the card is the challenge', () {
      engine.addWord(user, candidate('research'));
      ripen();
      finishChallenge();
      engine.addWord(user, candidate('book'));
      ripen();

      final card = engine.hub(user).weeklyReview;
      expect(card.available, isTrue);
      expect(card.practiceAvailable, isFalse);
    });
  });

  test('a practice records nothing the challenge reads (rule R9)', () {
    engine.addWord(user, candidate('research'));
    engine.addWord(user, candidate('book'));
    ripen();
    finishChallenge();
    final before = trace();

    engine.advanceClock(const Duration(days: 2));
    final practice = engine.startWeeklyReviewPractice(user);
    for (final item in List.of(practice.queue)) {
      engine.answerWeeklyReview(user, practice.id, item.id, 'wrong on purpose');
    }
    final result = engine.completeWeeklyReview(user, practice.id);

    expect(result.isPractice, isTrue);
    expect(trace(), before);
    // And it can be done again, any day of the week.
    expect(engine.startWeeklyReviewPractice(user).totalWords, 2);
  });

  testWidgets('the hub card opens a practice that says what it is',
      (tester) async {
    // The seeded demo account, with its challenge already done this week.
    final shared = MockEngine();
    final demo = shared.requireUser(
        shared.login('demo@wordos.app', 'wordos123').token);
    final meanings = <String, String>{};
    engine = shared;
    user = demo;
    if (!shared.hub(demo).weeklyReview.available) ripen();
    for (final w in demo.words) {
      meanings[w.text] = w.meaning;
    }
    finishChallenge();
    expect(shared.hub(demo).weeklyReview.practiceAvailable, isTrue);

    tester.view.physicalSize = const Size(1200, 3000);
    tester.view.devicePixelRatio = 3;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(ProviderScope(
      overrides: [
        ...testOverrides(),
        wordOsApiProvider.overrideWith((ref) {
          final tokens = ref.watch(tokenStoreProvider);
          return MockWordOsApi(
            tokenReader: () => tokens.token,
            engine: shared,
            latencyScale: 0,
            onChanged: () =>
                ref.read(serverRevisionProvider.notifier).bump(),
          );
        }),
      ],
      child: const WordOsApp(),
    ));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Sign in'));
    await tester.pumpAndSettle();

    final practiseLine = find.textContaining('Done for this week');
    await tester.scrollUntilVisible(practiseLine, 200,
        scrollable: find.byType(Scrollable).first);
    await tester.tap(practiseLine);
    await tester.pumpAndSettle();

    expect(find.text('Word practice'), findsOneWidget);
    expect(find.textContaining('It is not your weekly review'), findsWidgets);

    // Name every word, whatever order they come in.
    for (var guard = 0; guard < 60; guard++) {
      if (find.byType(OptionTile).evaluate().isEmpty) break;
      final finish = find.widgetWithText(FilledButton, 'Finish');
      if (finish.evaluate().isNotEmpty) break;
      final prompt = meanings.keys.firstWhere(
          (text) => find.text(text).evaluate().isNotEmpty);
      await tester.tap(find.widgetWithText(OptionTile, meanings[prompt]!));
      await tester.pump(const Duration(seconds: 1));
      await tester.pumpAndSettle();
    }
    // The last answer moves straight on to the result.
    final finish = find.widgetWithText(FilledButton, 'Finish');
    if (finish.evaluate().isNotEmpty) {
      await tester.tap(finish);
      await tester.pumpAndSettle();
    }

    expect(find.text('Practice score'), findsOneWidget);
    expect(find.text('100%'), findsOneWidget);
    expect(find.text('Weekly score'), findsNothing);
  });
}
