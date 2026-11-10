import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:wordos/app/router.dart';

import 'support/test_harness.dart';

/// A dictionary result the learner already owns says so (ADR-136).
///
/// Before this it looked like any other result, and tapping it came back as
/// "Already in your words" — an error for something the learner did not do
/// wrong. The server marks the sense; the screen only shows it.
void main() {
  testWidgets('an owned sense reads "Added" and cannot be added again',
      (tester) async {
    await bootAndSignIn(tester);
    GoRouter.of(tester.element(find.byType(Scaffold).first))
        .push(Routes.addWord);
    await tester.pumpAndSettle();

    // The demo learner owns `hardware = عتاد`.
    await tester.enterText(find.byType(TextField).first, 'hardware');
    await tester.pump(const Duration(milliseconds: 300));
    await tester.pumpAndSettle();

    final added = find.text('Added');
    expect(added, findsOneWidget);

    await tester.tap(added);
    await tester.pumpAndSettle();

    expect(find.text('Already in your words'), findsNothing,
        reason: 'the tile is not tappable, so nothing was sent');
    expect(find.text('Added'), findsOneWidget,
        reason: 'still on the results, not on the "added" confirmation');
  });

  testWidgets('a sense the learner does not own is offered as before',
      (tester) async {
    await bootAndSignIn(tester);
    GoRouter.of(tester.element(find.byType(Scaffold).first))
        .push(Routes.addWord);
    await tester.pumpAndSettle();

    await tester.enterText(find.byType(TextField).first, 'book');
    await tester.pump(const Duration(milliseconds: 300));
    await tester.pumpAndSettle();

    expect(find.text('Added'), findsNothing);
  });
}
