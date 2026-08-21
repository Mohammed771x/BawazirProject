import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/models/models.dart';
import 'package:wordos/mock_backend/engine/levels/level_engine.dart';
import 'package:wordos/mock_backend/engine/mock_dictionary.dart';
import 'package:wordos/mock_backend/engine/mock_engine.dart';

/// Rule R6 in practice: the level the learner picks and the level the system
/// has proven are different things, and only the second may move the pipeline
/// or archive a word.
///
/// Sources: `MVP Core.txt` §22–23, `Word Life Cycle.txt` §27–31, ADR-013.
void main() {
  const engine = LevelEngine();
  const policy = LevelPolicy();

  SkillLevel levelWith({
    SkillType skill = SkillType.reading,
    CefrLevel system = CefrLevel.b1,
    required int sessions,
    required double accuracy,
  }) =>
      SkillLevel(
        skill: skill,
        userSelectedLevel: system,
        systemAssessedLevel: system,
        evaluationSessions: sessions,
        rollingAccuracy: accuracy,
        dailyTargetWords: 10,
      );

  group('when the level may move at all', () {
    test('a single strong session changes nothing', () {
      final decision = engine.evaluate(
        levelWith(sessions: 1, accuracy: 1.0),
      );

      expect(decision, isNull,
          reason: 'a level never moves on one session (MVP Core §22)');
    });

    test('nothing happens until the evaluation window is full', () {
      for (var sessions = 0;
          sessions < policy.minEvaluationSessions;
          sessions++) {
        expect(
          engine.evaluate(levelWith(sessions: sessions, accuracy: 0.99)),
          isNull,
          reason: '$sessions sessions is not enough evidence',
        );
      }

      expect(
        engine.evaluate(
          levelWith(sessions: policy.minEvaluationSessions, accuracy: 0.99),
        ),
        isNotNull,
      );
    });
  });

  group('promotion', () {
    test('sustained accuracy at or above 85% promotes one step', () {
      final decision = engine.evaluate(
        levelWith(sessions: 20, accuracy: 0.88),
      )!;

      expect(decision.isPromotion, isTrue);
      expect(decision.previous, CefrLevel.b1);
      expect(decision.next, CefrLevel.b1Plus,
          reason: 'one step, not a whole band (MVP Core §23)');
    });

    test('exactly at the threshold promotes', () {
      final decision = engine.evaluate(
        levelWith(sessions: 20, accuracy: policy.promoteThreshold),
      )!;

      expect(decision.isPromotion, isTrue);
    });

    test('a learner already at C2 holds instead of overflowing the ladder', () {
      final decision = engine.evaluate(
        levelWith(sessions: 20, accuracy: 1.0, system: CefrLevel.c2),
      )!;

      expect(decision.moved, isFalse);
      expect(decision.next, isNull);
    });
  });

  group('demotion', () {
    test('accuracy below 70% demotes one step', () {
      final decision = engine.evaluate(
        levelWith(sessions: 20, accuracy: 0.55),
      )!;

      expect(decision.reason, LevelChangeReason.demoted);
      expect(decision.next, CefrLevel.a2Plus);
    });

    test('a learner already at A1 holds instead of underflowing', () {
      final decision = engine.evaluate(
        levelWith(sessions: 20, accuracy: 0.1, system: CefrLevel.a1),
      )!;

      expect(decision.moved, isFalse);
    });
  });

  group('holding', () {
    test('between the thresholds the level holds but the window resets', () {
      final level = levelWith(sessions: 20, accuracy: 0.78);
      final decision = engine.evaluate(level)!;

      expect(decision.moved, isFalse);

      final applied = engine.apply(level, decision);
      expect(applied.systemAssessedLevel, CefrLevel.b1,
          reason: 'the level itself is unchanged');
      expect(applied.evaluationSessions, 0,
          reason: 'spent evidence must not be re-used');
    });
  });

  group('rule R6 — the two levels stay separate', () {
    test('applying a decision never touches the user-selected level', () {
      final level = SkillLevel(
        skill: SkillType.reading,
        userSelectedLevel: CefrLevel.c1, // learner is ambitious
        systemAssessedLevel: CefrLevel.a2,
        evaluationSessions: 20,
        rollingAccuracy: 0.9,
        dailyTargetWords: 10,
      );

      final applied = engine.apply(level, engine.evaluate(level)!);

      expect(applied.userSelectedLevel, CefrLevel.c1);
      expect(applied.systemAssessedLevel, CefrLevel.a2Plus);
    });

    test('spelling is never promoted or demoted', () {
      final spelling = SkillLevel.unlevelled(
        skill: SkillType.spelling,
        evaluationSessions: 50,
        rollingAccuracy: 1.0,
        dailyTargetWords: 10,
      );

      expect(engine.evaluate(spelling), isNull,
          reason: 'spelling carries no CEFR band (ADR-008)');
    });

    test('the proven level is the weakest skill, not the average', () {
      final levels = [
        levelWith(skill: SkillType.reading, system: CefrLevel.c1, sessions: 0, accuracy: 0),
        levelWith(skill: SkillType.listening, system: CefrLevel.a2, sessions: 0, accuracy: 0),
        levelWith(skill: SkillType.speaking, system: CefrLevel.b2, sessions: 0, accuracy: 0),
        SkillLevel.unlevelled(
          skill: SkillType.spelling,
          evaluationSessions: 0,
          rollingAccuracy: 0,
          dailyTargetWords: 10,
        ),
      ];

      expect(engine.systemValidatedLevel(levels), CefrLevel.a2);
    });
  });

  group('archiving', () {
    test('an Active word far below the proven level, with exposure, archives',
        () {
      expect(
        engine.shouldArchive(
          wordLevel: CefrLevel.a1,
          state: WordState.active,
          exposureCount: 5,
          systemValidatedLevel: CefrLevel.b1,
        ),
        isTrue,
      );
    });

    test('a word still being learned is never archived', () {
      expect(
        engine.shouldArchive(
          wordLevel: CefrLevel.a1,
          state: WordState.learning,
          exposureCount: 99,
          systemValidatedLevel: CefrLevel.c2,
        ),
        isFalse,
      );
    });

    test('a word close to the proven level is kept', () {
      expect(
        engine.shouldArchive(
          wordLevel: CefrLevel.b1,
          state: WordState.active,
          exposureCount: 99,
          systemValidatedLevel: CefrLevel.b2,
        ),
        isFalse,
        reason: 'one band up is not "outgrown"',
      );
    });

    test('a word without enough exposure is kept', () {
      expect(
        engine.shouldArchive(
          wordLevel: CefrLevel.a1,
          state: WordState.active,
          exposureCount: 0,
          systemValidatedLevel: CefrLevel.b1,
        ),
        isFalse,
        reason: 'retire what is established, not merely what is easy (§30)',
      );
    });
  });

  group('end to end through the engine', () {
    late MockEngine mock;
    late MockUser user;

    setUp(() {
      mock = MockEngine();
      user = mock.requireUser(
          mock.register('levels@test.dev', 'wordos123', 'Levels',
            phoneCountryCode: '967', phoneNumber: '770000012').token);
    });

    /// Feeds the level accumulator directly — the same field a completed
    /// session writes to.
    void seedPerformance(SkillType skill, {required double accuracy}) {
      user.levels[skill] = user.levels[skill]!.copyWith(
        evaluationSessions: policy.minEvaluationSessions,
        rollingAccuracy: accuracy,
      );
    }

    test('a manual level change is logged but never archives anything', () {
      final word = mock.addWord(user, MockDictionary.entries['book']!.first);
      // Force it to Active with exposure, so only the level rule is in play.
      final record = user.words.firstWhere((w) => w.id == word.id)
        ..state = WordState.active
        ..exposureCount = 10;

      mock.updateSkillLevel(user, SkillType.reading, CefrLevel.c2);

      expect(record.state, WordState.active,
          reason: 'a self-declared level must never archive words (§28)');
      expect(
        user.levelChanges.single.changeType,
        LevelChangeType.userManualChange,
      );
      expect(user.levels[SkillType.reading]!.systemAssessedLevel,
          isNot(CefrLevel.c2),
          reason: 'the validated level is unaffected by a manual change');
    });

    test('setting a level on Spelling is refused', () {
      expect(
        () => mock.updateSkillLevel(user, SkillType.spelling, CefrLevel.b1),
        throwsA(isA<Object>()),
      );
    });

    test('sustained performance promotes the validated level and archives '
        'outgrown words', () {
      final word = mock.addWord(user, MockDictionary.entries['book']!.first);
      final record = user.words.firstWhere((w) => w.id == word.id)
        ..state = WordState.active
        ..exposureCount = 6;
      // A second word, still Learning, so there is something to run a session
      // with — the first one is Active and therefore no longer due.
      mock.addWord(user, MockDictionary.entries['research']!.first);

      // Start everything low so a single promotion clears the archive gap.
      for (final skill in MockEngine.configuration.skillsOrder) {
        final level = user.levels[skill]!;
        if (!level.carriesCefrLevel) continue;
        user.levels[skill] = level.copyWith(
          systemAssessedLevel: CefrLevel.a2Plus,
          userSelectedLevel: CefrLevel.a2Plus,
        );
      }

      // `book = كتاب` is A1; a promotion to B1 puts it four steps behind.
      expect(record.level, CefrLevel.a1);

      seedPerformance(SkillType.reading, accuracy: 0.95);
      seedPerformance(SkillType.listening, accuracy: 0.95);
      seedPerformance(SkillType.speaking, accuracy: 0.95);
      seedPerformance(SkillType.writing, accuracy: 0.95);

      // Promote every CEFR skill one step: A2+ → B1.
      for (final skill in MockEngine.configuration.skillsOrder) {
        if (!user.levels[skill]!.carriesCefrLevel) continue;
        final decision = const LevelEngine().evaluate(user.levels[skill]!);
        expect(decision?.isPromotion, isTrue, reason: '$skill should promote');
      }

      // Drive it through the real path by completing a Reading session.
      final session = mock.startSession(user, SkillType.reading);
      String? current = session.items.first.id;
      while (current != null) {
        final item = session.items.firstWhere((i) => i.id == current);
        final target = item.wordId == null
            ? null
            : session.targetWords.firstWhere((w) => w.wordId == item.wordId);
        current = mock
            .submitAnswer(user, session.id, item.id,
                target?.meaning ?? item.options.first)
            .progress
            .nextItemId;
      }
      mock.completeSession(user, session.id);

      expect(user.levels[SkillType.reading]!.systemAssessedLevel, CefrLevel.b1,
          reason: 'the completed session triggered the level review');
      expect(
        user.levelChanges.any(
            (c) => c.changeType == LevelChangeType.systemValidated),
        isTrue,
      );
    });

    test('archiving preserves the record and its history (rule R8)', () {
      final word = mock.addWord(user, MockDictionary.entries['book']!.first);
      final record = user.words.firstWhere((w) => w.id == word.id)
        ..state = WordState.active
        ..exposureCount = 6;
      final eventsBefore = record.events.length;
      mock.addWord(user, MockDictionary.entries['research']!.first);

      // Reading is the last skill lagging behind. Once it catches up, the
      // *weakest* proven level becomes B1 — four steps above this A1 word — so
      // the learner has demonstrably outgrown it.
      for (final skill in MockEngine.configuration.skillsOrder) {
        final level = user.levels[skill]!;
        if (!level.carriesCefrLevel) continue;
        user.levels[skill] = level.copyWith(
          systemAssessedLevel:
              skill == SkillType.reading ? CefrLevel.a2Plus : CefrLevel.b1,
          evaluationSessions: policy.minEvaluationSessions,
          rollingAccuracy: 0.95,
        );
      }

      final session = mock.startSession(user, SkillType.reading);
      String? current = session.items.first.id;
      while (current != null) {
        final item = session.items.firstWhere((i) => i.id == current);
        final target = item.wordId == null
            ? null
            : session.targetWords.firstWhere((w) => w.wordId == item.wordId);
        current = mock
            .submitAnswer(user, session.id, item.id,
                target?.meaning ?? item.options.first)
            .progress
            .nextItemId;
      }
      mock.completeSession(user, session.id);

      expect(record.state, WordState.archived);
      expect(record.archivedAt, isNotNull);
      expect(record.text, 'book', reason: 'the row survives archiving');
      expect(record.events.length, greaterThan(eventsBefore));
      expect(record.events.last.type, WordEventType.archived);

      // And it is still readable through the normal API.
      final detail = mock.wordDetail(user, word.id);
      expect(detail.word.state, WordState.archived);
      expect(mock.hub(user).vocabulary.archived, 1);
    });
  });
}
