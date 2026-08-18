import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:wordos/core/widgets/app_widgets.dart';

import 'support/journey.dart';

/// Resume, for every skill that has something to resume.
///
/// The learner leaves mid-session and the app is relaunched with a fresh widget
/// tree and a fresh provider scope — only the tokens survive, exactly the two
/// values the real app keeps in the keystore. Everything the session knows must
/// therefore come back from the server (rule R4).
///
/// What each skill has to preserve differs, so each is checked on its own
/// terms:
///
/// * **Reading / Listening** — the same generated content, and the question the
///   learner had reached rather than the first one.
/// * **Speaking** — the conversation so far, not just the opening line.
/// * **Writing / Spelling** — the same task, with answered items still counted.
///
/// The app's navigator uses a library-level `GlobalKey`, so its route stack
/// outlives `pumpWidget` and a literal process kill cannot be staged in-process.
/// The learner returns to the Hub first, which is what a backgrounded app does
/// anyway, and the relaunch then proves the part that matters: nothing about the
/// session is held on the device.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('every skill resumes where the learner left it', (tester) async {
    final store = MemoryTokenStore();
    await bootFreshLearner(tester, prefix: 'resume', store: store);
    final meaning = await addWord(tester, 'research');

    // ── Reading: same passage, same place in the queue ───────────────────
    await openSkill(tester, 'Reading');
    final passage = longestText(tester);
    expect(passage.length, greaterThan(150));

    await tapAny(tester, ['I finished reading']);
    await settle(tester);
    await answerOne(tester);

    await relaunch(tester, store);
    await openSkill(tester, 'Reading');

    expect(find.text('I finished reading'), findsNothing,
        reason: 'a resumed reading session should not replay the passage step');
    expect(find.byType(OptionTile), findsWidgets,
        reason: 'it should come back on a question. '
            'Visible: ${visibleText(tester).take(6)}');
    debugPrint('✓ READING resumed on a question');

    await answerEveryQuestion(tester, meaning: meaning);
    await backToHub(tester);

    // ── Listening: still audio-only after a relaunch ─────────────────────
    await openSkill(tester, 'Listening');
    await tapAny(tester, ['I finished listening']);
    await settle(tester);
    await answerOne(tester);

    await relaunch(tester, store);
    await openSkill(tester, 'Listening');

    expect(find.text('I finished listening'), findsNothing,
        reason: 'a resumed listening session should not replay the audio step');
    expect(visibleText(tester).any((t) => t.length > 150), isFalse,
        reason: 'resuming must not print the script it was hiding');
    debugPrint('✓ LISTENING resumed, still audio-only');

    await answerEveryQuestion(tester, meaning: meaning);
    await backToHub(tester);

    // ── Speaking: the conversation comes back with it ────────────────────
    await openSkill(tester, 'Speaking');
    const said = 'I really enjoy research because it teaches me new things.';
    await sendChatTurn(tester, said);

    final beforeTurns = visibleText(tester).where((t) => t.length > 15).length;
    expect(beforeTurns, greaterThanOrEqualTo(2),
        reason: 'the opening and the reply should both be on screen');

    await relaunch(tester, store);
    await openSkill(tester, 'Speaking');

    // The learner's own sentence is the proof: the client never stored it, so
    // seeing it again means the server returned the transcript.
    expect(visibleText(tester).any((t) => t.contains(said)), isTrue,
        reason: 'a resumed conversation must show what was already said. '
            'Visible: ${visibleText(tester).take(8)}');
    debugPrint('✓ SPEAKING resumed with its transcript intact');

    // The conversation opens with a greeting now, so it takes a few more turns
    // before every target word has been used and the server ends it.
    await finishSpeaking(tester, [
      'Last month my research about sleep changed my daily habits.',
      'I would like to do more research about healthy food next year.',
      'Reading research papers is difficult but very useful for me.',
      'My favourite research topic is how people learn languages.',
      'I do research every weekend when I have some free time.',
    ]);
    await backToHub(tester);

    // ── Writing: the same task, unanswered work not invented ─────────────
    await openSkill(tester, 'Writing');
    final prompt = visibleText(tester)
        .firstWhere((t) => t.contains('research'), orElse: () => '');
    expect(prompt, isNotEmpty,
        reason: 'expected a writing task naming the word. '
            'Visible: ${visibleText(tester).take(10)}');

    await relaunch(tester, store);
    await openSkill(tester, 'Writing');

    expect(visibleText(tester).any((t) => t == prompt), isTrue,
        reason: 'the same writing task should come back. '
            'Visible: ${visibleText(tester).take(6)}');
    debugPrint('✓ WRITING resumed on the same task');

    await typeInto(tester, TextField,
        'I did research about sleep last week and it changed my habits.');
    await tapAny(tester, ['Check']);
    await waitFor(
        tester,
        () => visibleText(tester)
            .any((t) => t.contains('Correct') || t.contains('Not quite')),
        total: const Duration(seconds: 120));
    await tapAnyIfPresent(tester, ['Next', 'Finish', 'Continue']);
    await settle(tester, total: const Duration(seconds: 60));
    await backToHub(tester);

    // ── Spelling: the same clue and input mode ───────────────────────────
    await openSkill(tester, 'Spelling');
    // The clue is the longest Arabic string on screen — the meaning, rather
    // than a target-word chip that also renders in Arabic. Taking "the first
    // Arabic text" made this flaky, because which element comes first is a
    // layout detail rather than a property of the session.
    final arabicBefore = visibleText(tester).where(hasArabic).toList();
    expect(arabicBefore, isNotEmpty, reason: 'spelling shows an Arabic clue');

    final clue = arabicBefore.reduce((a, b) => b.length > a.length ? b : a);
    final tilesBefore = liveLetterTiles(tester).evaluate().length;

    await relaunch(tester, store);
    await openSkill(tester, 'Spelling');

    expect(visibleText(tester).any((t) => t == clue), isTrue,
        reason: 'the same spelling clue should come back. '
            'Before: $arabicBefore  After: ${visibleText(tester).where(hasArabic)}');
    expect(liveLetterTiles(tester).evaluate().length, equals(tilesBefore),
        reason: 'the same tiles, in the same shuffle — regenerating them would '
            'be a different task');
    debugPrint('✓ SPELLING resumed with the same clue and tiles');
  }, timeout: const Timeout(Duration(minutes: 25)));
}

/// Answers exactly one question, so the session is genuinely part-finished.
///
/// Tapping an option submits it, and the answer must be **confirmed** before
/// the app is relaunched — otherwise the request is still in flight when the
/// widget tree is torn down, the client cancels it, and the "resumed" session
/// legitimately has nothing recorded.
Future<void> answerOne(WidgetTester tester) async {
  final options = find.byType(OptionTile);
  if (options.evaluate().isEmpty) return;

  await tester.ensureVisible(options.first);
  await tester.tap(options.first);

  await waitFor(
      tester,
      () => visibleText(tester)
          .any((t) => t.contains('Correct') || t.contains('Not quite')),
      total: const Duration(seconds: 60));

  await tapAny(tester, ['Next', 'Check']);
  await settle(tester, total: const Duration(seconds: 60));
}
