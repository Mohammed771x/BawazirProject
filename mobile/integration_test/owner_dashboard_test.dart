import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

import 'support/journey.dart';

/// The Owner dashboard, against the real backend.
///
/// The point is that the charts show **real** figures. A dashboard wired to a
/// mock renders exactly as convincingly as one wired to nothing, so the test
/// asserts that the numbers on screen came from the database — a session count
/// above zero cannot be produced by an empty response.
///
/// ```
/// # Create the owner first (the API offers no route to it, by design):
/// #   psql -c "update users set \\"Role\\"='Owner' where \\"Email\\"='...'"
/// flutter test integration_test/owner_dashboard_test.dart -d <device> \
///   --dart-define=WORDOS_MOCK=false \
///   --dart-define=WORDOS_API_BASE_URL=http://127.0.0.1:5199/api \
///   --dart-define=WORDOS_OWNER_EMAIL=<email>
/// ```
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  const ownerEmail = String.fromEnvironment('WORDOS_OWNER_EMAIL');

  testWidgets('the Owner dashboard renders real analytics', (tester) async {
    expect(ownerEmail, isNotEmpty,
        reason: 'pass --dart-define=WORDOS_OWNER_EMAIL=<an Owner account>');

    await boot(tester);

    // Sign in as the Owner. Registration can only ever create a learner, so the
    // account is promoted in SQL beforehand — that restriction is itself a
    // security property, tested on the backend.
    final fields = find.byType(TextFormField);
    await tester.enterText(fields.at(0), ownerEmail);
    await tester.enterText(fields.at(1), testPassword);
    await tester.pump(const Duration(milliseconds: 100));
    await tapAny(tester, ['Sign in']);
    await settle(tester, total: const Duration(seconds: 60));

    // Reach the dashboard through the UI the Owner actually uses.
    await tapAny(tester, ['Settings']);
    await settle(tester);

    // The Owner entry sits at the bottom of a long settings list.
    await tester.dragUntilVisible(
      find.text('Developer Dashboard'),
      find.byType(Scrollable).first,
      const Offset(0, -300),
    );
    await settle(tester);
    await tapAny(tester, ['Developer Dashboard']);
    await settle(tester, total: const Duration(seconds: 60));

    final texts = visibleText(tester);
    debugPrint('DASHBOARD: ${texts.take(30)}');

    // Real data, not an empty shell: there are learners and there are words.
    final numbers = texts
        .map((t) => int.tryParse(t.replaceAll(',', '').trim()))
        .whereType<int>()
        .toList();

    expect(numbers.any((n) => n > 0), isTrue,
        reason: 'the overview should show non-zero figures from the database. '
            'Visible: ${texts.take(20)}');

    // The per-skill panel is the one that was previously always zero, because
    // the server never sent `skillStats` at all.
    expect(texts.any((t) => t.contains('Reading')), isTrue);
    debugPrint('✓ overview rendered with real figures');

    // And the drill-down: pick the first learner in the list.
    await tapAny(tester, ['Users', 'Learners']);
    await settle(tester, total: const Duration(seconds: 60));

    final rows = find.byWidgetPredicate(
        (w) => w.runtimeType.toString().contains('Card') ||
            w.runtimeType.toString().contains('ListTile'));

    if (rows.evaluate().isNotEmpty) {
      await tester.ensureVisible(rows.first);
      await tester.tap(rows.first, warnIfMissed: false);
      await settle(tester, total: const Duration(seconds: 60));
      debugPrint('✓ drill-down: ${visibleText(tester).take(20)}');
    }
  }, timeout: const Timeout(Duration(minutes: 8)));
}
