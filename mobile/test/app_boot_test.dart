import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'support/test_harness.dart';

void main() {
  testWidgets('the app boots to the sign-in screen', (tester) async {
    await bootApp(tester);

    // Splash while the stored session is checked.
    expect(find.byType(CircularProgressIndicator), findsOneWidget);

    await tester.pumpAndSettle();

    expect(find.text('Welcome back'), findsOneWidget);
    expect(find.widgetWithText(FilledButton, 'Sign in'), findsOneWidget);
  });

  testWidgets('signing in with the demo account reaches the Skills Hub',
      (tester) async {
    await bootApp(tester);
    await tester.pumpAndSettle();

    await tester.tap(find.widgetWithText(FilledButton, 'Sign in'));
    await tester.pumpAndSettle();

    expect(find.text('Skills Hub'), findsWidgets);
    expect(find.text('Words added today'), findsOneWidget);
    expect(find.text('Reading'), findsOneWidget);

    // The five skill cards live in a scrolling list.
    await tester.scrollUntilVisible(find.text('Spelling'), 200);
    expect(find.text('Spelling'), findsOneWidget);
  });
}
