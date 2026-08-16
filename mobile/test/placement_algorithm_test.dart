import 'dart:math';

import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/api/wordos_api.dart';
import 'package:wordos/core/models/models.dart';
import 'package:wordos/mock_backend/engine/placement/ability_estimator.dart';
import 'package:wordos/mock_backend/engine/placement/placement_engine.dart';
import 'package:wordos/mock_backend/engine/placement/placement_item_bank.dart';

/// These tests are written as a **specification of the placement algorithm**,
/// not as tests of the mock's internals. Phase 5 should port them to xUnit
/// scenario-for-scenario when the C# backend takes the algorithm over
/// (`docs/06-PLACEMENT-ALGORITHM.md`).
void main() {
  /// Runs a whole placement test, answering each item via [respond].
  ///
  /// [respond] receives the bank item behind the question so a test can decide
  /// "answer correctly" or "answer wrongly" deterministically — the projected
  /// item deliberately does not carry the correct answer (rule R7).
  PlacementResult runPlacement(
    String Function(BankItem item) respond, {
    PlacementEngine? engine,
  }) {
    final e = engine ?? PlacementEngine(random: Random(7));
    var step = e.start('pt_test', 'u_test');

    var guard = 0;
    while (!step.isComplete) {
      guard++;
      expect(guard, lessThan(100),
          reason: 'the adaptive loop must always terminate');

      final item = step.item!;
      final bank = PlacementItemBank.forSkill(item.skill)
          .firstWhere((b) => b.id == item.id);
      step = e.answer(step.sessionId, item.id, respond(bank));
    }
    return e.complete(step.sessionId);
  }

  String correctFor(BankItem item) =>
      item.isFreeText ? _competentAnswerFor(item) : item.correctAnswer!;

  String wrongFor(BankItem item) => item.isFreeText
      ? 'no'
      : item.options.firstWhere((o) => o != item.correctAnswer);

  CefrLevel? levelOf(PlacementResult r, SkillType skill) =>
      r.levels.firstWhere((l) => l.skill == skill).systemAssessedLevel;

  group('the estimator', () {
    test('a correct answer raises ability and a wrong one lowers it', () {
      const estimator = AbilityEstimator();
      final neutral = estimator.estimate(const []);

      final afterCorrect = estimator.estimate(const [
        ScoredResponse(itemId: 'a', difficulty: 0, score: 1),
      ]);
      final afterWrong = estimator.estimate(const [
        ScoredResponse(itemId: 'a', difficulty: 0, score: 0),
      ]);

      expect(afterCorrect.theta, greaterThan(neutral.theta));
      expect(afterWrong.theta, lessThan(neutral.theta));
    });

    test('more answers shrink the standard error', () {
      const estimator = AbilityEstimator();
      final few = estimator.estimate(const [
        ScoredResponse(itemId: 'a', difficulty: 0, score: 1),
      ]);
      final many = estimator.estimate(const [
        ScoredResponse(itemId: 'a', difficulty: 0.0, score: 1),
        ScoredResponse(itemId: 'b', difficulty: 0.5, score: 1),
        ScoredResponse(itemId: 'c', difficulty: 1.0, score: 0),
        ScoredResponse(itemId: 'd', difficulty: 0.5, score: 1),
        ScoredResponse(itemId: 'e', difficulty: 1.0, score: 0),
      ]);

      expect(many.standardError, lessThan(few.standardError));
    });

    test('a long run of answers does not underflow into NaN', () {
      const estimator = AbilityEstimator();
      final estimate = estimator.estimate([
        for (var i = 0; i < 200; i++)
          ScoredResponse(itemId: '$i', difficulty: 2.5, score: 0),
      ]);

      expect(estimate.theta.isFinite, isTrue);
      expect(estimate.standardError.isFinite, isTrue);
    });

    test('partial credit sits between a wrong and a right answer', () {
      const estimator = AbilityEstimator();
      double thetaFor(double score) => estimator.estimate([
            ScoredResponse(itemId: 'a', difficulty: 0, score: score),
          ]).theta;

      expect(thetaFor(0.5), greaterThan(thetaFor(0.0)));
      expect(thetaFor(0.5), lessThan(thetaFor(1.0)));
    });
  });

  group('a full placement run', () {
    test('a learner who answers everything correctly places high', () {
      final result = runPlacement(correctFor);

      for (final skill in PlacementItemBank.cefrSkills) {
        final level = levelOf(result, skill);
        expect(level, isNotNull);
        expect(level!.rank, greaterThanOrEqualTo(CefrLevel.b1.rank),
            reason: '$skill should not place low after a perfect run');
      }
    });

    test('a learner who answers everything wrongly places low', () {
      final result = runPlacement(wrongFor);

      for (final skill in PlacementItemBank.cefrSkills) {
        final level = levelOf(result, skill)!;
        expect(level.rank, lessThanOrEqualTo(CefrLevel.a2.rank),
            reason: '$skill should place low after an all-wrong run');
      }
    });

    test('strong reading and weak listening produce different levels', () {
      final result = runPlacement((item) => switch (item.skill) {
            SkillType.reading => correctFor(item),
            SkillType.listening => wrongFor(item),
            _ => correctFor(item),
          });

      final reading = levelOf(result, SkillType.reading)!;
      final listening = levelOf(result, SkillType.listening)!;

      expect(reading.rank, greaterThan(listening.rank),
          reason: 'skills are measured independently, never averaged');
    });

    test('the test always terminates and asks a bounded number of questions',
        () {
      final engine = PlacementEngine(random: Random(3));
      var step = engine.start('pt_bounded', 'u_1');
      var asked = 0;

      while (!step.isComplete && asked < 200) {
        asked++;
        final item = step.item!;
        final bank = PlacementItemBank.forSkill(item.skill)
            .firstWhere((b) => b.id == item.id);
        step = engine.answer(step.sessionId, item.id, correctFor(bank));
      }

      expect(step.isComplete, isTrue);
      expect(asked, lessThanOrEqualTo(24),
          reason: 'the caps in PlacementConfig bound the test length');
      expect(asked, greaterThanOrEqualTo(10),
          reason: 'every skill must contribute at least its minimum items');
    });

    test('no question is ever asked twice', () {
      final engine = PlacementEngine(random: Random(11));
      var step = engine.start('pt_unique', 'u_1');
      final seen = <String>{};

      while (!step.isComplete) {
        final item = step.item!;
        expect(seen.add(item.id), isTrue, reason: '${item.id} was repeated');
        final bank = PlacementItemBank.forSkill(item.skill)
            .firstWhere((b) => b.id == item.id);
        step = engine.answer(step.sessionId, item.id, correctFor(bank));
      }
    });
  });

  group('spelling', () {
    test('is measured but never assigned a CEFR level', () {
      final result = runPlacement(correctFor);
      final spelling =
          result.levels.firstWhere((l) => l.skill == SkillType.spelling);

      expect(spelling.systemAssessedLevel, isNull);
      expect(spelling.userSelectedLevel, isNull);
      expect(spelling.carriesCefrLevel, isFalse);
      expect(result.spelling.itemsAnswered, greaterThan(0));
    });

    test('a strong speller starts on free typing, a weak one on letter tiles',
        () {
      final strong = runPlacement(correctFor);
      final weak = runPlacement(wrongFor);

      expect(strong.spelling.supportMode, SpellingInputMode.freeTyping);
      expect(strong.spelling.accuracy, 1.0);
      expect(weak.spelling.supportMode, SpellingInputMode.letterTiles);
      expect(weak.spelling.accuracy, 0.0);
    });
  });

  group('uncertainty', () {
    test('an erratic learner is placed with low confidence and flagged', () {
      // Alternates right and wrong, which is exactly the pattern that leaves
      // the posterior wide.
      var flip = false;
      final result = runPlacement((item) {
        flip = !flip;
        return flip ? correctFor(item) : wrongFor(item);
      });

      expect(result.hasLowConfidence, isTrue,
          reason: 'inconsistent answers must not yield a confident level');
      for (final level in result.levels.where((l) => l.carriesCefrLevel)) {
        expect(level.confidence, inInclusiveRange(0.0, 1.0));
      }
    });

    test('a learner inside the bank\'s range is placed more confidently than '
        'one who tops it out', () {
      // Under a Rasch model the posterior width is driven by how *informative*
      // the items were — that is, how close their difficulty sat to the
      // learner's ability — not by whether the answers were consistent. So the
      // meaningful contrast is a mid-band learner (well covered by the bank)
      // against a perfect scorer, whose ability runs off the top of it. A wide
      // posterior in the second case is honest: the test only established
      // "at least C1".
      final midBand = runPlacement((item) => item.level.rank <= CefrLevel.b1.rank
          ? correctFor(item)
          : wrongFor(item));
      final topsOut = runPlacement(correctFor);

      double meanConfidence(PlacementResult r) {
        final levels = r.levels.where((l) => l.carriesCefrLevel).toList();
        return levels.map((l) => l.confidence).reduce((a, b) => a + b) /
            levels.length;
      }

      expect(meanConfidence(midBand), greaterThan(meanConfidence(topsOut)));
    });
  });

  group('protocol errors', () {
    test('answering a question that is not the current one is rejected', () {
      final engine = PlacementEngine(random: Random(5));
      final step = engine.start('pt_stale', 'u_1');

      expect(
        () => engine.answer(step.sessionId, 'not_the_current_item', 'x'),
        throwsA(isA<ApiException>()
            .having((e) => e.code, 'code', 'ITEM_NOT_CURRENT')),
      );
    });

    test('completing an unfinished test is rejected', () {
      final engine = PlacementEngine(random: Random(5));
      final step = engine.start('pt_early', 'u_1');

      expect(
        () => engine.complete(step.sessionId),
        throwsA(isA<ApiException>()
            .having((e) => e.code, 'code', 'PLACEMENT_INCOMPLETE')),
      );
    });

    test('an unknown session is rejected rather than silently restarted', () {
      final engine = PlacementEngine(random: Random(5));

      expect(
        () => engine.answer('pt_ghost', 'x', 'y'),
        throwsA(isA<ApiException>()
            .having((e) => e.code, 'code', 'PLACEMENT_NOT_FOUND')),
      );
    });

    test('an empty free-text answer scores zero rather than crashing', () {
      final result = runPlacement((item) => item.isFreeText ? '' : wrongFor(item));

      expect(levelOf(result, SkillType.writing), isNotNull);
      expect(levelOf(result, SkillType.speaking), isNotNull);
    });
  });

  test('options reaching the client are shuffled, and the correct answer is '
      'never sent', () {
    // Over many starts the first option must not always be the same one, and
    // the projected item carries no `correctAnswer` field at all.
    final firstOptions = <String>{};
    for (var seed = 0; seed < 25; seed++) {
      final engine = PlacementEngine(random: Random(seed));
      final step = engine.start('pt_$seed', 'u_1');
      final item = step.item!;
      firstOptions.add(item.options.first);
      expect(item.toJson().containsKey('correctAnswer'), isFalse);
    }

    expect(firstOptions.length, greaterThan(1),
        reason: 'the backend shuffles options (rule R7)');
  });
}

/// A response long and varied enough that the offline scorer treats it as
/// competent for the item's band.
String _competentAnswerFor(BankItem item) {
  const sentence =
      'I usually plan my week carefully because it helps me focus, and when '
      'something unexpected happens I adjust the plan instead of abandoning it '
      'entirely, which keeps my progress steady over time and reduces stress.';
  final words = sentence.split(' ');
  final needed = item.expectedWords == 0 ? 8 : item.expectedWords;
  return words.take(needed.clamp(1, words.length)).join(' ');
}
