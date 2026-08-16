import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/models/models.dart';
import 'package:wordos/mock_backend/engine/mock_dictionary.dart';
import 'package:wordos/mock_backend/engine/mock_engine.dart';

/// These tests pin the WordOS learning rules.
///
/// They currently run against the mock engine, but they are written as a
/// specification: when the C# backend lands in Phase 5, the same scenarios must
/// hold there (see `docs/01-PHASES.md`, Phase 5 acceptance).
void main() {
  late MockEngine engine;
  late MockUser user;

  const interval = 2; // MockEngine.configuration.skillIntervalDays

  setUp(() {
    engine = MockEngine();
    final auth = engine.register('learner@test.dev', 'wordos123', 'Learner');
    user = engine.requireUser(auth.token);
  });

  WordCandidate candidate(String key) => MockDictionary.entries[key]!.first;

  /// Drives a session exactly the way the client does: follow the queue the
  /// server dictates, answering the *current* item each time. Words in
  /// [failWordIds] are answered wrongly on every attempt.
  SessionResult playSession(
    SkillType skill, {
    Set<String> failWordIds = const {},
  }) {
    final session = engine.startSession(user, skill);

    if (skill == SkillType.speaking) {
      final spoken = session.targetWords
          .where((w) => !failWordIds.contains(w.wordId))
          .map((w) => 'I think the ${w.text} is really useful in my studies.')
          .join(' ');
      engine.submitSpeakingTurn(
          user, session.id, spoken.isEmpty ? 'nothing at all today' : spoken);
      return engine.completeSession(user, session.id);
    }

    String? currentId = session.items.isEmpty ? null : session.items.first.id;
    var guard = 0;

    while (currentId != null) {
      guard++;
      if (guard > 200) {
        fail('the session queue did not terminate');
      }

      final item = session.items.firstWhere((i) => i.id == currentId);
      final wordId = item.wordId;
      final word = wordId == null
          ? null
          : session.targetWords.firstWhere((w) => w.wordId == wordId);
      final shouldFail = wordId != null && failWordIds.contains(wordId);

      final SessionProgress progress;
      switch (item.type) {
        case SessionItemType.comprehension:
          // Comprehension does not decide pass/fail, but it is part of the
          // queue and must be answered.
          progress = engine
              .submitAnswer(user, session.id, item.id, item.options.first)
              .progress;
        case SessionItemType.targetWord:
          final wrong = item.options.firstWhere((o) => o != word!.meaning);
          progress = engine
              .submitAnswer(
                user,
                session.id,
                item.id,
                shouldFail ? wrong : word!.meaning,
              )
              .progress;
        case SessionItemType.spellingTask:
          progress = engine
              .submitAnswer(
                user,
                session.id,
                item.id,
                shouldFail ? 'zzz' : word!.text,
              )
              .progress;
        case SessionItemType.writingTask:
          progress = engine
              .submitWriting(
                user,
                session.id,
                item.id,
                shouldFail
                    ? 'no'
                    : 'The ${word!.text} helped me finish my project on time.',
              )
              .progress;
        default:
          progress = engine
              .submitAnswer(user, session.id, item.id, item.options.first)
              .progress;
      }
      currentId = progress.nextItemId;
    }

    return engine.completeSession(user, session.id);
  }

  Word reload(String id) => engine.wordDetail(user, id).word;

  test('a new word enters the pipeline at the first skill only', () {
    final word = engine.addWord(user, candidate('research'));

    expect(word.state, WordState.learning);
    expect(word.currentSkill, SkillType.reading);
    expect(word.skillState(SkillType.reading).status, SkillStatus.available);
    for (final skill in [
      SkillType.listening,
      SkillType.speaking,
      SkillType.writing,
      SkillType.spelling,
    ]) {
      expect(word.skillState(skill).status, SkillStatus.pending,
          reason: '$skill must not be open before its turn');
    }
  });

  test('passing a skill schedules the next one after the configured gap', () {
    final added = engine.addWord(user, candidate('research'));
    final result = playSession(SkillType.reading);

    expect(result.words.single.passed, isTrue);
    expect(result.words.single.nextSkill, SkillType.listening);

    final word = reload(added.id);
    expect(word.skillState(SkillType.reading).status, SkillStatus.passed);
    expect(word.currentSkill, SkillType.listening);

    // Not eligible before the gap elapses…
    expect(
      () => engine.startSession(user, SkillType.listening),
      throwsA(isA<Object>()),
    );

    // …and eligible once it does.
    engine.advanceClock(const Duration(days: interval));
    expect(reload(added.id).skillState(SkillType.listening).status,
        SkillStatus.available);
    expect(engine.startSession(user, SkillType.listening).targetWords.length, 1);
  });

  test('a missed day never loses the word — it simply stays due', () {
    final added = engine.addWord(user, candidate('research'));
    playSession(SkillType.reading);

    engine.advanceClock(const Duration(days: 9));

    final hub = engine.hub(user);
    final listening =
        hub.skills.firstWhere((c) => c.skill == SkillType.listening);
    expect(listening.availability, SkillAvailability.available);
    expect(listening.dueWordCount, 1);
    expect(reload(added.id).state, WordState.learning);
  });

  test('failing one skill keeps the skills already passed', () {
    final added = engine.addWord(user, candidate('research'));
    playSession(SkillType.reading);
    engine.advanceClock(const Duration(days: interval));

    playSession(SkillType.listening, failWordIds: {added.id});

    final word = reload(added.id);
    expect(word.skillState(SkillType.reading).status, SkillStatus.passed,
        reason: 'demonstrated learning must never be thrown away');
    expect(word.skillState(SkillType.listening).status, SkillStatus.failed);
    expect(word.currentSkill, SkillType.listening,
        reason: 'only the failed skill is retried');
    expect(word.skillState(SkillType.speaking).status, SkillStatus.pending);
  });

  test('all five skills passed makes the word Active', () {
    final added = engine.addWord(user, candidate('research'));

    for (final skill in MockEngine.configuration.skillsOrder) {
      final result = playSession(skill);
      expect(result.words.single.passed, isTrue, reason: 'failed at $skill');
      engine.advanceClock(const Duration(days: interval));
    }

    final word = reload(added.id);
    expect(word.state, WordState.active);
    expect(word.currentSkill, isNull);
    expect(word.skills.every((s) => s.status == SkillStatus.passed), isTrue);
    expect(engine.hub(user).vocabulary.active, 1);
  });

  test('a session never exceeds the per-skill daily target', () {
    // Eight *distinct* words: the same word with the same meaning cannot be
    // added twice (see vocabulary_test.dart).
    for (final key in [
      'research',
      'reliable',
      'evidence',
      'achieve',
      'allocate',
      'estimate',
      'schedule',
      'improve',
    ]) {
      engine.addWord(user, candidate(key));
    }
    engine.updateDailyTarget(user, SkillType.reading, 5);

    final session = engine.startSession(user, SkillType.reading);
    expect(session.targetWords.length, 5);
  });

  test('daily target is clamped to the configured range', () {
    expect(engine.updateDailyTarget(user, SkillType.reading, 99).dailyTargetWords,
        MockEngine.configuration.maxDailyTarget);
    expect(engine.updateDailyTarget(user, SkillType.reading, 1).dailyTargetWords,
        MockEngine.configuration.minDailyTarget);
  });

  test('a manual level change never moves the system-validated level', () {
    final updated =
        engine.updateSkillLevel(user, SkillType.reading, CefrLevel.c1);

    expect(updated.userSelectedLevel, CefrLevel.c1);
    expect(updated.systemAssessedLevel, isNot(CefrLevel.c1),
        reason: 'archiving and progression must not follow a manual choice');
  });

  test('weekly review requeues wrong answers and leaves the pipeline alone', () {
    final a = engine.addWord(user, candidate('research'));
    engine.addWord(user, candidate('reliable'));
    playSession(SkillType.reading);
    final before = reload(a.id);

    final review = engine.startWeeklyReview(user);
    expect(review.totalWords, 2);

    // Answer the first item wrongly, then correctly; the second correctly.
    final first = review.queue.first;
    final wrongOption = first.options.first == 'x' ? first.options[1] : 'x';
    final wrong =
        engine.answerWeeklyReview(user, review.id, first.id, wrongOption);
    expect(wrong.isCorrect, isFalse);
    expect(wrong.requeued, isTrue);
    expect(wrong.remaining, 2, reason: 'the word came back into the queue');

    var next = wrong.nextItem!;
    while (true) {
      final word = engine
          .words(user, null)
          .items
          .firstWhere((w) => w.id == next.wordId);
      final result =
          engine.answerWeeklyReview(user, review.id, next.id, word.meaning);
      expect(result.isCorrect, isTrue);
      if (result.nextItem == null) break;
      next = result.nextItem!;
    }

    final summary = engine.completeWeeklyReview(user, review.id);
    expect(summary.totalWords, 2);
    expect(summary.firstPassCorrect, 1, reason: 'one word was wrong first time');
    expect(summary.weeklyScore, 0.5);

    final after = reload(a.id);
    expect(after.state, before.state);
    expect(after.currentSkill, before.currentSkill);
    for (final skill in MockEngine.configuration.skillsOrder) {
      expect(after.skillState(skill).status, before.skillState(skill).status,
          reason: 'weekly review must not touch $skill');
    }
  });
}
