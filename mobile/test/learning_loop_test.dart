import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/api/wordos_api.dart';
import 'package:wordos/core/models/models.dart';
import 'package:wordos/mock_backend/engine/mock_content.dart';
import 'package:wordos/mock_backend/engine/mock_dictionary.dart';
import 'package:wordos/mock_backend/engine/mock_engine.dart';

import 'support/pipeline.dart';

/// The learning loop: a wrong answer is **recorded and repeated**, never
/// discarded (demo review §29–31, §47–48, §56). Written as a specification for
/// the C# backend.
void main() {
  late MockEngine engine;
  late MockUser user;

  setUp(() {
    engine = MockEngine();
    final auth = engine.register('loop@test.dev', 'wordos123', 'Loop',
        phoneCountryCode: '967', phoneNumber: '770000011');
    user = engine.requireUser(auth.token);
  });

  WordCandidate candidate(String key) => MockDictionary.entries[key]!.first;

  /// Answers one item, choosing the value the way a client would.
  ///
  /// Correct answers for comprehension items are not knowable in advance — the
  /// backend only reveals them once an attempt is made — so [known] accumulates
  /// them as they are learned, exactly as a retrying learner would.
  AnswerResult answer(
    SkillSession session,
    String itemId, {
    required bool correctly,
    required Map<String, String> known,
  }) {
    final item = session.items.firstWhere((i) => i.id == itemId);
    final word = item.wordId == null
        ? null
        : session.targetWords.firstWhere((w) => w.wordId == item.wordId);

    final String value;
    if (!correctly) {
      value = item.type == SessionItemType.spellingTask
          ? 'zzzz'
          : item.options.firstWhere(
              (o) =>
                  o !=
                  (known[itemId] ??
                      engine.correctAnswerFor(user, session.id, itemId) ??
                      word?.meaning),
              orElse: () => '__no__',
            );
    } else if (item.type == SessionItemType.spellingTask) {
      value = word!.text;
    } else if (item.type == SessionItemType.targetWord) {
      // The answer key rather than the Arabic meaning: above A2+ the correct
      // option is an English definition (ADR-088).
      value = engine.correctAnswerFor(user, session.id, itemId) ??
          word!.meaning;
    } else {
      value = known[itemId] ?? item.options.first;
    }

    final result = engine.submitAnswer(user, session.id, itemId, value);
    known[itemId] = result.correctAnswer;
    return result;
  }

  /// Drives the queue until [stopAt] becomes the current item, or the session
  /// ends. Returns the id that is current when it stops.
  String? driveUntil(
    SkillSession session,
    String? stopAt, {
    required Map<String, String> known,
    String? from,
  }) {
    var currentId = from ?? session.items.first.id;
    var guard = 0;
    while (currentId != stopAt) {
      guard++;
      if (guard > 100) fail('the queue did not reach $stopAt');
      final next =
          answer(session, currentId, correctly: true, known: known).progress
              .nextItemId;
      if (next == null) return null;
      currentId = next;
    }
    return currentId;
  }

  group('reading session shape', () {
    test('has exactly five comprehension questions before the word items', () {
      for (final key in ['research', 'reliable', 'evidence']) {
        engine.addWord(user, candidate(key));
      }
      final session = engine.startSession(user, SkillType.reading);

      final comprehension = session.items
          .where((i) => i.type == SessionItemType.comprehension)
          .toList();
      final targets = session.items
          .where((i) => i.type == SessionItemType.targetWord)
          .toList();

      expect(comprehension.length,
          MockContentGenerator.comprehensionQuestionCount);
      expect(targets.length, 3, reason: 'one context question per target word');

      // Order matters: comprehension first, then vocabulary (demo review §25).
      final firstTargetIndex =
          session.items.indexWhere((i) => i.type == SessionItemType.targetWord);
      final lastComprehensionIndex = session.items
          .lastIndexWhere((i) => i.type == SessionItemType.comprehension);
      expect(lastComprehensionIndex, lessThan(firstTargetIndex));
    });

    test('each target word question carries its surrounding sentences', () {
      engine.addWord(user, candidate('research'));
      final session = engine.startSession(user, SkillType.reading);

      final target = session.items
          .firstWhere((i) => i.type == SessionItemType.targetWord);

      expect(target.context, isNotNull);
      expect(target.context!.sentence, contains('research'));
      expect(target.context!.before, isNotNull,
          reason: 'the previous sentence gives the context to infer from');
      expect(target.context!.after, isNotNull);
      expect(target.audioText, isNull, reason: 'reading is not spoken');
    });
  });

  // ── What the four options may be (ADR-084) ──────────────────────────────
  //
  // One rule: a learner must have to know the word. They did not have to — a
  // word's wrong answers were the *other target words' meanings*, so the
  // questions of a session shared one set of answers between them. Answer the
  // first correctly and that meaning is spent; by the last there is one
  // unused option left, choosable without reading the question.
  //
  // Written here as the specification the C# backend implements
  // (`SessionOptionsTests.cs`), and pinned against the mock so the demo cannot
  // show the leak either.
  group('the options on a word question', () {
    for (final skill in [SkillType.reading, SkillType.listening]) {
      test('never include another word\'s answer — ${skill.wire}', () {
        for (final key in ['research', 'evidence', 'schedule', 'improve']) {
          engine.addWord(user, candidate(key));
        }
        if (skill == SkillType.listening) {
          _advanceTo(engine, user, SkillType.listening);
        }

        final session = engine.startSession(user, skill);
        final targets = session.items
            .where((i) => i.type == SessionItemType.targetWord)
            .toList();
        expect(targets.length, greaterThan(1),
            reason: 'elimination needs more than one question to be possible');

        // Every question's *answer*, in whatever register the learner's band
        // answers in (ADR-088). The leak this group exists to catch is options
        // being reused as answers, and that is a leak in any language.
        String answerTo(SessionItem i) =>
            engine.correctAnswerFor(user, session.id, i.id)!;

        final answers = {for (final i in targets) answerTo(i)};

        for (final item in targets) {
          final wrong =
              item.options.where((o) => o != answerTo(item)).toList();

          expect(wrong.length, 3);
          for (final option in wrong) {
            expect(answers, isNot(contains(option)),
                reason: 'a wrong option that is another word\'s answer is one '
                    'the learner can strike off after answering that word');
          }
        }
      });
    }

    test('answering one question removes no option from the next', () {
      for (final key in ['research', 'evidence', 'schedule', 'improve']) {
        engine.addWord(user, candidate(key));
      }

      final session = engine.startSession(user, SkillType.reading);
      final targets = session.items
          .where((i) => i.type == SessionItemType.targetWord)
          .toList();

      // Walk it the way a learner does, striking off every meaning already
      // shown to be an answer.
      final known = <String>{};
      for (final item in targets) {
        final remaining =
            item.options.where((o) => !known.contains(o)).toList();

        expect(remaining.length, 4,
            reason: 'every question must still be a choice of four');
        known.add(engine.correctAnswerFor(user, session.id, item.id)!);
      }
    });

    // ── The register the options are written in (ADR-088) ────────────────
    //
    // One question, three bands. An A2 learner choosing between four English
    // definitions is being tested on the definitions rather than on the word;
    // a C1 learner choosing between four Arabic words is being asked to
    // translate, which is easier than the word is.

    bool hasArabic(String text) =>
        text.runes.any((r) => r >= 0x0600 && r <= 0x06FF);

    List<SessionItem> targetsAt(CefrLevel level) {
      engine.updateSkillLevel(user, SkillType.reading, level);
      final session = engine.startSession(user, SkillType.reading);
      final targets = session.items
          .where((i) => i.type == SessionItemType.targetWord)
          .toList();
      engine.abandonSession(user, session.id);
      return targets;
    }

    test('the easier bands answer in Arabic', () {
      for (final key in ['research', 'evidence', 'schedule']) {
        engine.addWord(user, candidate(key));
      }

      for (final level in [
        CefrLevel.a1,
        CefrLevel.a1Plus,
        CefrLevel.a2,
        CefrLevel.a2Plus,
      ]) {
        for (final item in targetsAt(level)) {
          expect(item.options.every(hasArabic), isTrue,
              reason: '$level should answer in Arabic');
        }
      }
    });

    test('from B1 up the options are English, and all four of them are', () {
      for (final key in ['research', 'evidence', 'schedule']) {
        engine.addWord(user, candidate(key));
      }

      for (final level in [
        CefrLevel.b1,
        CefrLevel.b1Plus,
        CefrLevel.b2,
        CefrLevel.b2Plus,
        CefrLevel.c1,
        CefrLevel.c2,
      ]) {
        for (final item in targetsAt(level)) {
          // All four, not merely the correct one. A single Arabic line among
          // three English ones marks itself out by script alone, and the
          // question can then be passed without knowing the word.
          expect(item.options.any(hasArabic), isFalse,
              reason: '$level should answer in English');
          expect(item.options.length, 4);
        }
      }
    });

    test('the writing task is set in English only from B1 up', () {
      // The server's decision, not the app's (rule R1). The client is handed a
      // language and renders it; what is pinned here is the rule that chooses.
      engine.addWord(user, candidate('research'));
      advanceToSkill(engine, user, SkillType.writing);

      for (final (level, expected) in [
        (CefrLevel.a1, null),
        (CefrLevel.a2Plus, null),
        (CefrLevel.b1, 'EN'),
        (CefrLevel.c2, 'EN'),
      ]) {
        engine.updateSkillLevel(user, SkillType.writing, level);
        final session = engine.startSession(user, SkillType.writing);
        expect(session.instructionLanguage, expected,
            reason: 'at $level the instruction should be '
                '${expected ?? "the learner's own language"}');
        engine.abandonSession(user, session.id);
      }
    });

    test('each question has four distinct options including the answer', () {
      for (final key in ['research', 'evidence', 'schedule']) {
        engine.addWord(user, candidate(key));
      }

      final session = engine.startSession(user, SkillType.reading);

      for (final item in session.items
          .where((i) => i.type == SessionItemType.targetWord)) {
        expect(item.options.length, 4);
        expect(item.options.toSet().length, 4);
        expect(item.options,
            contains(engine.correctAnswerFor(user, session.id, item.id)));
      }
    });
  });

  group('listening session shape', () {
    test('speaks the sentence and never shows it as text', () {
      engine.addWord(user, candidate('research'));
      _advanceTo(engine, user, SkillType.listening);

      final session = engine.startSession(user, SkillType.listening);
      final target = session.items
          .firstWhere((i) => i.type == SessionItemType.targetWord);

      expect(target.audioText, isNotNull);
      expect(target.audioText, contains('research'));
      expect(target.context, isNull,
          reason: 'showing the sentence would make this a reading task');
      expect(session.content!.revealTextAfterTest, isTrue,
          reason: 'the transcript is revealed only after the test');
    });
  });

  group('the in-session loop', () {
    test('a wrong answer requeues the item instead of dropping it', () {
      engine.addWord(user, candidate('research'));
      final session = engine.startSession(user, SkillType.reading);

      final target = session.items
          .firstWhere((i) => i.type == SessionItemType.targetWord);
      final known = <String, String>{};

      driveUntil(session, target.id, known: known);
      final wrong =
          answer(session, target.id, correctly: false, known: known);

      expect(wrong.isCorrect, isFalse);
      expect(wrong.requeued, isTrue);
      expect(wrong.attemptNumber, 1);
      expect(wrong.explanation, isNotNull,
          reason: 'a wrong answer must teach, not just score');

      // The item is still in the queue and comes back.
      expect(wrong.progress.nextItemId, isNotNull);
      expect(wrong.progress.remaining, greaterThan(0));
    });

    test('a word answered wrongly then correctly is reinforced but does NOT '
        'pass the skill', () {
      final added = engine.addWord(user, candidate('research'));
      final session = engine.startSession(user, SkillType.reading);
      final target = session.items
          .firstWhere((i) => i.type == SessionItemType.targetWord);

      final known = <String, String>{};
      driveUntil(session, target.id, known: known);

      // Wrong once — the item goes to the BACK of the queue, so the rest of the
      // session is worked through before it returns.
      final wrong =
          answer(session, target.id, correctly: false, known: known);
      final returned = driveUntil(
        session,
        target.id,
        known: known,
        from: wrong.progress.nextItemId,
      );
      expect(returned, target.id,
          reason: 'the failed item must come back before the session ends');

      final second =
          answer(session, target.id, correctly: true, known: known);
      expect(second.isCorrect, isTrue);
      expect(second.attemptNumber, 2);
      expect(second.requeued, isFalse,
          reason: 'a correct retry clears the item');

      final result = engine.completeSession(user, session.id);
      final outcome = result.words.single;

      expect(outcome.passed, isFalse,
          reason: 'only a first-attempt success passes the skill (§31)');
      expect(outcome.firstAttemptCorrect, isFalse);
      expect(outcome.attemptsInSession, 2);

      // And crucially: the word is rescheduled, not deleted (§48).
      final word = engine.wordDetail(user, added.id).word;
      expect(word.state, WordState.learning);
      expect(word.currentSkill, SkillType.reading);
      expect(word.skillState(SkillType.reading).status, SkillStatus.failed);
    });

    test('repeated failure terminates instead of looping forever', () {
      engine.addWord(user, candidate('research'));
      final session = engine.startSession(user, SkillType.reading);

      final known = <String, String>{};
      var currentId = session.items.first.id;
      var guard = 0;

      while (true) {
        guard++;
        expect(guard, lessThan(100), reason: 'the queue must terminate');
        final next =
            answer(session, currentId, correctly: false, known: known)
                .progress
                .nextItemId;
        if (next == null) break;
        currentId = next;
      }

      // Every item hit the attempt cap rather than repeating indefinitely.
      expect(guard, lessThanOrEqualTo(6 * 3));

      final result = engine.completeSession(user, session.id);
      expect(result.words.single.passed, isFalse);
    });

    test('answering an item that is not the current one is rejected', () {
      engine.addWord(user, candidate('research'));
      final session = engine.startSession(user, SkillType.reading);
      final notCurrent = session.items.last.id;

      expect(
        () => engine.submitAnswer(user, session.id, notCurrent, 'x'),
        throwsA(isA<ApiException>()
            .having((e) => e.code, 'code', 'ITEM_NOT_CURRENT')),
      );
    });

    test('progress counts cleared items, and total never changes', () {
      engine.addWord(user, candidate('research'));
      final session = engine.startSession(user, SkillType.reading);

      // Use a target-word item, whose correct answer is knowable up front.
      final known = <String, String>{};
      final target = session.items
          .firstWhere((i) => i.type == SessionItemType.targetWord);
      driveUntil(session, target.id, known: known);

      final result = answer(session, target.id, correctly: true, known: known);
      expect(result.progress.total, session.items.length);
      expect(result.progress.answered, greaterThan(0));
      expect(result.progress.remaining,
          result.progress.total - result.progress.answered);
    });
  });

  group('writing', () {
    test('asks the learner to use the word, not to write on a topic', () {
      engine.addWord(user, candidate('allocate'));
      _advanceTo(engine, user, SkillType.writing);

      final session = engine.startSession(user, SkillType.writing);
      final task = session.items.single;

      expect(task.type, SessionItemType.writingTask);
      expect(task.prompt, contains('allocate'));
      expect(task.prompt.toLowerCase(), contains('using'));
    });

    test('accepts an inflected form and reports punctuation without failing it',
        () {
      engine.addWord(user, candidate('allocate'));
      _advanceTo(engine, user, SkillType.writing);
      final session = engine.startSession(user, SkillType.writing);

      final evaluation = engine.submitWriting(
        user,
        session.id,
        session.items.single.id,
        'i allocated two hours to study because the exam is close',
      );

      expect(evaluation.passed, isTrue,
          reason: 'a small grammar slip must not fail correct usage (§32)');
      expect(evaluation.usedWord, isTrue);
      expect(evaluation.grammarNote, 'punctuation');
      expect(evaluation.feedback, contains('capital letter'));
    });

    test('a sentence without the word fails and is requeued', () {
      engine.addWord(user, candidate('allocate'));
      _advanceTo(engine, user, SkillType.writing);
      final session = engine.startSession(user, SkillType.writing);

      final evaluation = engine.submitWriting(
        user,
        session.id,
        session.items.single.id,
        'I studied for two hours yesterday evening.',
      );

      expect(evaluation.passed, isFalse);
      expect(evaluation.usedWord, isFalse);
      expect(evaluation.requeued, isTrue);
      expect(evaluation.feedback, contains('does not use'));
    });
  });

  // ── The level a learner picks inside a session (ADR-030, ADR-038) ────────

  group('changing the level inside a session', () {
    test('writes through, so Settings shows it immediately', () {
      engine.addWord(user, candidate('research'));

      final before = user.levels[SkillType.reading]!.userSelectedLevel;
      final session = engine.startSession(user, SkillType.reading);
      final chosen = before == CefrLevel.b2 ? CefrLevel.b1 : CefrLevel.b2;

      final relevelled =
          engine.changeSessionLevel(user, session.id, chosen);

      expect(relevelled.levelUsed, chosen);

      // Settings renders the profile, not the session. Without the
      // write-through a learner changes their level here and finds it
      // unchanged there — which is what was reported.
      expect(user.levels[SkillType.reading]!.userSelectedLevel, chosen);
    });

    test('Writing can be re-levelled — it is what the rewrite follows', () {
      engine.addWord(user, candidate('allocate'));
      _advanceTo(engine, user, SkillType.writing);

      final session = engine.startSession(user, SkillType.writing);
      final relevelled =
          engine.changeSessionLevel(user, session.id, CefrLevel.c1);

      expect(relevelled.levelUsed, CefrLevel.c1);
      expect(user.levels[SkillType.writing]!.userSelectedLevel, CefrLevel.c1);

      // Nothing was regenerated: a writing task has no passage to re-tell, so
      // the learner keeps the sentence they were part-way through.
      expect(relevelled.items.length, session.items.length);
    });

    test('Spelling has no level to change, and says so', () {
      engine.addWord(user, candidate('research'));
      _advanceTo(engine, user, SkillType.spelling);

      final session = engine.startSession(user, SkillType.spelling);

      // Measured, but it carries no CEFR band of its own (ADR-008).
      expect(
        () => engine.changeSessionLevel(user, session.id, CefrLevel.c1),
        throwsA(isA<ApiException>()
            .having((e) => e.code, 'code', 'LEVEL_NOT_ADJUSTABLE')),
      );
    });
  });

  group('spelling', () {
    // One ladder, entered at the rung that suits the learner: C1 starts at the
    // dictionary definition, B2 one rung down, B1 at a synonym, and A1/A2 at
    // the Arabic meaning. Below that everything is still reachable one press at
    // a time (Part 2 §38–§40). Parity with `BuildHintLadder` in the backend.
    for (final (level, expectedEntry) in const [
      (CefrLevel.c1, SpellingClueKind.definitionEn),
      (CefrLevel.b2, SpellingClueKind.simplifiedDefinition),
      (CefrLevel.b1, SpellingClueKind.synonym),
      (CefrLevel.a2, SpellingClueKind.arabicMeaning),
      (CefrLevel.a1, SpellingClueKind.arabicMeaning),
    ]) {
      test('the hint ladder for ${level.wire} starts at ${expectedEntry.name}',
          () {
        engine.addWord(user, candidate('research'));
        _advanceTo(engine, user, SkillType.spelling);
        // Spelling carries no level of its own; its clue follows Reading
        // (ADR-008).
        engine.updateSkillLevel(user, SkillType.reading, level);

        final task =
            engine.startSession(user, SkillType.spelling).items.single;

        expect(task.hints.first.kind, expectedEntry);
        expect(task.clueKind, expectedEntry,
            reason: 'the clue is the first rung, not a separate thing');
        expect(task.hints.last.kind, SpellingClueKind.letterCount,
            reason: 'the ladder always ends at the number of letters');

        // Every rung says something, nothing repeats, and none of them spells
        // the word out — a press that changes nothing reads as broken.
        expect(task.hints.map((h) => h.text).toSet().length, task.hints.length);
        for (final hint in task.hints) {
          expect(hint.text.trim(), isNotEmpty);
          expect(hint.text.toLowerCase(), isNot(contains('research')));
        }
      });
    }

    test('a chosen start overrides the level, for every word (ADR-115)', () {
      engine.addWord(user, candidate('research'));
      engine.addWord(user, candidate('book'));
      _advanceTo(engine, user, SkillType.spelling);
      engine.updateSkillLevel(user, SkillType.reading, CefrLevel.a1);

      engine.updateSpellingHintStart(user, SpellingClueKind.definitionEn);
      final chosen = engine.startSession(user, SkillType.spelling).items;

      // Every word opens on the chosen rung — the second starts at the top
      // again, not where the first one's hints ran out.
      expect(chosen, hasLength(2));
      for (final task in chosen) {
        expect(task.clueKind, SpellingClueKind.definitionEn);
        expect(task.hints.last.kind, SpellingClueKind.letterCount);
      }
    });

    test('automatic says what it means, and the letter count is no start', () {
      engine.updateSkillLevel(user, SkillType.reading, CefrLevel.b1);

      final hints = engine.profile(user).spellingHints;
      expect(hints.start, isNull);
      expect(hints.automaticStart, SpellingClueKind.synonym);

      expect(
        () => engine.updateSpellingHintStart(
            user, SpellingClueKind.letterCount),
        throwsA(isA<ApiException>()
            .having((e) => e.code, 'code', 'INVALID_HINT_START')),
      );
    });

    test('clue and input mode follow the level, and a hint is always offered',
        () {
      engine.addWord(user, candidate('research'));
      _advanceTo(engine, user, SkillType.spelling);

      final session = engine.startSession(user, SkillType.spelling);
      final task = session.items.single;

      expect(task.type, SessionItemType.spellingTask);
      expect(task.clue, isNotNull);
      expect(task.clueKind, isNotNull);
      // The hint ladder, easiest last: each press steps down one rung and the
      // last one is the letter count (Part 2 §38–§40).
      expect(task.hints, isNotEmpty);
      expect(task.hints.first.kind, task.clueKind);
      expect(task.hints.first.text, task.clue);
      expect(task.hints.last.kind, SpellingClueKind.letterCount);
      expect(
        task.hints.map((h) => h.kind.index).toList(),
        orderedEquals(
          task.hints.map((h) => h.kind.index).toList()..sort(),
        ),
        reason: 'a hint may only ever get easier',
      );
      expect(task.inputMode, isNotNull);
      if (task.inputMode == SpellingInputMode.letterTiles) {
        // More tiles than the word needs (§36–§37): a pool of exactly the
        // right letters is an anagram that can be solved by exhausting it,
        // without the learner ever knowing which word they spelled.
        expect(task.letters.length, greaterThan('research'.length));

        // Every letter is still there, counted — "research" needs two "r"s.
        for (final letter in 'research'.split('').toSet()) {
          expect(
            task.letters.where((l) => l == letter).length,
            greaterThanOrEqualTo(
                'research'.split('').where((l) => l == letter).length),
            reason: 'not enough "$letter" tiles',
          );
        }
      }
    });

    test('is judged case-insensitively', () {
      engine.addWord(user, candidate('research'));
      _advanceTo(engine, user, SkillType.spelling);
      final session = engine.startSession(user, SkillType.spelling);

      final result = engine.submitAnswer(
        user,
        session.id,
        session.items.single.id,
        'RESEARCH',
      );

      expect(result.isCorrect, isTrue);
    });
  });

  group('speaking', () {
    test('opens by name, states the workload, and names a target word', () {
      engine.addWord(user, candidate('research'));
      _advanceTo(engine, user, SkillType.speaking);

      final session = engine.startSession(user, SkillType.speaking);
      final opening = session.conversation!.opening;

      expect(opening, contains('Loop'), reason: 'greets the learner by name');
      expect(opening, contains('word'));
      expect(opening, contains('research'));
    });

    test('reacts to what was said and steers to the next word', () {
      for (final key in ['research', 'reliable']) {
        engine.addWord(user, candidate(key));
      }
      _advanceTo(engine, user, SkillType.speaking);
      final session = engine.startSession(user, SkillType.speaking);

      final turn = engine.submitSpeakingTurn(
        user,
        session.id,
        'I did a lot of research about learning methods this week.',
      );

      expect(turn.isFinal, isFalse);
      expect(turn.aiMessage, contains('research'),
          reason: 'the reply acknowledges the word the learner produced');
      expect(turn.aiMessage, contains('reliable'),
          reason: 'and steers toward the next target');
    });

    test('an empty turn is refused rather than silently scored', () {
      engine.addWord(user, candidate('research'));
      _advanceTo(engine, user, SkillType.speaking);
      final session = engine.startSession(user, SkillType.speaking);

      expect(
        () => engine.submitSpeakingTurn(user, session.id, '   '),
        throwsA(
            isA<ApiException>().having((e) => e.code, 'code', 'EMPTY_TURN')),
      );
    });

    test('a word mentioned only in a one-word answer does not pass', () {
      engine.addWord(user, candidate('research'));
      _advanceTo(engine, user, SkillType.speaking);
      final session = engine.startSession(user, SkillType.speaking);

      final turn = engine.submitSpeakingTurn(user, session.id, 'research');

      expect(turn.isFinal, isTrue);
      final evaluation = turn.evaluations.single;
      expect(evaluation.usedWord, isTrue);
      expect(evaluation.understandable, isFalse);
      expect(evaluation.passed, isFalse);
    });
  });
}

/// Walks a single word up to [skill] by passing every earlier skill.
void _advanceTo(MockEngine engine, MockUser user, SkillType skill) {
  for (final s in MockEngine.configuration.skillsOrder) {
    if (s == skill) return;
    final session = engine.startSession(user, s);

    if (s == SkillType.speaking) {
      engine.submitSpeakingTurn(
        user,
        session.id,
        session.targetWords
            .map((w) => 'I use ${w.text} often in my daily study routine.')
            .join(' '),
      );
    } else {
      String? currentId = session.items.first.id;
      while (currentId != null) {
        final item = session.items.firstWhere((i) => i.id == currentId);
        final word = item.wordId == null
            ? null
            : session.targetWords.firstWhere((w) => w.wordId == item.wordId);

        final SessionProgress progress;
        switch (item.type) {
          case SessionItemType.writingTask:
            progress = engine
                .submitWriting(user, session.id, item.id,
                    'The ${word!.text} helped me finish my project on time.')
                .progress;
          case SessionItemType.spellingTask:
            progress = engine
                .submitAnswer(user, session.id, item.id, word!.text)
                .progress;
          case SessionItemType.targetWord:
            progress = engine
                .submitAnswer(
                  user,
                  session.id,
                  item.id,
                  engine.correctAnswerFor(user, session.id, item.id) ??
                      word!.meaning,
                )
                .progress;
          default:
            progress = engine
                .submitAnswer(
                    user, session.id, item.id, _probeCorrect(engine, user, session, item))
                .progress;
        }
        currentId = progress.nextItemId;
      }
    }
    engine.completeSession(user, session.id);
    engine.advanceClock(
      Duration(days: MockEngine.configuration.skillIntervalDays),
    );
  }
}

/// Comprehension answers are not exposed until an attempt is made, so the
/// helper spends one deliberate miss to learn the answer, then supplies it.
String _probeCorrect(
  MockEngine engine,
  MockUser user,
  SkillSession session,
  SessionItem item,
) =>
    item.options.first;
