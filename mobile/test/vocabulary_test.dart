import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/api/wordos_api.dart';
import 'package:wordos/core/models/models.dart';
import 'package:wordos/mock_backend/engine/mock_engine.dart';

/// The vocabulary cases the product owner listed in the demo review (§55.1),
/// written as a specification for the C# backend.
void main() {
  late MockEngine engine;
  late MockUser user;

  setUp(() {
    engine = MockEngine();
    user = engine.requireUser(engine.login('demo@wordos.app', 'wordos123').token);
  });

  WordCandidate candidate(String text, {int meaning = 0}) =>
      engine.lookup(text).where((c) => !c.isSpellingSuggestion).toList()[meaning];

  group('lookup', () {
    test('a known word returns its meanings', () {
      final results = engine.lookup('book');

      expect(results, isNotEmpty);
      expect(results.every((c) => !c.isSpellingSuggestion), isTrue);
      expect(results.map((c) => c.meaning), containsAll(['كتاب', 'يحجز']));
    });

    test('a word with several meanings offers each one separately', () {
      final results = engine.lookup('book');

      expect(results.length, greaterThan(1));
      // Each candidate carries its own definition and part of speech, so the
      // learner is choosing a *sense*, not just a translation.
      expect(results.map((c) => c.partOfSpeech).toSet().length, greaterThan(1));
    });

    test('a non-word returns no real candidates, only spelling help', () {
      final results = engine.lookup('hch');

      expect(
        results.where((c) => !c.isSpellingSuggestion),
        isEmpty,
        reason: 'the lexicon must not invent an entry for a non-word',
      );
    });

    test('a near-miss offers the correct spelling', () {
      final results = engine.lookup('sofware');

      expect(results, isNotEmpty);
      expect(results.every((c) => c.isSpellingSuggestion), isTrue);
      expect(results.map((c) => c.text), contains('software'));
    });

    test('an empty query returns nothing rather than everything', () {
      expect(engine.lookup(''), isEmpty);
      expect(engine.lookup('   '), isEmpty);
    });

    test('a prefix returns every matching sense with word, level and meaning',
        () {
      final results = engine.lookup('bo');

      expect(results, isNotEmpty);
      expect(results.every((c) => !c.isSpellingSuggestion), isTrue);
      expect(results.map((c) => c.text), everyElement(startsWith('bo')));

      // Each row is directly renderable as "word · level · Arabic meaning".
      for (final candidate in results) {
        expect(candidate.text, isNotEmpty);
        expect(candidate.meaning, isNotEmpty);
        expect(candidate.senseId, isNotNull);
      }
    });

    test('an exact match is listed before longer words sharing the prefix', () {
      final results = engine.lookup('research');

      expect(results.first.text, 'research');
    });

    test('every candidate carries a sense id, and the two senses of one word '
        'differ', () {
      final results = engine.lookup('book');
      final ids = results.map((c) => c.senseId).toSet();

      expect(ids.length, results.length,
          reason: 'the sense id is the identity, so it must be unique');
      expect(ids.every((id) => id != null && id.isNotEmpty), isTrue);
    });
  });

  group('adding', () {
    test('a valid word enters the pipeline at the first skill only', () {
      final word = engine.addWord(user, candidate('book'));

      expect(word.text, 'book');
      expect(word.currentSkill, SkillType.reading);
      expect(
        word.skills.where((s) => s.status == SkillStatus.available).length,
        1,
      );
    });

    test('the same word with the same meaning is refused', () {
      engine.addWord(user, candidate('book'));

      expect(
        () => engine.addWord(user, candidate('book')),
        throwsA(isA<ApiException>()
            .having((e) => e.code, 'code', 'WORD_ALREADY_ADDED')
            .having((e) => e.statusCode, 'status', 409)),
      );
    });

    test('the same word with a DIFFERENT meaning is allowed, and is a '
        'separate journey', () {
      final asNoun = engine.addWord(user, candidate('book'));
      final asVerb = engine.addWord(user, candidate('book', meaning: 1));

      expect(asNoun.id, isNot(asVerb.id));
      expect(asNoun.meaning, isNot(asVerb.meaning));

      // Failing one must not touch the other — they are independent words.
      expect(
        user.words.where((w) => w.text == 'book').length,
        2,
      );
    });

    test('a word the lexicon does not know is refused even if posted directly',
        () {
      // Simulates a client that built a candidate itself instead of picking one
      // from a lookup.
      const forged = WordCandidate(
        text: 'hch',
        meaning: 'شيء',
        definitionEn: 'made up',
        partOfSpeech: 'noun',
        suggestedLevel: CefrLevel.b1,
        isSpellingSuggestion: false,
      );

      expect(
        () => engine.addWord(user, forged),
        throwsA(isA<ApiException>()
            .having((e) => e.code, 'code', 'WORD_NOT_FOUND')),
      );
    });

    test('a real word with an invented meaning is refused', () {
      const forged = WordCandidate(
        text: 'book',
        meaning: 'طائرة',
        definitionEn: 'not a meaning of book',
        partOfSpeech: 'noun',
        suggestedLevel: CefrLevel.a1,
        isSpellingSuggestion: false,
      );

      expect(
        () => engine.addWord(user, forged),
        throwsA(isA<ApiException>()
            .having((e) => e.code, 'code', 'WORD_NOT_FOUND')),
        reason: 'the meaning must come from the lexicon, not from the client',
      );
    });

    test('an empty meaning is refused', () {
      const empty = WordCandidate(
        text: 'book',
        meaning: '',
        definitionEn: '',
        partOfSpeech: 'noun',
        suggestedLevel: CefrLevel.a1,
        isSpellingSuggestion: false,
      );

      expect(
        () => engine.addWord(user, empty),
        throwsA(isA<ApiException>()
            .having((e) => e.code, 'code', 'INVALID_WORD')),
      );
    });

    test('the CEFR level comes from the lexicon, not from the learner', () {
      final word = engine.addWord(user, candidate('book'));

      expect(word.cefrLevel, candidate('book').suggestedLevel);
    });

    test('a forged level or definition is ignored — the stored row is the '
        'lexicon row', () {
      final real = candidate('book');
      final forged = WordCandidate(
        text: real.text,
        meaning: real.meaning,
        definitionEn: 'whatever the client felt like sending',
        partOfSpeech: 'interjection',
        suggestedLevel: CefrLevel.c2,
        isSpellingSuggestion: false,
        senseId: real.senseId,
      );

      final word = engine.addWord(user, forged);

      expect(word.cefrLevel, real.suggestedLevel);
      expect(word.definitionEn, real.definitionEn);
      expect(word.partOfSpeech, real.partOfSpeech);
    });

    test('a sense id that does not exist is refused', () {
      final real = candidate('book');
      final forged = WordCandidate(
        text: real.text,
        meaning: real.meaning,
        definitionEn: real.definitionEn,
        partOfSpeech: real.partOfSpeech,
        suggestedLevel: real.suggestedLevel,
        isSpellingSuggestion: false,
        senseId: 'sense:book:noun:not-a-real-sense',
      );

      expect(
        () => engine.addWord(user, forged),
        throwsA(isA<ApiException>()
            .having((e) => e.code, 'code', 'WORD_NOT_FOUND')),
      );
    });

    test('two learners may each add the same word independently', () {
      final other =
          engine.requireUser(engine.login('sara@wordos.app', 'wordos123').token);

      engine.addWord(user, candidate('book'));
      final theirs = engine.addWord(other, candidate('book'));

      expect(theirs.text, 'book');
      expect(other.words.where((w) => w.text == 'book').length, 1);
    });
  });
}
