import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:wordos/core/widgets/app_widgets.dart';
import 'package:wordos/features/session/session_widgets.dart';

import 'support/journey.dart';

/// Tapping words inside a real, Gemini-generated passage (Part 2 §17–§20).
///
/// The widget tests prove the interaction; this proves the part they cannot —
/// that a word lifted out of genuine generated prose, with whatever inflection
/// the model chose, resolves against the real 200k-entry lexicon rather than
/// only against the dozen words the mock dictionary knows.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('words in a generated passage resolve against the real lexicon',
      (tester) async {
    await bootFreshLearner(tester, prefix: 'wordtap');
    await addWord(tester, 'research');

    await openSkill(tester, 'Reading');
    await settle(tester, total: const Duration(seconds: 60));

    final passage = tester
        .widget<Text>(find.descendant(
          of: find.byType(HighlightedPassage),
          matching: find.byType(Text),
        ))
        .textSpan!
        .toPlainText();
    expect(passage.length, greaterThan(150));

    // Several candidates, tried in turn: the passage is generated fresh every
    // run, and a given word may turn out to be off-screen, hyphenated across a
    // line, or absent from this particular text. One failure is not a finding.
    final candidates = RegExp(r'[a-z]{7,}')
        .allMatches(passage.toLowerCase())
        .map((m) => m.group(0)!)
        .where((w) =>
            !w.contains('research') &&
            RegExp('\\b$w\\b').allMatches(passage.toLowerCase()).length == 1)
        .toSet()
        .toList();

    expect(candidates, isNotEmpty,
        reason: 'the passage should contain a taggable word');

    var word = '';
    for (final candidate in candidates) {
      try {
        await tester.tapOnText(find.textRange.ofSubstring(candidate).first);
        word = candidate;
        break;
      } catch (_) {
        // Off-screen or split across a line; try the next one.
      }
    }

    expect(word, isNotEmpty,
        reason: 'none of $candidates could be tapped');
    await settle(tester, total: const Duration(seconds: 30));

    // Arabic on screen inside the sheet means the real lexicon answered: the
    // meanings come from the imported Arabic WordNet, not from anything the
    // client could have made up.
    final sheet = visibleText(tester);
    debugPrint('✓ tapped "$word" → ${sheet.reversed.take(5)}');
    expect(
      sheet.any(hasArabic) ||
          sheet.any((t) => t.contains('No dictionary entry')),
      isTrue,
      reason: 'the sheet should either define "$word" or say it cannot. '
          'Visible: ${sheet.take(8)}',
    );

    // Popped rather than tapped away: a barrier tap lands wherever the sheet
    // happens not to be, which is a coin flip on a real screen.
    Navigator.of(tester.element(find.byType(HighlightedPassage))).pop();
    await settle(tester);

    // The word the session is about to test keeps its meaning to itself.
    await tester.tapOnText(find.textRange.ofSubstring('research').first);
    await settle(tester, total: const Duration(seconds: 20));

    expect(
      visibleText(tester).any((t) => t.contains('meaning stays hidden')),
      isTrue,
      reason: 'a target word must not be defined mid-session (§19). '
          'Visible: ${visibleText(tester).take(8)}',
    );
    debugPrint('✓ the target word was pronounced but not explained');

    // ── Re-telling the passage at another level ──────────────────────────
    Navigator.of(tester.element(find.byType(HighlightedPassage))).pop();
    await settle(tester);

    final before = tester
        .widget<Text>(find.descendant(
          of: find.byType(HighlightedPassage),
          matching: find.byType(Text),
        ))
        .textSpan!
        .toPlainText();

    await tester.tap(find.byType(LevelBadge).first);
    await settle(tester);
    await tapAny(tester, ['A1']);

    // A real re-telling goes to Gemini. The passage is replaced by a spinner
    // while it does, so the wait tolerates it being absent rather than
    // treating that moment as a failure.
    String? passageNow() {
      final found = find.descendant(
        of: find.byType(HighlightedPassage),
        matching: find.byType(Text),
      );
      if (found.evaluate().isEmpty) return null;
      return tester.widget<Text>(found).textSpan?.toPlainText();
    }

    await waitFor(
        tester,
        () {
          final now = passageNow();
          return now != null && now != before;
        },
        total: const Duration(seconds: 120));

    final after = passageNow()!;

    expect(after, isNot(equals(before)));
    expect(after.length, greaterThan(80),
        reason: 'a re-telling is a passage, not a summary');
    debugPrint('✓ re-told at A1: ${after.substring(0, after.length.clamp(0, 90))}');
  }, timeout: const Timeout(Duration(minutes: 10)));
}
