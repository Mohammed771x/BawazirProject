import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/storage/preferences_providers.dart';
import 'package:wordos/core/tour/tour_overlay.dart';

import 'support/test_harness.dart';

/// The guided tour (ADR-128): shown once, skippable, and replayable.
///
/// The overlay's ring pulses for as long as it is up, so these tests pump by
/// time while it is on screen — `pumpAndSettle` would wait for ever.
void main() {
  const surface = Size(1200, 2600);

  /// Lets the trigger's settle delay pass and the overlay animate in.
  Future<void> letTourIn(WidgetTester tester) async {
    for (var i = 0; i < 16; i++) {
      await tester.pump(const Duration(milliseconds: 250));
    }
  }

  Future<void> tapAndWait(WidgetTester tester, Finder target) async {
    await tester.tap(target);
    await letTourIn(tester);
  }

  testWidgets('a first-time learner is walked through the hub, once',
      (tester) async {
    await bootApp(tester, surfaceSize: surface, toursSeen: false);
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Sign in'));
    await letTourIn(tester);

    expect(find.byType(TourOverlay), findsOneWidget);
    expect(find.textContaining('Welcome'), findsOneWidget);

    // Next moves to the first lit element.
    await tapAndWait(tester, find.widgetWithText(FilledButton, 'Next'));
    expect(find.text('How many new words you have added today.'),
        findsOneWidget);

    // Skip ends it, and it stays ended.
    await tapAndWait(tester, find.text('Skip'));
    await tester.pumpAndSettle();
    expect(find.byType(TourOverlay), findsNothing);

    final prefs = ProviderScope.containerOf(
      tester.element(find.byType(WordOsApp)),
    ).read(appPreferencesProvider);
    expect(prefs.tourSeen('hub'), isTrue);

    // Away and back: the hub does not explain itself a second time.
    await tester.tap(find.text('My words').last);
    // My Words has its own tour; close it to get back to the bar.
    await letTourIn(tester);
    if (find.byType(TourOverlay).evaluate().isNotEmpty) {
      await tapAndWait(tester, find.text('Skip'));
      await tester.pumpAndSettle();
    }
    await tester.tap(find.text('Skills Hub').last);
    await letTourIn(tester);
    expect(find.byType(TourOverlay), findsNothing);
  });

  testWidgets('placement says it is required and not the final level',
      (tester) async {
    await bootApp(tester, surfaceSize: surface, toursSeen: false);
    await tester.pumpAndSettle();

    final create = find.widgetWithText(TextButton, 'Create account');
    await tester.ensureVisible(create);
    await tester.tap(create);
    await tester.pumpAndSettle();

    final fields = find.byType(TextFormField);
    await tester.enterText(fields.at(0), 'Tour Tester');
    await tester.enterText(fields.at(1), 'tour@wordos.test');
    await tester.enterText(fields.at(2), '770112233');
    await tester.enterText(fields.last, 'wordos123');
    final submit = find.widgetWithText(FilledButton, 'Create account');
    await tester.ensureVisible(submit);
    await tester.tap(submit);
    await tester.pumpAndSettle();

    await tester.tap(find.textContaining('Technology').first,
        warnIfMissed: false);
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Continue'));
    await letTourIn(tester);

    expect(find.text('Next: a short placement test'), findsOneWidget);
    await tapAndWait(tester, find.widgetWithText(FilledButton, 'Next'));
    expect(find.text('This is not your final level'), findsOneWidget);
    expect(find.textContaining('from Settings'), findsOneWidget);

    // The last step lights the real Start button.
    await tapAndWait(tester, find.widgetWithText(FilledButton, 'Next'));
    expect(find.text('Ready?'), findsOneWidget);
    await tapAndWait(tester, find.widgetWithText(FilledButton, 'Got it'));
    await tester.pumpAndSettle();
    expect(find.byType(TourOverlay), findsNothing);
  });

  testWidgets('Settings replays the tour from the hub', (tester) async {
    await bootAndSignIn(tester);

    await tester.tap(find.text('Settings').last);
    await tester.pumpAndSettle();
    final replay = find.text('Replay the app tour');
    await tester.scrollUntilVisible(replay, 400);
    await tester.tap(replay);
    await letTourIn(tester);

    expect(find.byType(TourOverlay), findsOneWidget);
    expect(find.textContaining('Welcome'), findsOneWidget);

    await tapAndWait(tester, find.text('Skip'));
    await tester.pumpAndSettle();
    expect(find.byType(TourOverlay), findsNothing);
  });

  testWidgets('the first session of a skill says how it works',
      (tester) async {
    await bootAndSignIn(tester, toursSeen: false);
    await letTourIn(tester);
    // The hub's own tour comes first; this test is about the session's.
    await tapAndWait(tester, find.text('Skip'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Reading').first);
    await letTourIn(tester);

    expect(find.text('How Reading works'), findsOneWidget);
    // The passage's own steps follow the introduction in the same run.
    await tapAndWait(tester, find.widgetWithText(FilledButton, 'Next'));
    await tapAndWait(tester, find.widgetWithText(FilledButton, 'Next'));
    expect(find.text('Your passage'), findsOneWidget);
    await tapAndWait(tester, find.text('Skip'));
    await tester.pumpAndSettle();
    expect(find.byType(TourOverlay), findsNothing);

    // The questions explain themselves when they first appear — including
    // the way back to the passage.
    await tester.tap(find.text('I finished reading'));
    await letTourIn(tester);
    expect(find.text('Question by question'), findsOneWidget);
    var steps = 0;
    while (find.text('Back to the text, any time').evaluate().isEmpty &&
        steps++ < 4) {
      await tapAndWait(tester, find.widgetWithText(FilledButton, 'Next'));
    }
    expect(find.text('Back to the text, any time'), findsOneWidget);
    await tapAndWait(tester, find.text('Skip'));
    await tester.pumpAndSettle();
    expect(find.byType(TourOverlay), findsNothing);
  });

  testWidgets('Writing shows where the task and the answer go',
      (tester) async {
    await bootAndSignIn(tester, toursSeen: false);
    await letTourIn(tester);
    await tapAndWait(tester, find.text('Skip'));
    await tester.pumpAndSettle();

    final writing = find.text('Writing').first;
    await tester.ensureVisible(writing);
    await tester.tap(writing);
    await letTourIn(tester);

    expect(find.text('How Writing works'), findsOneWidget);
    var steps = 0;
    while (find.text('Write it here').evaluate().isEmpty && steps++ < 5) {
      await tapAndWait(tester, find.widgetWithText(FilledButton, 'Next'));
    }
    expect(find.text('Write it here'), findsOneWidget);
    await tapAndWait(tester, find.text('Skip'));
    await tester.pumpAndSettle();
    expect(find.byType(TourOverlay), findsNothing);
  });
}
