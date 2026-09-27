import 'package:flutter_test/flutter_test.dart';
import 'package:flutter/widgets.dart';
import 'package:wordos/core/api/wordos_api.dart';
import 'package:wordos/core/l10n/app_strings.dart';
import 'package:wordos/core/models/models.dart';
import 'package:wordos/core/widgets/app_widgets.dart';
import 'package:wordos/mock_backend/engine/mock_dictionary.dart';
import 'package:wordos/mock_backend/engine/mock_engine.dart';

import 'support/test_harness.dart';

/// The weekly challenge: when it opens, how much it asks at once, and how its
/// progress is shown (ADR-089, ADR-091).
void main() {
  late MockEngine engine;
  late MockUser user;

  setUp(() {
    engine = MockEngine();
    final auth = engine.register('learner@test.dev', 'wordos123', 'Learner',
        phoneCountryCode: '967', phoneNumber: '770000021');
    user = engine.requireUser(auth.token);
  });

  WordCandidate candidate(String key) => MockDictionary.entries[key]!.first;

  /// Every distinct sense the mock dictionary holds — seventeen of them.
  ///
  /// Which is why the *ceiling* of fifty is tested on the backend, against
  /// `WeeklyReviewPolicy`, where a word costs nothing to construct. What is
  /// testable here is everything the learner can actually reach with the
  /// vocabulary this stand-in has: ripening, carrying over, and leaving the
  /// pool once asked.
  late final List<WordCandidate> allCandidates = [
    for (final senses in MockDictionary.entries.values) ...senses,
  ];

  /// Adds [count] distinct words, continuing from where the last call stopped.
  var added = 0;
  void addWords(int count) {
    for (var i = 0; i < count; i++) {
      engine.addWord(user, allCandidates[added++]);
    }
  }

  void ripen() => engine.advanceClock(
        const Duration(days: MockEngine.reviewMaturityDays),
      );

  group('when it opens', () {
    test('a word added today is not in this week\'s challenge', () {
      // The whole point of the gap. A word tested the evening it was added is
      // not being reviewed — it is being taught again, the learner still has
      // it in mind, and the score says nothing about retention (rule R9).
      engine.addWord(user, candidate('research'));

      expect(
        () => engine.startWeeklyReview(user),
        throwsA(isA<ApiException>()
            .having((e) => e.code, 'code', 'REVIEW_NOT_READY')),
      );
    });

    test('the hub names the day it opens instead of only refusing', () {
      engine.addWord(user, candidate('research'));

      final hub = engine.hub(user).weeklyReview;

      expect(hub.available, isFalse);
      expect(hub.nextAvailableAt, isNotNull,
          reason: 'a learner in their first week should be told when');
      expect(
        hub.nextAvailableAt!.difference(engine.now).inDays,
        MockEngine.reviewMaturityDays - 1,
      );
    });

    test('it opens once the week has passed', () {
      engine.addWord(user, candidate('research'));
      ripen();

      expect(engine.hub(user).weeklyReview.available, isTrue);
      expect(engine.startWeeklyReview(user).totalWords, 1);
    });

    test('a learner who has reviewed everything is told so, not refused', () {
      // A different sentence from "not ready yet", because it is a different
      // situation: there is no date to wait for.
      engine.addWord(user, candidate('research'));
      ripen();

      final review = engine.startWeeklyReview(user);
      final item = review.queue.single;
      engine.answerWeeklyReview(
          user, review.id, item.id, candidate('research').meaning);
      engine.completeWeeklyReview(user, review.id);

      expect(
        () => engine.startWeeklyReview(user),
        throwsA(isA<ApiException>()
            .having((e) => e.code, 'code', 'REVIEW_NOTHING_TO_REVIEW')),
      );
    });
  });

  group('the backlog', () {
    test('nothing is waiting behind a sitting that fits', () {
      addWords(6);
      ripen();

      final hub = engine.hub(user).weeklyReview;

      expect(hub.wordCount, 6);
      expect(hub.wordsWaitingAfterThis, 0,
          reason: 'six words is one sitting, and there is no second group');
    });

    test('a word skipped last week comes back with this week\'s', () {
      // Carrying over is the feature: the words worth asking about are exactly
      // the ones belonging to the week somebody was too busy to review.
      addWords(3);
      ripen();

      // A week goes by untouched, and three more words are added and ripen.
      engine.advanceClock(const Duration(days: 3));
      addWords(2);
      ripen();

      expect(engine.startWeeklyReview(user).totalWords, 5,
          reason: 'last week\'s words did not expire');
    });

    test('answering takes a word out of the pool for good', () {
      addWords(2);
      ripen();

      final review = engine.startWeeklyReview(user);
      final first = review.queue.first;
      final wordText = engine
          .words(user, null)
          .items
          .firstWhere((w) => w.id == first.wordId);

      engine.answerWeeklyReview(
          user, review.id, first.id, wordText.meaning);
      engine.completeWeeklyReview(user, review.id);

      // The one still owed a review is the only one offered again.
      expect(engine.startWeeklyReview(user).totalWords, 1);
    });
  });

  group('the card on the hub', () {
    testWidgets('an open challenge says how much it will ask', (tester) async {
      // The seeded learner has words old enough to have ripened, so their card
      // is the open one.
      await bootAndSignIn(tester);

      expect(find.text('Weekly Review'), findsOneWidget);
      expect(find.textContaining('words ready'), findsWidgets);

      await tester.tap(find.text('Weekly Review'));
      await tester.pumpAndSettle();

      expect(find.textContaining('measures'), findsWidgets,
          reason: 'the card opened the challenge');
    });

    test('a challenge still ripening is shown waiting, not hidden', () {
      // A learner's first week has no challenge in it. A card that simply is
      // not there teaches them the feature does not exist; one that names the
      // day tells them it is coming (ADR-089). The hub decides by sending a
      // date, and this is the state that produces it.
      engine.addWord(user, candidate('research'));

      final status = engine.hub(user).weeklyReview;

      expect(status.available, isFalse);
      expect(status.nextAvailableAt, isNotNull);

      for (final locale in [const Locale('en'), const Locale('ar')]) {
        final line = AppStrings(locale).challengeOpensOn('24 Sep');
        expect(line.trim(), isNotEmpty);
        expect(line, contains('24 Sep'));
      }
    });
  });

  group('the progress bar', () {
    test('it starts at nothing and arrives exactly full', () {
      // The curve flatters the middle. It must not flatter the end: the bar
      // reaching full before the challenge does is the one lie a learner
      // catches immediately.
      expect(challengeProgress(0, 50), 0);
      expect(challengeProgress(50, 50), 1);
      expect(challengeProgress(3, 3), 1);
    });

    test('the first few words move it further than the later ones', () {
      // The reason it exists. Fifty questions on a linear bar means the first
      // answer moves it two per cent, which reads as nothing happening — and
      // the learner most likely to give up is the one who has answered three
      // and cannot see that they have.
      final firstFive = challengeProgress(5, 50) - challengeProgress(0, 50);
      final middleFive = challengeProgress(30, 50) - challengeProgress(25, 50);

      expect(firstFive, greaterThan(middleFive * 2));
      expect(challengeProgress(1, 50), greaterThan(1 / 50));
    });

    test('it only ever moves forwards', () {
      for (var total in [3, 6, 12, 50]) {
        var previous = -1.0;
        for (var done = 0; done <= total; done++) {
          final value = challengeProgress(done, total);
          expect(value, greaterThanOrEqualTo(previous),
              reason: 'went backwards at $done of $total');
          expect(value, inInclusiveRange(0, 1));
          previous = value;
        }
      }
    });

    test('a short challenge is not sprinted through', () {
      // With five words the "opening stretch" would be the whole thing, and
      // the second answer would fill the bar — the same lie in the other
      // direction.
      expect(challengeProgress(2, 4), 0.5);
      expect(challengeProgress(1, 3), closeTo(1 / 3, 0.0001));
    });

    test('nothing divides by zero', () {
      expect(challengeProgress(0, 0), 0);
      expect(challengeProgress(5, 0), 0);
      expect(challengeProgress(-1, 10), 0);
    });
  });
}
