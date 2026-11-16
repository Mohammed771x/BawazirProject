import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/mock_backend/engine/mock_engine.dart';

import 'support/test_harness.dart';

/// Deleting the account (ADR-140) and confirming the email address (ADR-141),
/// walked through the real screens against the mock backend.
void main() {
  MockEngine engineOf(WidgetTester tester) => ProviderScope.containerOf(
        tester.element(find.byType(WordOsApp)),
      ).read(mockApiProvider)!.engine;

  Future<void> openSettings(WidgetTester tester) async {
    await tester.tap(find.text('Settings').last);
    await tester.pumpAndSettle();
  }

  Future<void> openDeleteDialog(WidgetTester tester) async {
    final card = find.text('Delete account');
    await tester.scrollUntilVisible(card, 300,
        scrollable: find.byType(Scrollable).first);
    await tester.pumpAndSettle();
    await tester.tap(card);
    await tester.pumpAndSettle();
    expect(find.text('Delete your account?'), findsOneWidget);
  }

  Finder dialogPassword() => find.descendant(
      of: find.byType(AlertDialog), matching: find.byType(TextField));

  group('deleting the account', () {
    testWidgets('a wrong password is refused and the account stays',
        (tester) async {
      await bootAndSignIn(tester);
      await openSettings(tester);
      await openDeleteDialog(tester);

      // It says what goes before anything goes.
      expect(find.textContaining('your words'), findsOneWidget);
      expect(find.textContaining('cannot be undone'), findsOneWidget);

      await tester.enterText(dialogPassword(), 'not-my-password');
      await tester.tap(find.widgetWithText(FilledButton, 'Delete permanently'));
      await tester.pumpAndSettle();

      expect(find.text('That password is not right.'), findsOneWidget);
      expect(find.text('Delete your account?'), findsOneWidget,
          reason: 'the dialog stays open for another try');

      await tester.tap(find.widgetWithText(TextButton, 'Cancel'));
      await tester.pumpAndSettle();
      // Still signed in, on Settings.
      expect(find.text('Welcome back'), findsNothing);
      expect(find.text('Delete account'), findsOneWidget);
    });

    testWidgets('the right password deletes it and signs the learner out',
        (tester) async {
      await bootAndSignIn(tester);
      await openSettings(tester);
      await openDeleteDialog(tester);

      await tester.enterText(dialogPassword(), 'wordos123');
      await tester.tap(find.widgetWithText(FilledButton, 'Delete permanently'));
      await tester.pumpAndSettle();

      expect(find.text('Welcome back'), findsOneWidget);
      expect(find.text('Your account has been deleted.'), findsOneWidget);

      // The account is gone, not merely signed out of.
      await tester.enterText(find.byType(TextFormField).at(0), 'demo@wordos.app');
      await tester.enterText(find.byType(TextFormField).at(1), 'wordos123');
      await tester.tap(find.widgetWithText(FilledButton, 'Sign in'));
      await tester.pumpAndSettle();
      expect(find.text('Skills Hub'), findsNothing);
      expect(find.text('Welcome back'), findsOneWidget);
    });

    testWidgets('an empty password is asked for, not sent', (tester) async {
      await bootAndSignIn(tester);
      await openSettings(tester);
      await openDeleteDialog(tester);

      await tester.tap(find.widgetWithText(FilledButton, 'Delete permanently'));
      await tester.pumpAndSettle();

      expect(find.text('Enter your password to confirm.'), findsWidgets);
      expect(find.text('Delete your account?'), findsOneWidget);
    });

    testWidgets('it reads right-to-left in Arabic', (tester) async {
      await bootApp(tester,
          locale: const Locale('ar'), surfaceSize: const Size(1200, 2600));
      await tester.pumpAndSettle();
      await tester.tap(find.byType(FilledButton).first);
      await tester.pumpAndSettle();
      await tester.tap(find.text('الإعدادات').last);
      await tester.pumpAndSettle();

      final card = find.text('حذف الحساب');
      await tester.scrollUntilVisible(card, 300,
          scrollable: find.byType(Scrollable).first);
      await tester.pumpAndSettle();
      expect(Directionality.of(tester.element(card)), TextDirection.rtl);
      await tester.tap(card);
      await tester.pumpAndSettle();
      expect(find.text('حذف حسابك نهائيًا؟'), findsOneWidget);
      expect(tester.takeException(), isNull);
    });
  });

  group('confirming the email address', () {
    Future<String> registerWithVerification(WidgetTester tester) async {
      await bootApp(tester, surfaceSize: const Size(1200, 2600));
      await tester.pumpAndSettle();
      engineOf(tester).requireEmailVerification = true;

      await tester.tap(find.text('Create account').first);
      await tester.pumpAndSettle();

      const email = 'new.learner@wordos.test';
      final fields = find.byType(TextFormField);
      await tester.enterText(fields.at(0), 'Layla');
      await tester.enterText(fields.at(1), email);
      await tester.enterText(fields.at(2), '777123456');
      await tester.enterText(fields.at(3), 'correct-horse-battery');
      await tester.tap(find.widgetWithText(FilledButton, 'Create account'));
      // Not pumpAndSettle: the resend countdown ticks every second, and
      // settling would run it out before the test can look at it.
      for (var i = 0; i < 4; i++) {
        await tester.pump(const Duration(milliseconds: 300));
      }
      return email;
    }

    testWidgets('a new account goes to the code, and the right code goes on',
        (tester) async {
      final email = await registerWithVerification(tester);

      expect(find.text('Confirm your email'), findsOneWidget);
      expect(find.textContaining(email), findsWidgets);

      // "Send another" waits: a code went out a moment ago.
      final resend = find.textContaining('Send another code in');
      expect(resend, findsOneWidget);
      expect(
          tester.widget<TextButton>(find.ancestor(
              of: resend, matching: find.byType(TextButton))).onPressed,
          isNull);

      final wrong =
          engineOf(tester).outstandingEmailCodeFor(email) == '000000' ? '111111' : '000000';
      await tester.enterText(find.byType(TextField).first, wrong);
      await tester.tap(find.widgetWithText(FilledButton, 'Confirm'));
      await tester.pumpAndSettle();
      expect(find.text('That code is not right. Check it and try again.'),
          findsOneWidget);

      final code = engineOf(tester).outstandingEmailCodeFor(email)!;
      await tester.enterText(find.byType(TextField).first, code);
      await tester.tap(find.widgetWithText(FilledButton, 'Confirm'));
      await tester.pumpAndSettle();

      expect(find.text('Confirm your email'), findsNothing);
      expect(find.textContaining('interest'), findsWidgets,
          reason: 'onboarding starts once the address is proved');
    });

    testWidgets('a learner who typed the wrong address can sign out of it',
        (tester) async {
      await registerWithVerification(tester);
      expect(find.text('Confirm your email'), findsOneWidget);

      await tester.tap(find.text('Wrong email? Sign out and register again'));
      await tester.pumpAndSettle();
      expect(find.text('Welcome back'), findsOneWidget);
    });

    testWidgets('without the setting, registering goes straight to interests',
        (tester) async {
      await bootApp(tester, surfaceSize: const Size(1200, 2600));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Create account').first);
      await tester.pumpAndSettle();
      final fields = find.byType(TextFormField);
      await tester.enterText(fields.at(0), 'Omar');
      await tester.enterText(fields.at(1), 'omar@wordos.test');
      await tester.enterText(fields.at(2), '777123456');
      await tester.enterText(fields.at(3), 'correct-horse-battery');
      await tester.tap(find.widgetWithText(FilledButton, 'Create account'));
      await tester.pumpAndSettle();

      expect(find.text('Confirm your email'), findsNothing);
      expect(find.textContaining('interest'), findsWidgets);
    });
  });
}
