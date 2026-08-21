import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/l10n/app_strings.dart';
import 'package:wordos/core/models/models.dart';
import 'package:wordos/core/theme/app_theme.dart';
import 'package:wordos/features/words/word_widgets.dart';

/// What a learner is told a word *is* (ADR-056).
void main() {
  const en = AppStrings(Locale('en'));
  const ar = AppStrings(Locale('ar'));

  group('part of speech', () {
    // Every code the lexicon actually stores, taken from the live database:
    //   n 119407 · v 80656 · a 28823 · r 5306 · prep 48 · det 37 · pron 28
    //   conj 20 · aux 17 · modal 10 · intj 4 · part 3
    //
    // The short forms were the gap: only the spelled-out names were listed, so
    // a learner holding a word tagged `modal` was shown the word "modal" in an
    // Arabic interface.
    const stored = [
      'n', 'v', 'a', 'r', 's',
      'prep', 'det', 'pron', 'conj', 'aux', 'modal', 'intj', 'part',
    ];

    test('every code the lexicon stores has a label in both languages', () {
      for (final code in stored) {
        expect(en.partOfSpeechLabel(code), isNot(code),
            reason: '"$code" fell through to the raw code in English');
        expect(ar.partOfSpeechLabel(code), isNot(code),
            reason: '"$code" fell through to the raw code in Arabic');

        // And the Arabic label is actually Arabic, not the English one reused.
        expect(ar.partOfSpeechLabel(code),
            matches(RegExp(r'[؀-ۿ]')),
            reason: '"$code" is not translated');
      }
    });

    test('the short code and the spelled-out name agree', () {
      // Both spellings reach the app — the importer writes `prep`, the AI
      // glossary writes `preposition` — and they must not read differently.
      const pairs = {
        'n': 'noun',
        'v': 'verb',
        'a': 'adjective',
        'r': 'adverb',
        'prep': 'preposition',
        'det': 'determiner',
        'pron': 'pronoun',
        'conj': 'conjunction',
        'aux': 'auxiliary',
      };

      pairs.forEach((short, long) {
        expect(ar.partOfSpeechLabel(short), ar.partOfSpeechLabel(long),
            reason: '"$short" and "$long" are the same thing');
      });
    });

    test('a code nobody has seen shows itself rather than an empty chip', () {
      expect(en.partOfSpeechLabel('zzz'), 'zzz');
    });
  });

  group('word form', () {
    test('each inflection is named in both languages', () {
      for (final key in ['past', 'pastParticiple', 'ing', 'plural']) {
        expect(en.wordFormLabel(key), isNotNull, reason: key);
        expect(ar.wordFormLabel(key), isNotNull, reason: key);
        expect(ar.wordFormLabel(key), matches(RegExp(r'[؀-ۿ]')),
            reason: '$key is not translated');
      }
    });

    test('a plain word carries no form label', () {
      // A learner looking at `book` needs nothing telling them it is `book`.
      expect(en.wordFormLabel(null), isNull);
      expect(en.wordFormLabel('nonsense'), isNull);
    });
  });

  group('the word tile', () {
    Word word({required String pos, String? form, String text = 'went'}) => Word(
          id: 'w1',
          text: text,
          meaning: 'ذهب',
          definitionEn: 'a definition',
          partOfSpeech: pos,
          form: form,
          cefrLevel: CefrLevel.b1,
          state: WordState.learning,
          currentSkill: SkillType.reading,
          addedAt: DateTime.utc(2026, 8, 20),
          nextEligibleAt: null,
          exposureCount: 0,
          skills: const [],
        );

    // The strings are overridden directly rather than through preferences:
    // this test is about what the tile draws, not about how a locale is loaded.
    Future<void> show(WidgetTester tester, Word w, {String locale = 'ar'}) =>
        tester.pumpWidget(ProviderScope(
          overrides: [
            stringsProvider.overrideWithValue(AppStrings(Locale(locale))),
          ],
          child: MaterialApp(
            locale: Locale(locale),
            theme: AppTheme.light(),
            home: Scaffold(body: WordTile(word: w)),
          ),
        ));

    testWidgets('an inflected entry says which form it is', (tester) async {
      await show(tester, word(pos: 'v', form: 'past'));
      await tester.pumpAndSettle();

      // A learner with `go` and `went` in one list cannot tell them apart
      // otherwise (ADR-056).
      expect(find.text('فعل · الماضي'), findsOneWidget);
    });

    testWidgets('a plain word says only what kind it is', (tester) async {
      await show(tester, word(pos: 'n', text: 'book'));
      await tester.pumpAndSettle();

      // No form label: `book` needs nothing telling the learner it is `book`.
      expect(find.text('اسم'), findsOneWidget);
    });

    testWidgets('a closed-class word is named, not coded', (tester) async {
      // `modal` and `prep` are what the importer writes. Before this they fell
      // through and the learner read the raw code.
      await show(tester, word(pos: 'modal', text: 'can'));
      await tester.pumpAndSettle();

      expect(find.text('فعل ناقص'), findsOneWidget);
      expect(find.text('modal'), findsNothing);
    });
  });
}
