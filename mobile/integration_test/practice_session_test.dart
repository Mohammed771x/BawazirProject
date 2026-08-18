import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

import 'support/journey.dart';

/// The empty-pipeline fallback, end to end (Part 2 §5).
///
/// A brand-new learner has nothing due — every word they add starts on Reading
/// with a gap in front of it — so the first thing many of them see is an empty
/// skill. This proves they are offered something to do instead, that it is real
/// Gemini content with real questions, and that it says plainly it does not
/// count.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('an empty skill offers practice that changes nothing',
      (tester) async {
    await bootFreshLearner(tester, prefix: 'practice');

    // Straight to Listening without adding a word: nothing is due there.
    await openSkill(tester, 'Listening');
    await settle(tester, total: const Duration(seconds: 30));

    expect(visibleText(tester).any((t) => t.contains('will not change any')),
        isTrue,
        reason: 'the offer should say what practice does not do. '
            'Visible: ${visibleText(tester).take(8)}');

    await tapAny(tester, ['Practise anyway']);
    await waitFor(
        tester,
        () => visibleText(tester).any((t) => t.contains('Practice only')),
        total: const Duration(seconds: 120));

    // Real content: a generated script, and comprehension questions about it.
    expect(visibleText(tester).any((t) => t.contains('Practice')), isTrue);
    debugPrint('✓ PRACTICE · session running and labelled');

    await tapAny(tester, ['I finished listening']);
    await settle(tester, total: const Duration(seconds: 60));

    expect(find.textContaining('Question 1 of'), findsOneWidget,
        reason: 'practice still asks real comprehension questions. '
            'Visible: ${visibleText(tester).take(8)}');

    await backToHub(tester);

    // Reopening resumes the same practice session rather than spending another
    // Gemini call — and it is still labelled practice, so a learner who comes
    // back to it is not misled into thinking it now counts. That practice
    // leaves the pipeline untouched is proven exhaustively in the backend
    // tests, which can read the rows.
    await openSkill(tester, 'Listening');
    await settle(tester, total: const Duration(seconds: 30));
    expect(visibleText(tester).any((t) => t.contains('Practice only')), isTrue,
        reason: 'Visible: ${visibleText(tester).take(6)}');
    debugPrint('✓ PRACTICE · resumed, still labelled practice');
  }, timeout: const Timeout(Duration(minutes: 10)));
}
