import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

import 'support/journey.dart';

/// Signing out, against the real API.
///
/// The widget tests pass against the mock and the device disagrees, which
/// points at the one thing the mock cannot reproduce: a real network call.
/// Everything here is therefore driven through the real backend.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('sign out ends the session and returns to sign-in',
      (tester) async {
    await bootFreshLearner(tester, prefix: 'signout');
    expect(isOnHub(tester), isTrue);

    await tapAny(tester, ['Settings']);
    await settle(tester, total: const Duration(seconds: 30));

    await tester.tap(find.descendant(
      of: find.byType(AppBar),
      matching: find.byIcon(Icons.logout_rounded),
    ));
    await settle(tester);

    // The dialog's title and its confirm button carry the same words, so the
    // button is targeted by type rather than by text.
    expect(visibleText(tester).any((t) => t.contains('sign in again')), isTrue,
        reason: 'the confirmation should be open. '
            'Visible: ${visibleText(tester).take(8)}');

    await tester.tap(find.widgetWithText(FilledButton, 'Sign out'));

    // The learner should be out *immediately*. Waiting on the server first
    // means a slow or unreachable network leaves the button looking dead —
    // which is exactly what was reported from the device.
    await tester.pump(const Duration(milliseconds: 600));
    await tester.pump(const Duration(milliseconds: 600));

    final quick = visibleText(tester);
    debugPrint('600ms after tapping: ${quick.take(6)}');

    await waitFor(
        tester,
        () => visibleText(tester).any((t) => t.contains('Welcome back')),
        total: const Duration(seconds: 20));

    expect(visibleText(tester).any((t) => t.contains('Welcome back')), isTrue,
        reason: 'sign out must return to the sign-in screen. '
            'Visible: ${visibleText(tester).take(10)}');
    debugPrint('✓ signed out against the real API');
  }, timeout: const Timeout(Duration(minutes: 8)));
}
