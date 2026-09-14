import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/api/api_providers.dart';

import 'support/test_harness.dart';

/// Recovering a forgotten password from the sign-in screen (ADR-078).
///
/// The rules being checked belong to the backend — this suite runs against the
/// mock, which simulates them. What these tests own is the half the server
/// cannot: that the screen is reachable by someone who is locked out, that it
/// never says more than the server does, and that a learner ends up able to
/// sign in again.
void main() {
  /// The code, read the way the mock publishes it — standing in for an inbox.
  String codeFor(WidgetTester tester, String email) {
    final container = ProviderScope.containerOf(
      tester.element(find.byType(WordOsApp)),
    );
    final code = container.read(mockApiProvider)!.engine
        .outstandingResetCodeFor(email);
    expect(code, isNotNull, reason: 'no reset code was issued');
    return code!;
  }

  Future<void> openResetScreen(WidgetTester tester) async {
    await bootApp(tester, surfaceSize: const Size(1200, 2600));
    await tester.pumpAndSettle();

    await tester.tap(find.widgetWithText(TextButton, 'Forgot your password?'));
    await tester.pumpAndSettle();
  }

  testWidgets('the sign-in screen offers a way out of being locked out',
      (tester) async {
    await bootApp(tester, surfaceSize: const Size(1200, 2600));
    await tester.pumpAndSettle();

    // The whole feature starts with this link existing where a refused learner
    // is already looking.
    expect(find.widgetWithText(TextButton, 'Forgot your password?'),
        findsOneWidget);
  });

  testWidgets('a learner who forgot their password can set a new one',
      (tester) async {
    await openResetScreen(tester);
    expect(find.text('Reset your password'), findsOneWidget);

    await tester.enterText(
        find.byType(TextFormField).first, 'demo@wordos.app');
    await tester.tap(find.widgetWithText(FilledButton, 'Send code'));
    await tester.pumpAndSettle();

    final code = codeFor(tester, 'demo@wordos.app');

    final fields = find.byType(TextFormField);
    await tester.enterText(fields.at(0), code);
    await tester.enterText(fields.at(1), 'a-brand-new-password');
    await tester.enterText(fields.at(2), 'a-brand-new-password');
    await tester.tap(find.widgetWithText(FilledButton, 'Set new password'));
    await tester.pumpAndSettle();

    // Back at sign-in, not signed in: the reset hands back no session.
    expect(find.text('Welcome back'), findsOneWidget);

    // And the new password actually works. The demo account's prefilled
    // password is the old one, so it has to be replaced here.
    await tester.enterText(
        find.byType(TextFormField).at(1), 'a-brand-new-password');
    await tester.tap(find.widgetWithText(FilledButton, 'Sign in'));
    await tester.pumpAndSettle();

    expect(find.text('Skills Hub'), findsWidgets);
  });

  testWidgets('an unknown address is told exactly what a known one is told',
      (tester) async {
    await openResetScreen(tester);

    await tester.enterText(
        find.byType(TextFormField).first, 'nobody@nowhere.test');
    await tester.tap(find.widgetWithText(FilledButton, 'Send code'));
    await tester.pumpAndSettle();

    // It moves to the code step and says "if that email is registered" — the
    // client half of the server's refusal to confirm who has an account. An
    // honest "no such account" here would undo all of it.
    expect(
      find.textContaining('If that email is registered'),
      findsOneWidget,
    );
    expect(find.widgetWithText(FilledButton, 'Set new password'),
        findsOneWidget);
  });

  testWidgets('a wrong code is refused without saying which part was wrong',
      (tester) async {
    await openResetScreen(tester);

    await tester.enterText(
        find.byType(TextFormField).first, 'demo@wordos.app');
    await tester.tap(find.widgetWithText(FilledButton, 'Send code'));
    await tester.pumpAndSettle();

    final real = codeFor(tester, 'demo@wordos.app');
    final wrong = real[0] == '0' ? '1${real.substring(1)}' : '0${real.substring(1)}';

    final fields = find.byType(TextFormField);
    await tester.enterText(fields.at(0), wrong);
    await tester.enterText(fields.at(1), 'a-brand-new-password');
    await tester.enterText(fields.at(2), 'a-brand-new-password');
    await tester.tap(find.widgetWithText(FilledButton, 'Set new password'));
    await tester.pumpAndSettle();

    expect(
      find.textContaining('wrong or has expired'),
      findsOneWidget,
    );
  });

  testWidgets('a mistyped confirmation is caught before the server sees it',
      (tester) async {
    await openResetScreen(tester);

    await tester.enterText(
        find.byType(TextFormField).first, 'demo@wordos.app');
    await tester.tap(find.widgetWithText(FilledButton, 'Send code'));
    await tester.pumpAndSettle();

    final code = codeFor(tester, 'demo@wordos.app');

    final fields = find.byType(TextFormField);
    await tester.enterText(fields.at(0), code);
    await tester.enterText(fields.at(1), 'a-brand-new-password');
    await tester.enterText(fields.at(2), 'a-brand-new-passwerd');
    await tester.tap(find.widgetWithText(FilledButton, 'Set new password'));
    await tester.pumpAndSettle();

    // The server cannot catch this — it only ever sees one password. A learner
    // who mistypes one they cannot read would otherwise be locked out by their
    // own reset, holding a code that is now spent.
    expect(find.text('The two passwords do not match'), findsOneWidget);
    expect(find.text('Welcome back'), findsNothing);
  });

  testWidgets('the reset screen is reachable while signed out', (tester) async {
    await openResetScreen(tester);

    // The router sends a signed-out learner to /login for every other route.
    // If this one were not exempt, the only people who can reach the recovery
    // screen would be the ones who do not need it.
    expect(find.text('Reset your password'), findsOneWidget);
    expect(find.text('Welcome back'), findsNothing);
  });
}
