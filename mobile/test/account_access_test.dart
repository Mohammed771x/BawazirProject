import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/storage/app_preferences.dart';
import 'package:wordos/core/storage/preferences_providers.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/features/auth/session_controller.dart';

import 'support/test_harness.dart';

/// Getting in and out of an account.
///
/// Both of these were reported from the device: sign out could not be found,
/// and the link back to sign-in did nothing at all — which between them made
/// switching accounts impossible without reinstalling.
void main() {
  testWidgets('sign out is reachable without scrolling for it', (tester) async {
    await bootAndSignIn(tester);

    await tester.tap(find.text('Settings').last);
    await tester.pumpAndSettle();

    // In the toolbar, where a learner looks for it. At the foot of the list
    // alone it sat behind four sections, and a lazily-built list never even
    // constructed it — present in the code and absent in practice.
    expect(
      find.descendant(
        of: find.byType(AppBar),
        matching: find.byIcon(Icons.logout_rounded),
      ),
      findsOneWidget,
      reason: 'sign out should be reachable from the Settings toolbar',
    );
  });

  testWidgets('signing out asks first, then returns to sign-in',
      (tester) async {
    await bootAndSignIn(tester);
    await tester.tap(find.text('Settings').last);
    await tester.pumpAndSettle();

    await tester.tap(find.descendant(
      of: find.byType(AppBar),
      matching: find.byIcon(Icons.logout_rounded),
    ));
    await tester.pumpAndSettle();

    // One tap from a toolbar needs the question — an accidental tap otherwise
    // costs the learner their place and a re-login.
    expect(find.text('You will need to sign in again to continue learning.'),
        findsOneWidget);

    await tester.tap(find.widgetWithText(FilledButton, 'Sign out'));
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();

    expect(find.text('Welcome back'), findsWidgets,
        reason: 'sign out should land on the sign-in screen');
    expect(find.text('Skills Hub'), findsNothing,
        reason: 'a signed-out learner must not still see their hub');
  });

  test('signing out never sends the learner back through the product tour',
      () async {
    // The tour is shown once per installation, before there is an account to
    // attach the fact to. A device whose learner has not been through it — an
    // upgrade, a restored backup — must still land on the login form when they
    // sign out, not on slide one of a slide show.
    final prefs = InMemoryAppPreferences(onboardingSeen: false);
    final container = ProviderContainer(overrides: [
      appPreferencesProvider.overrideWithValue(prefs),
      tokenStoreProvider.overrideWith((ref) => FakeTokenStore()),
    ]);
    addTearDown(container.dispose);

    final session = container.read(sessionProvider.notifier);
    expect(await session.signIn('demo@wordos.app', 'wordos123'), isTrue);

    await session.signOut();

    expect(container.read(sessionProvider).isSignedIn, isFalse);
    expect(prefs.onboardingSeen, isTrue,
        reason: 'the router sends an unseen tour ahead of the login form, so '
            'signing out has to count as having seen it');
  });

  testWidgets('cancelling the confirmation keeps the learner signed in',
      (tester) async {
    await bootAndSignIn(tester);
    await tester.tap(find.text('Settings').last);
    await tester.pumpAndSettle();

    await tester.tap(find.descendant(
      of: find.byType(AppBar),
      matching: find.byIcon(Icons.logout_rounded),
    ));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(TextButton, 'Cancel'));
    await tester.pumpAndSettle();

    expect(find.text('Settings'), findsWidgets);
    expect(find.text('Welcome back'), findsNothing);
  });

  testWidgets('the sign-in link works even when nothing can be popped',
      (tester) async {
    // A learner arriving from the product tour lands on register with an empty
    // history. `context.pop()` alone did nothing there, which is exactly what
    // made the link look broken.
    await bootApp(tester);
    await tester.pumpAndSettle();

    await tester.tap(find.widgetWithText(TextButton, 'Create account'));
    await tester.pumpAndSettle();
    expect(find.text('Create your account'), findsWidgets);

    final link = find.widgetWithText(TextButton, 'Sign in');
    await tester.ensureVisible(link);
    await tester.pumpAndSettle();
    await tester.tap(link);
    await tester.pumpAndSettle();

    expect(find.text('Welcome back'), findsWidgets,
        reason: 'the link back to sign-in must always navigate');
  });
}
