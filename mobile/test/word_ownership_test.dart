import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/api/wordos_api.dart';
import 'package:wordos/core/models/models.dart';
import 'package:wordos/features/words/word_widgets.dart';
import 'package:wordos/mock_backend/mock_wordos_api.dart';

import 'support/test_harness.dart';

/// What a learner may do to their own vocabulary: remove a word, and decide
/// what a word means.
///
/// Both answer the same complaint, reported from the device. The Arabic glosses
/// are a machine join of three datasets, and it shows — `sell` offers "أَقْنَعَ بِـ"
/// before "باع". A learner who can neither correct that nor remove the result
/// is stuck with a card they know to be wrong for the eight days it takes to
/// mature.
///
/// Three changes, tested here through the app as a learner meets them:
/// deleting (ADR-071), writing the meaning yourself (ADR-072), and adding a
/// word from a passage with the meaning that passage gave it (ADR-073).
void main() {
  /// A signed-in mock API, for the tests whose subject is the rule rather than
  /// the screen.
  ///
  /// The token has to be *read back*, not captured as null: `tokenReader` is
  /// called on every request, and a reader that always answers null signs the
  /// learner out again the moment they have signed in.
  ///
  /// `latencyScale: 0` because these tests await real futures rather than
  /// pumping a widget tree, and the artificial delay only makes them slow.
  Future<MockWordOsApi> signedInApi() async {
    String? token;
    final api = MockWordOsApi(
      tokenReader: () => token,
      latencyScale: 0,
    );

    final auth =
        await api.login(email: 'demo@wordos.app', password: 'wordos123');
    token = auth.token;

    return api;
  }

  Future<void> openMyWords(WidgetTester tester) async {
    await bootAndSignIn(tester);
    await tester.tap(find.text('My words').last);
    await tester.pumpAndSettle();
  }

  Future<void> openAddWord(WidgetTester tester) async {
    await openMyWords(tester);
    await tester.tap(find.text('Add word').last);
    await tester.pumpAndSettle();
  }

  // ── Deleting (ADR-071) ─────────────────────────────────────────────────

  /// How many words the list says the learner has.
  ///
  /// Read from the header rather than by counting `WordTile`s: the list is
  /// lazily built, so counting widgets counts what fits on screen. That is what
  /// this test asserted first, and it "passed" at four words for a learner who
  /// had more.
  int wordCountShown(WidgetTester tester) {
    final header = tester
        .widgetList<Text>(find.byType(Text))
        .map((t) => t.data ?? '')
        .firstWhere((d) => RegExp(r'^\d+ words$').hasMatch(d));
    return int.parse(header.split(' ').first);
  }

  testWidgets('a word can be deleted from its own screen', (tester) async {
    await openMyWords(tester);

    final before = wordCountShown(tester);
    expect(before, greaterThan(1));

    final target = tester.widget<WordTile>(find.byType(WordTile).first).word;

    await tester.tap(find.byType(WordTile).first);
    await tester.pumpAndSettle();

    await tester.tap(find.byIcon(Icons.delete_outline_rounded));
    await tester.pumpAndSettle();

    // Asked first. Five skills and eight days of waiting can sit behind a word,
    // and an accidental tap is not recoverable by anything the learner can
    // reach — adding it back starts from Reading.
    expect(find.text('Delete this word?'), findsOneWidget);

    await tester.tap(find.widgetWithText(FilledButton, 'Delete word'));
    await tester.pumpAndSettle();
    // The list refetches rather than dropping the row locally (rule R1), so the
    // mock's artificial latency has to be let through before it is read.
    await tester.pump(const Duration(seconds: 1));
    await tester.pumpAndSettle();

    // Back on the list, one word lighter — and it is *that* word that went.
    expect(wordCountShown(tester), before - 1);
    expect(
      tester
          .widgetList<WordTile>(find.byType(WordTile))
          .map((tile) => tile.word.id),
      isNot(contains(target.id)),
    );
  });

  testWidgets('cancelling the confirmation keeps the word', (tester) async {
    await openMyWords(tester);

    final before = wordCountShown(tester);

    await tester.tap(find.byType(WordTile).first);
    await tester.pumpAndSettle();
    await tester.tap(find.byIcon(Icons.delete_outline_rounded));
    await tester.pumpAndSettle();

    await tester.tap(find.widgetWithText(TextButton, 'Cancel'));
    await tester.pumpAndSettle();

    // Still on the word's own screen, and the word is still there.
    expect(find.text('Delete this word?'), findsNothing);

    await tester.pageBack();
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 1));
    await tester.pumpAndSettle();
    expect(wordCountShown(tester), before);
  });

  testWidgets('a deleted word is gone from every list, not just this one',
      (tester) async {
    final api = await signedInApi();

    final before = await api.words();
    final target = before.items.first;

    await api.deleteWord(target.id);

    final after = await api.words();
    expect(after.items.map((w) => w.id), isNot(contains(target.id)));
    expect(after.total, before.total - 1);

    // And it is not addressable either: a learner who kept the route open no
    // longer has a word at the end of it.
    await expectLater(
      api.wordDetail(target.id),
      throwsA(isA<ApiException>()),
    );
  });

  test('a deleted word can be added again, as a new journey', () async {
    final api = await signedInApi();

    final candidates = await api.lookupWord('book');
    final sense = candidates.first;

    final first = await api.addWord(sense);
    await api.deleteWord(first.id);

    // Without deletion being invisible to the duplicate rule this is "you have
    // already added this word" for ever, on the strength of a row the learner
    // believes is gone.
    final second = await api.addWord(sense);

    expect(second.id, isNot(first.id));
    expect(second.currentSkill, SkillType.reading,
        reason: 'a fresh journey starts at the first skill, not where the '
            'deleted one left off');
  });

  test('deleting twice is not an error the learner has to see', () async {
    final api = await signedInApi();

    final target = (await api.words()).items.first;

    await api.deleteWord(target.id);
    await api.deleteWord(target.id); // a retry must not become a failure
  });

  // ── Writing the meaning yourself (ADR-072) ─────────────────────────────

  testWidgets('a learner can write the meaning instead of picking one',
      (tester) async {
    await openAddWord(tester);

    await tester.enterText(find.byType(TextField).first, 'book');
    await tester.pump(const Duration(milliseconds: 400)); // debounce
    await tester.pumpAndSettle();

    // The dictionary's own meanings are still offered first — this is a way
    // out of them, not a replacement for them.
    expect(find.text('كتاب'), findsWidgets);

    await tester.tap(find.text('Write the meaning yourself'));
    await tester.pumpAndSettle();

    // A real sense of `book`, and not the one the list offers first.
    await tester.enterText(find.byType(TextField).last, 'يحجز');
    await tester.pumpAndSettle();

    await tester.tap(find.widgetWithText(FilledButton, 'Save the word'));
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();

    // Saved with the learner's wording, not the lexicon's first gloss.
    expect(find.text('يحجز'), findsWidgets);
  });

  testWidgets('a meaning the checker rejects is answered, not just refused',
      (tester) async {
    await openAddWord(tester);

    await tester.enterText(find.byType(TextField).first, 'book');
    await tester.pump(const Duration(milliseconds: 400));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Write the meaning yourself'));
    await tester.pumpAndSettle();

    // Not a meaning of `book` at all — the case the product owner described.
    await tester.enterText(find.byType(TextField).last, 'إنسان');
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Save the word'));
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();

    // The sheet stays open, their text stays in it, and the checker's answer
    // appears beside it with meanings it would accept (ADR-074). Closing the
    // sheet to report this would throw away both.
    expect(find.text('Check this meaning'), findsOneWidget);
    expect(find.text('Did you mean:'), findsOneWidget);
    expect(find.byType(ActionChip), findsWidgets);
    expect(find.text('إنسان'), findsWidgets, reason: 'their text is not lost');
  });

  testWidgets('the learner may insist, and it is recorded as an override',
      (tester) async {
    await openAddWord(tester);

    await tester.enterText(find.byType(TextField).first, 'book');
    await tester.pump(const Duration(milliseconds: 400));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Write the meaning yourself'));
    await tester.pumpAndSettle();

    await tester.enterText(find.byType(TextField).last, 'إنسان');
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Save the word'));
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();

    // The checker is sometimes wrong, and this feature exists because an
    // automated source of meanings was (ADR-072). So there is a way past it —
    // below the suggestions, and quieter than them.
    await tester.tap(find.widgetWithText(TextButton, 'Save it as I wrote it'));
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();

    expect(find.text('إنسان'), findsWidgets);
    expect(find.text('Check this meaning'), findsNothing);
  });

  testWidgets('tapping a suggestion saves that meaning instead',
      (tester) async {
    await openAddWord(tester);

    await tester.enterText(find.byType(TextField).first, 'book');
    await tester.pump(const Duration(milliseconds: 400));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Write the meaning yourself'));
    await tester.pumpAndSettle();

    await tester.enterText(find.byType(TextField).last, 'إنسان');
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Save the word'));
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();

    await tester.tap(find.byType(ActionChip).first);
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();

    // A suggestion is the checker's own wording, so it goes in as an ordinary
    // accepted meaning rather than as an override.
    expect(find.text('Check this meaning'), findsNothing);
    expect(find.text('كتاب'), findsWidgets);
  });

  testWidgets('the option appears only once one word is in view',
      (tester) async {
    await openAddWord(tester);

    // A prefix that still matches several different words. The learner is
    // choosing a *word* here, and "write the meaning" would have to ask which
    // of them it was a meaning for.
    await tester.enterText(find.byType(TextField).first, 's');
    await tester.pump(const Duration(milliseconds: 400));
    await tester.pumpAndSettle();

    if (find.text('Choose the word').evaluate().isNotEmpty) {
      expect(find.text('Write the meaning yourself'), findsNothing);
    }

    await tester.enterText(find.byType(TextField).first, 'research');
    await tester.pump(const Duration(milliseconds: 400));
    await tester.pumpAndSettle();

    expect(find.text('Write the meaning yourself'), findsOneWidget);
  });

  testWidgets('the option survives a search that returns several words',
      (tester) async {
    await openAddWord(tester);

    // The case that broke it on the device. Typing `sell` returns `sell`,
    // `selling`, `seller` and `sell off` — four different words — so a rule of
    // "every candidate shares one text" is false for the most ordinary search
    // there is, and the card silently never appeared. What was typed *is* one
    // of the words on offer, and that is the word.
    await tester.enterText(find.byType(TextField).first, 'book');
    await tester.pump(const Duration(milliseconds: 400));
    await tester.pumpAndSettle();

    final texts = tester
        .widgetList<Text>(find.byType(Text))
        .map((w) => w.data ?? '')
        .toList();
    expect(texts, contains('book'));

    // Exactly one, however long the list is: the obvious way to place this card
    // drew it twice on any list past three rows.
    expect(find.text('Write the meaning yourself'), findsOneWidget);
  });

  test('a written meaning must be Arabic, and the word must be real',
      () async {
    final api = await signedInApi();

    // The meaning is the learner's; the word is not. Nothing can generate a
    // passage around `zzzznotaword`, or say what level it is, or clue it in
    // Spelling.
    await expectLater(
      api.addWordWithMeaning(text: 'zzzznotaword', meaning: 'معنى'),
      throwsA(isA<ApiException>().having((e) => e.code, 'code',
          'WORD_NOT_FOUND')),
    );

    // Every skill marks answers against this string. An English one makes its
    // own questions unanswerable, so it is refused now rather than discovered
    // two days later in a session.
    await expectLater(
      api.addWordWithMeaning(text: 'book', meaning: 'a notebook'),
      throwsA(isA<ApiException>().having((e) => e.code, 'code',
          'MEANING_NOT_ARABIC')),
    );
  });

  test('the checker carries what it would accept, not just a refusal',
      () async {
    final api = await signedInApi();

    // The refusal has to be actionable. "That is wrong" with nothing beside it
    // leaves the learner guessing, which is the state this feature exists to
    // get them out of (ADR-074).
    await expectLater(
      api.addWordWithMeaning(text: 'book', meaning: 'إنسان'),
      throwsA(isA<MeaningRejectedException>()
          .having((e) => e.suggestions, 'suggestions', isNotEmpty)),
    );
  });

  test('an insisted meaning is saved, and only that one is', () async {
    final api = await signedInApi();

    final word = await api.addWordWithMeaning(
      text: 'book',
      meaning: 'إنسان',
      acceptAnyway: true,
    );

    // Their wording, untouched — not quietly replaced by a suggestion.
    expect(word.meaning, 'إنسان');
  });

  test('the same written meaning cannot be added twice', () async {
    final api = await signedInApi();

    await api.addWordWithMeaning(text: 'book', meaning: 'يحجز');

    await expectLater(
      api.addWordWithMeaning(text: 'book', meaning: 'يحجز'),
      throwsA(isA<ApiException>().having((e) => e.code, 'code',
          'WORD_ALREADY_ADDED')),
    );
  });

  // ── The meaning the passage gave it (ADR-073) ──────────────────────────

  test('a word added from a passage keeps that passage\'s meaning', () async {
    final api = await signedInApi();

    final session = await api.startSession(SkillType.reading);
    final glossary = session.content?.glossary ?? const <GlossaryEntry>[];
    expect(glossary, isNotEmpty,
        reason: 'a reading passage glosses every content word in it');

    // `student` is in this passage and in the dictionary, and the two disagree
    // about what it means — the passage says 'طالب', the dictionary's first
    // sense is 'دارس'. That disagreement is the test.
    final entry = glossary.firstWhere((g) => g.word == 'student');

    final dictionary = await api.defineWord('student');
    expect(dictionary.senses.first.meaning, isNot(entry.meaning),
        reason: 'otherwise this test would pass either way');

    final added = await api.addWordFromPassage(
      sessionId: session.id,
      word: entry.word,
    );

    // The meaning it carried *in that sentence*. This is the whole bug: the
    // sheet used to fetch the dictionary senses and pick whichever read
    // closest, which for a word with six senses is a coin flip.
    expect(added.meaning, entry.meaning);
  });

  test('a word the passage never glossed is refused, not guessed at',
      () async {
    final api = await signedInApi();

    final session = await api.startSession(SkillType.reading);

    // Names and numbers appear in generated text and carry no gloss. The
    // refusal is what sends the sheet back to the ordinary dictionary, which
    // is the right answer for a word this passage never explained.
    await expectLater(
      api.addWordFromPassage(sessionId: session.id, word: 'Marrakesh'),
      throwsA(isA<ApiException>().having((e) => e.code, 'code',
          'NOT_IN_PASSAGE')),
    );
  });
}
