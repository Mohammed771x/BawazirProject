import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:wordos/core/audio/speech_service.dart';
import 'package:wordos/core/widgets/app_widgets.dart';
import 'package:wordos/features/session/listening_player.dart';
import 'package:wordos/features/session/session_widgets.dart';

import 'support/journey.dart';

/// Nothing a Listening question was playing may follow the learner to the
/// next one — checked against the real voice on a real device, because the
/// report was of sound, and every flag in the app can say "stopped" while the
/// player goes on.
///
/// Needs the gaps collapsed, like `five_skills_test.dart`
/// (`WordOs__SkillIntervalDays=0` on the API).
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('a playing sentence stops when the question is left',
      (tester) async {
    await bootFreshLearner(tester, prefix: 'audio');
    final meaning = await addWord(tester, 'research');

    await openSkill(tester, 'Reading');
    await tapAny(tester, ['I finished reading']);
    await settle(tester);
    await answerEveryQuestion(tester, meaning: meaning);
    await backToHub(tester);

    await openSkill(tester, 'Listening');
    final speech = ProviderScope.containerOf(
            tester.element(find.byType(ListeningPlayer)))
        .read(speechServiceProvider);

    Future<bool> heard(String where) async {
      final ok = await waitFor(tester, () => speech.audible,
          total: const Duration(seconds: 30));
      debugPrint('· $where: audible=$ok speaking=${speech.isSpeaking}');
      return ok;
    }

    Future<void> expectSilent(String where) async {
      // Sampled for three seconds, not once: a sentence that "stops" and then
      // has the next one start is the same failure, a moment later.
      var loud = 0;
      for (var i = 0; i < 15; i++) {
        await tester.pump(const Duration(milliseconds: 200));
        if (speech.audible || speech.isSpeaking) loud++;
      }
      debugPrint('· $where: loud samples $loud/15');
      expect(loud, 0, reason: 'the voice carried on $where');
    }

    // ── The clip, left while it is playing ───────────────────────────────
    await tester.tap(find.descendant(
        of: find.byType(ListeningPlayer),
        matching: find.byIcon(Icons.play_arrow_rounded)));
    expect(await heard('the clip'), isTrue);
    await tester.pump(const Duration(seconds: 2));
    await tapAny(tester, ['I finished listening']);
    await expectSilent('into the questions');

    // ── Every question, left while its sentence is playing ───────────────
    var played = 0;
    for (var i = 0; i < 40; i++) {
      if (find.text('Session complete').evaluate().isNotEmpty) break;

      final sentence = find.byType(SentencePlayer);
      final hasSentence = sentence.evaluate().isNotEmpty;
      if (hasSentence) {
        final play = find.descendant(
            of: sentence, matching: find.byIcon(Icons.play_arrow_rounded));
        await tester.ensureVisible(play.first);
        await tester.tap(play.first);
        expect(await heard('question ${i + 1} sentence'), isTrue);
        await tester.pump(const Duration(milliseconds: 600));
        played++;
      }

      final known = find.byWidgetPredicate(
          (w) => w is OptionTile && w.label == meaning);
      final option =
          known.evaluate().isNotEmpty ? known.first : find.byType(OptionTile).first;
      await tester.ensureVisible(option);
      await tester.tap(option);
      await tester.pump(const Duration(milliseconds: 150));
      await tapAny(tester, ['Check']);
      await waitFor(
          tester,
          () =>
              find.text('Next').evaluate().isNotEmpty ||
              find.text('Finish').evaluate().isNotEmpty,
          total: const Duration(seconds: 30));
      if (hasSentence) {
        debugPrint('· after Check: audible=${speech.audible} '
            'speaking=${speech.isSpeaking}');
      }
      await tapAny(tester, ['Next', 'Finish']);
      await expectSilent('after question ${i + 1}');
      await settle(tester, total: const Duration(seconds: 60));
    }

    expect(played, greaterThan(0),
        reason: 'no question carried a sentence to play');

    // ── The result: the same player as before the questions ──────────────
    expect(find.text('Session complete'), findsOneWidget);
    expect(find.byType(ListeningPlayer), findsOneWidget,
        reason: 'the recording comes back in the full player');
    expect(find.byType(Slider), findsOneWidget);
  });
}
