import 'package:flutter/foundation.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

import 'support/journey.dart';

/// A skill opens when a word arrives at it and its gap has passed — never by
/// itself. Run against an API with its **normal** two-day gap (not the
/// collapsed one `five_skills_test.dart` asks for).
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('a new learner has Reading open and nothing else',
      (tester) async {
    await bootFreshLearner(tester, prefix: 'gap');
    debugPrint('· hub, no words: ${visibleText(tester)}');

    final meaning = await addWord(tester, 'research');
    debugPrint('· hub, one word added: ${visibleText(tester)}');

    // The three that have no practice mode: tapping one that is not open
    // must leave the learner on the Hub.
    for (final skill in ['Speaking', 'Spelling', 'Writing']) {
      await tapAny(tester, [skill]);
      await settle(tester, total: const Duration(seconds: 10));
      expect(isOnHub(tester), isTrue,
          reason: '$skill opened with no word waiting at it');
    }

    await openSkill(tester, 'Reading');
    await tapAny(tester, ['I finished reading']);
    await settle(tester);
    await answerEveryQuestion(tester, meaning: meaning);
    debugPrint('· reading result: ${visibleText(tester)}');
    await backToHub(tester);
    debugPrint('· hub, reading passed: ${visibleText(tester)}');

    for (final skill in ['Speaking', 'Spelling', 'Writing']) {
      await tapAny(tester, [skill]);
      await settle(tester, total: const Duration(seconds: 10));
      expect(isOnHub(tester), isTrue,
          reason: '$skill opened straight after Reading was passed');
    }

    // Listening offers practice when nothing is due — it must say so rather
    // than start a counted session two days early.
    await tapAny(tester, ['Listening']);
    await settle(tester, total: const Duration(seconds: 20));
    final listening = visibleText(tester);
    debugPrint('· listening, two days early: $listening');
    expect(listening.any((t) => t.contains('Practise') || t.contains('practice')),
        isTrue,
        reason: 'Listening started a real session before its gap had passed. '
            'Visible: $listening');
  }, timeout: const Timeout(Duration(minutes: 10)));
}
