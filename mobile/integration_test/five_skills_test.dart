import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:wordos/core/widgets/app_widgets.dart';

import 'support/journey.dart';

/// Every skill screen, driven through the real UI against the real stack:
/// Flutter → ASP.NET Core → Python → Gemini → PostgreSQL and back.
///
/// One word is carried the whole way — Reading → Listening → Speaking →
/// Writing → Spelling → Active — so each screen is exercised on a word that
/// genuinely arrived there through the pipeline, not one placed by a fixture.
///
/// The two-day gaps are collapsed for this run (`WordOs__SkillIntervalDays=0`
/// on the API); the gaps themselves are pinned by backend tests, which assert
/// both the schedule and the 409 for starting early. Everything else here is
/// real: real Gemini content, real evaluations, real rows.
///
/// ```
/// # API:  ASPNETCORE_ENVIRONMENT=Development WordOs__SkillIntervalDays=0 \
/// #         dotnet run --project src/WordOs.Api --urls http://127.0.0.1:5199
/// flutter test integration_test/five_skills_test.dart -d <device> \
///   --dart-define=WORDOS_MOCK=false \
///   --dart-define=WORDOS_API_BASE_URL=http://127.0.0.1:5199/api
/// ```
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('one word travels all five skills through the real UI',
      (tester) async {
    await bootFreshLearner(tester, prefix: 'skills');
    // The meaning the learner chose — used to answer the one question that
    // decides whether the word passes each skill.
    final meaning = await addWord(tester, 'research');

    // ── Reading ──────────────────────────────────────────────────────────
    await openSkill(tester, 'Reading');

    final passage = longestText(tester);
    expect(passage.length, greaterThan(150),
        reason: 'Reading should show a generated passage');
    expect(passage.toLowerCase(), contains('research'));
    debugPrint('✓ READING · passage ${passage.length} chars');

    await tapAny(tester, ['I finished reading']);
    await settle(tester);
    await answerEveryQuestion(tester, meaning: meaning);
    await expectPass(tester, 'Reading');

    // ── Listening ────────────────────────────────────────────────────────
    await backToHub(tester);
    await openSkill(tester, 'Listening');

    // Audio-first: the script is spoken, never shown. Seeing the passage as
    // text here would defeat the whole skill (§32–34).
    final listeningTexts = visibleText(tester);
    expect(listeningTexts.any((t) => t.length > 150), isFalse,
        reason: 'a listening session must not print its script. '
            'Visible: ${listeningTexts.take(8)}');
    expect(
        listeningTexts.any((t) =>
            t.contains('Listen') || t.contains('listening')),
        isTrue,
        reason: 'Visible: ${listeningTexts.take(8)}');
    debugPrint('✓ LISTENING · audio-only, no printed script');

    await tapAny(tester, ['I finished listening']);
    await settle(tester);
    await answerEveryQuestion(tester, meaning: meaning);
    await expectPass(tester, 'Listening');

    // ── Speaking ─────────────────────────────────────────────────────────
    await backToHub(tester);
    await openSkill(tester, 'Speaking');

    // Speaking opens on a warm-up — each word, four meanings — so that nobody
    // walks into a conversation about words they cannot recall. `openSkill`
    // has just cleared it; what follows must be the conversation itself.
    expect(find.byType(OptionTile), findsNothing,
        reason: 'once the warm-up is done, Speaking is a conversation and not '
            'a list of questions');

    final opening = visibleText(tester).firstWhere(
        (t) => t.length > 20 && !t.contains('Speaking'),
        orElse: () => '');
    expect(opening, isNotEmpty, reason: 'the AI should open the conversation');
    debugPrint('✓ SPEAKING · opening: '
        '${opening.substring(0, opening.length.clamp(0, 90))}');

    // A turn substantial enough to judge — a bare mention does not pass the
    // word (ADR-016), and that decision is the server's.
    await sendChatTurn(tester,
        'I really enjoy research because it helps me understand new ideas.');

    final reply = visibleText(tester).length;
    expect(reply, greaterThan(0));

    // The conversation now opens with a greeting rather than an exercise, so it
    // takes several turns to cover the word — and there is no finish button by
    // design: the server ends it once every target word has been used.
    await finishSpeaking(tester, [
      'Last week I did some research about sleep and it changed my habits.',
      'I would like to do more research about healthy food next year.',
      'Reading research papers is hard for me but it is very useful.',
      'My favourite research topic is how people learn new languages.',
    ]);
    debugPrint('✓ SPEAKING · conversation completed by the server');

    await expectPass(tester, 'Speaking');

    // ── Writing ──────────────────────────────────────────────────────────
    await backToHub(tester);
    await openSkill(tester, 'Writing');

    final prompt = visibleText(tester)
        .firstWhere((t) => t.contains('research'), orElse: () => '');
    expect(prompt, isNotEmpty,
        reason: 'Writing asks the learner to use *this* word, never a generic '
            'topic (§41–43). Visible: ${visibleText(tester).take(6)}');
    debugPrint('✓ WRITING · prompt: $prompt');

    // Deliberately contains a grammar slip — "a research" is uncountable — but
    // uses the word correctly. §32 says that must still pass, and the decision
    // is the backend's, not the model's (ADR-015).
    await typeInto(tester, TextField,
        'Yesterday I did a research about sleep and it change my daily habits.');
    await tapAny(tester, ['Check']);

    // While Gemini is evaluating, the button reads "Evaluating" — there is no
    // spinner to wait on, so waiting on the verdict itself is the only honest
    // way to know the round trip finished.
    await waitFor(
        tester,
        () => visibleText(tester)
            .any((t) => t.contains('Correct') || t.contains('Not quite')),
        total: const Duration(seconds: 120));

    final feedback = visibleText(tester);
    expect(feedback.any((t) => t.contains('Correct')), isTrue,
        reason: 'a grammar slip alone must not fail correct usage. '
            'Visible: ${feedback.take(8)}');
    expect(feedback.any((t) => t.length > 30), isTrue,
        reason: 'Gemini should return real feedback, not an empty banner');
    debugPrint('✓ WRITING · passed with feedback despite the grammar slip');

    await tapAny(tester, ['Next', 'Finish', 'Continue']);
    await settle(tester, total: const Duration(seconds: 60));
    await expectPass(tester, 'Writing');

    // ── Spelling ─────────────────────────────────────────────────────────
    await backToHub(tester);
    await openSkill(tester, 'Spelling');

    final spellingTexts = visibleText(tester);
    final tiles = find.byWidgetPredicate(
        (w) => w.runtimeType.toString() == '_LetterTile');

    // Below B2 the learner gets the Arabic meaning and letter tiles; at B2 and
    // above, an English definition and free typing (MVP Core §33–34).
    final usesTiles = tiles.evaluate().isNotEmpty;
    debugPrint('✓ SPELLING · input mode: '
        '${usesTiles ? "LETTER_TILES" : "FREE_TYPING"}');

    // A hint is always offered, whichever mode it is.
    expect(spellingTexts.any((t) => t.contains('hint')), isTrue,
        reason: 'spelling always offers a hint. Visible: ${spellingTexts.take(8)}');

    // The ladder: press until it runs out, and the last rung is always the
    // number of letters (Part 2 §38–§40). Each press must add something, so
    // the visible text can only grow.
    var before = visibleText(tester).length;
    await tapAny(tester, ['Need a hint?']);
    await settle(tester);
    expect(visibleText(tester).length, greaterThan(before),
        reason: 'the first press must reveal a rung');

    while (find.text('Something easier').evaluate().isNotEmpty) {
      before = visibleText(tester).length;
      await tapAny(tester, ['Something easier']);
      await settle(tester);
      expect(visibleText(tester).length, greaterThan(before),
          reason: 'every press must reveal a rung');
    }

    expect(visibleText(tester).any((t) => t.contains('Number of letters')),
        isTrue,
        reason: 'the ladder ends at the number of letters');

    if (usesTiles) {
      expect(spellingTexts.any((t) => t.contains('Tap the letters')), isTrue);

      // The pool holds decoys (§36–§37), so finishing it is not the same as
      // spelling the word — the learner has to leave tiles behind.
      expect(tiles.evaluate().length, greaterThan('research'.length),
          reason: 'the letter pool should be larger than the word');

      await spellWithTiles(tester, 'research');
      expect(liveLetterTiles(tester).evaluate(), isNotEmpty,
          reason: 'unused tiles should remain once the word is spelled');
    } else {
      await typeInto(tester, TextField, 'research');
    }
    await tapAny(tester, ['Check']);
    await settle(tester, total: const Duration(seconds: 60));

    expect(visibleText(tester).any((t) => t.contains('Correct')), isTrue,
        reason: 'the spelling was right. Visible: ${visibleText(tester).take(8)}');

    await tapAny(tester, ['Next', 'Finish', 'Continue']);
    await settle(tester, total: const Duration(seconds: 60));

    // Five skills passed → Mature → Active vocabulary.
    final result = visibleText(tester).join(' | ');
    expect(result.toLowerCase(), contains('active'),
        reason: 'passing the last skill should activate the word. '
            'Visible: $result');
    debugPrint('✓ SPELLING · word reached Active vocabulary');

    // Spelling carries no CEFR band anywhere in the UI (ADR-008).
    await backToHub(tester);
    await tapAny(tester, ['Settings']);
    await settle(tester);
    await expectSpellingIsUnlevelled(tester);
  }, timeout: const Timeout(Duration(minutes: 20)));
}
