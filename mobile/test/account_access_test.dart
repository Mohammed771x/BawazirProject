import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
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

  testWidgets('the support button hands WhatsApp the right link',
      (tester) async {
    // The plugin is not present in a test binary, so the platform channel is
    // answered here — which is also the only way to see *what* the app asks
    // the phone to open (ADR-055).
    final launched = <String>[];

    tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(
      const MethodChannel('plugins.flutter.io/url_launcher'),
      (call) async {
        if (call.method == 'launch' || call.method == 'launchUrl') {
          launched.add((call.arguments as Map)['url'] as String);
          return true;
        }
        if (call.method == 'canLaunch') return true;
        return null;
      },
    );

    addTearDown(() => tester.binding.defaultBinaryMessenger
        .setMockMethodCallHandler(
            const MethodChannel('plugins.flutter.io/url_launcher'), null));

    await bootAndSignIn(tester);

    await tester.tap(find.byIcon(Icons.settings_outlined));
    await tester.pumpAndSettle();

    final support = find.text('Contact support');

    // Dragged rather than scrollUntilVisible: the list disposes what it has
    // scrolled past, so settling after it comes into view can dispose it again
    // before the tap lands. Found and tapped in the same settled frame.
    for (var attempt = 0; attempt < 12 && support.evaluate().isEmpty; attempt++) {
      await tester.drag(find.byType(ListView).first, const Offset(0, -400));
      await tester.pumpAndSettle();
    }

    expect(support, findsOneWidget,
        reason: 'Settings should offer a way to reach support');

    await tester.tap(support);
    await tester.pumpAndSettle();

    expect(launched, ['https://wa.me/917558973719'],
        reason: 'one tap, straight to the chat — no dialog in between');
  });

  testWidgets('an account cannot be created without a phone number',
      (tester) async {
    // Required (ADR-054): an account the Owner cannot reach is one whose
    // learner cannot be helped when they report a problem — which is the whole
    // reason the number is collected.
    await bootApp(tester);
    await tester.pumpAndSettle();

    final create = find.widgetWithText(TextButton, 'Create account');
    await tester.ensureVisible(create);
    await tester.pumpAndSettle();
    await tester.tap(create);
    await tester.pumpAndSettle();

    final fields = find.byType(TextFormField);
    await tester.enterText(fields.at(0), 'Salem');
    await tester.enterText(fields.at(1), 'salem@wordos.test');
    // Phone deliberately left empty.
    await tester.enterText(fields.last, 'wordos123');

    final submit = find.widgetWithText(FilledButton, 'Create account');
    await tester.ensureVisible(submit);
    await tester.tap(submit);
    await tester.pumpAndSettle();

    // Stopped at the form, with the reason said in the learner's language —
    // not sent, refused by the server and shown as a generic failure.
    expect(find.text('Enter your phone number so we can reach you.'),
        findsOneWidget);
    expect(find.text('Create your account'), findsWidgets,
        reason: 'the learner should still be on the form');
  });

  testWidgets('punctuation is not a phone number', (tester) async {
    await bootApp(tester);
    await tester.pumpAndSettle();

    final create = find.widgetWithText(TextButton, 'Create account');
    await tester.ensureVisible(create);
    await tester.pumpAndSettle();
    await tester.tap(create);
    await tester.pumpAndSettle();

    final fields = find.byType(TextFormField);
    await tester.enterText(fields.at(0), 'Salem');
    await tester.enterText(fields.at(1), 'salem2@wordos.test');
    // The field permits these characters as separators; on their own they are
    // not a number, and the server refuses them for the same reason.
    //
    // Index 2: name, email, phone, password. The country code is a picker
    // rather than a field, and this was written as `at(3)` — the password box —
    // so the phone stayed empty and the test passed for the wrong reason.
    await tester.enterText(fields.at(2), '()- ');
    await tester.enterText(fields.last, 'wordos123');

    final submit = find.widgetWithText(FilledButton, 'Create account');
    await tester.ensureVisible(submit);
    await tester.tap(submit);
    await tester.pumpAndSettle();

    expect(find.text('Enter your phone number so we can reach you.'),
        findsOneWidget);
  });

  testWidgets('the sign-in link works even when nothing can be popped',
      (tester) async {
    // A learner arriving from the product tour lands on register with an empty
    // history. `context.pop()` alone did nothing there, which is exactly what
    // made the link look broken.
    await bootApp(tester);
    await tester.pumpAndSettle();

    // Scrolled to first: the link sits at the foot of the form, and anything
    // added above it — a notice, a banner — pushes it past the fold on a small
    // screen, where a tap lands on nothing.
    final create = find.widgetWithText(TextButton, 'Create account');
    await tester.ensureVisible(create);
    await tester.pumpAndSettle();
    await tester.tap(create);
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
