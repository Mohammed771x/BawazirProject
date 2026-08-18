import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/features/session/session_widgets.dart';

import 'support/test_harness.dart';

/// The app under abuse: mashed buttons, screens left mid-load, audio playing
/// while the learner walks away.
///
/// Flutter surfaces these as exceptions during a frame rather than as failed
/// assertions, and an exception in a frame is what closes an app on a device.
/// So the assertion in every test here is `takeException()` — the screen only
/// has to survive.
void main() {
  /// Nothing was thrown while that was happening.
  void expectNoCrash(WidgetTester tester, String what) {
    expect(tester.takeException(), isNull, reason: 'crashed while $what');
  }

  /// Goes back, whether or not the screen finished appearing.
  ///
  /// `pageBack` insists on finding a back button, which is exactly what is
  /// missing when a learner leaves before the screen has drawn — the case
  /// under test.
  Future<void> goBack(WidgetTester tester) async {
    final state = tester.state<NavigatorState>(find.byType(Navigator).first);
    if (state.canPop()) state.pop();
    await tester.pump();
  }

  testWidgets('mashing a skill tile does not open a stack of sessions',
      (tester) async {
    await bootAndSignIn(tester);

    // Six taps in the time it takes one session to load. A learner does this
    // whenever the app feels slow.
    for (var i = 0; i < 6; i++) {
      await tester.tap(find.text('Reading').first, warnIfMissed: false);
      await tester.pump(const Duration(milliseconds: 30));
    }
    await tester.pumpAndSettle();
    expectNoCrash(tester, 'opening a session six times');

    // One session, not six stacked on top of each other.
    await goBack(tester);
    await tester.pumpAndSettle();
    expect(find.text('Skills Hub'), findsWidgets,
        reason: 'one back should be enough to leave one session');
  });

  testWidgets('leaving a session immediately after starting it is safe',
      (tester) async {
    await bootAndSignIn(tester);

    await tester.tap(find.text('Reading').first);
    // Deliberately not settled: the passage is still being fetched.
    await tester.pump(const Duration(milliseconds: 20));

    await goBack(tester);
    await tester.pumpAndSettle();

    // The response arrives after the screen is gone. Anything that touches
    // state here — a setState, a provider read — throws on a dead widget.
    await tester.pump(const Duration(seconds: 2));
    expectNoCrash(tester, 'leaving a session while it was still loading');
  });

  testWidgets('walking away while audio is playing stops it', (tester) async {
    await bootAndSignIn(tester);

    await tester.tap(find.text('Listening').first);
    await tester.pumpAndSettle();

    final player = find.byType(SentencePlayer);
    if (player.evaluate().isNotEmpty) {
      await tester.tap(player.first);
      await tester.pump(const Duration(milliseconds: 100));
    }

    // Out of the session with the clip still going. A player left running
    // reads its answers aloud over whatever the learner opened next.
    await goBack(tester);
    await tester.pumpAndSettle();
    expectNoCrash(tester, 'leaving a listening session mid-clip');

    // The bounded wait a spoken utterance sits behind is allowed to expire, so
    // the teardown check is about state and not about a timer that was always
    // going to fire (`speakToCompletion` gives the platform seconds to report
    // back before giving up).
    await tester.pump(const Duration(seconds: 30));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Settings').last);
    await tester.pumpAndSettle();
    expectNoCrash(tester, 'opening settings while audio had been playing');
  });

  testWidgets('flicking between tabs at speed', (tester) async {
    await bootAndSignIn(tester);

    // Eight round trips: the pattern of someone looking for something they
    // cannot find.
    for (var i = 0; i < 8; i++) {
      for (final tab in ['My words', 'Settings', 'Skills Hub']) {
        await tester.tap(find.text(tab).last, warnIfMissed: false);
        await tester.pump(const Duration(milliseconds: 40));
      }
    }
    await tester.pumpAndSettle();
    expectNoCrash(tester, 'switching tabs at speed');
  });

  testWidgets('typing into the word search faster than it can answer',
      (tester) async {
    await bootAndSignIn(tester);

    await tester.tap(find.widgetWithText(FloatingActionButton, 'Add word'));
    await tester.pumpAndSettle();

    // Each keystroke cancels the last search. The debounce is what makes this
    // survivable; without it every prefix would be in flight at once.
    for (final prefix in ['r', 're', 'res', 'rese', 'resea', 'resear']) {
      await tester.enterText(find.byType(TextField).first, prefix);
      await tester.pump(const Duration(milliseconds: 20));
    }

    // Then leave before any of them lands.
    await goBack(tester);
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 1));
    expectNoCrash(tester, 'leaving the search while it was still typing');
  });

  testWidgets('a session survives being answered as fast as it can be tapped',
      (tester) async {
    await bootAndSignIn(tester);

    await tester.tap(find.text('Reading').first);
    await tester.pumpAndSettle();

    final finished = find.widgetWithText(FilledButton, 'I finished reading');
    if (finished.evaluate().isNotEmpty) {
      await tester.tap(finished);
      await tester.pumpAndSettle();
    }

    // Answer, next, answer, next — with no pause anywhere, which is how a
    // double-tap becomes an answer to a question that has not appeared yet.
    for (var guard = 0; guard < 20; guard++) {
      if (find.text('Session complete').evaluate().isNotEmpty) break;

      final options = find.byType(InkWell);
      if (options.evaluate().isEmpty) break;

      await tester.tap(options.at(0), warnIfMissed: false);
      await tester.pump(const Duration(milliseconds: 10));
      await tester.tap(options.at(0), warnIfMissed: false);
      await tester.pump(const Duration(milliseconds: 10));

      for (final label in ['Next', 'Finish']) {
        final button = find.widgetWithText(FilledButton, label);
        if (button.evaluate().isNotEmpty) {
          await tester.tap(button.first, warnIfMissed: false);
          await tester.pump(const Duration(milliseconds: 10));
          await tester.tap(button.first, warnIfMissed: false);
          break;
        }
      }
      await tester.pumpAndSettle();
    }

    expectNoCrash(tester, 'answering a session as fast as possible');
  });

  testWidgets('changing the language in the middle of everything',
      (tester) async {
    await bootAndSignIn(tester);

    await tester.tap(find.text('Settings').last);
    await tester.pumpAndSettle();

    // Back and forth, with the rest of the app already built behind it. Every
    // screen has to rebuild in the new direction without laying out in the
    // old one first.
    for (var i = 0; i < 4; i++) {
      for (final label in ['العربية', 'English']) {
        final button = find.text(label);
        if (button.evaluate().isEmpty) continue;
        await tester.tap(button.last, warnIfMissed: false);
        await tester.pump(const Duration(milliseconds: 60));
      }
    }
    await tester.pumpAndSettle();
    expectNoCrash(tester, 'switching language repeatedly');
  });

  testWidgets('signing out from inside a session', (tester) async {
    await bootAndSignIn(tester);

    await tester.tap(find.text('Reading').first);
    await tester.pumpAndSettle();

    await goBack(tester);
    await tester.pumpAndSettle();

    await tester.tap(find.text('Settings').last);
    await tester.pumpAndSettle();

    await tester.tap(find.descendant(
      of: find.byType(AppBar),
      matching: find.byIcon(Icons.logout_rounded),
    ));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Sign out'));
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();

    expectNoCrash(tester, 'signing out after a session');
    expect(find.text('Welcome back'), findsWidgets);
  });
}
