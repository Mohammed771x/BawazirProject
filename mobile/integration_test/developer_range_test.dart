import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/core/storage/app_preferences.dart';
import 'package:wordos/core/storage/preferences_providers.dart';
import 'package:wordos/core/storage/token_store.dart';

/// The reported crash, driven against the real stack: the presets worked and a
/// hand-typed range closed the app.
///
/// Two faults met here. The dialog's text controller was disposed while the
/// dialog was still animating away, so the next frame read a dead controller;
/// and a large enough number overflowed the date arithmetic on the server,
/// which answered 500 and emptied the dashboard.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  const email = String.fromEnvironment('WORDOS_TEST_EMAIL');
  const password = String.fromEnvironment('WORDOS_TEST_PASSWORD');

  testWidgets('every custom reporting window leaves the dashboard standing',
      (tester) async {
    expect(email.isNotEmpty && password.isNotEmpty, isTrue,
        reason: 'pass WORDOS_TEST_EMAIL and WORDOS_TEST_PASSWORD');

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          appPreferencesProvider.overrideWithValue(
              InMemoryAppPreferences(locale: const Locale('en'))),
          tokenStoreProvider.overrideWith((ref) => _MemoryTokenStore()),
        ],
        child: const WordOsApp(),
      ),
    );
    await _settle(tester);

    final fields = find.byType(TextFormField);
    await tester.enterText(fields.at(0), email);
    await tester.enterText(fields.at(1), password);
    await tester.pump(const Duration(milliseconds: 100));
    await tester.tap(find.widgetWithText(FilledButton, 'Sign in').first);
    await _settle(tester);

    await tester.tap(find.text('Settings').last);
    await _settle(tester);
    await tester.scrollUntilVisible(find.text('Developer Dashboard'), 250);
    await tester.tap(find.text('Developer Dashboard'));
    await _settle(tester);

    for (final typed in ['3', '7', '365', '999999999', '0', 'abc']) {
      final custom = find.byType(ActionChip);
      await tester.ensureVisible(custom.first);
      await tester.tap(custom.first);
      await _settle(tester);

      final field = find.descendant(
        of: find.byType(AlertDialog),
        matching: find.byType(TextField),
      );
      await tester.enterText(field, typed);
      await tester.pumpAndSettle();

      final save = find.descendant(
        of: find.byType(AlertDialog),
        matching: find.widgetWithText(FilledButton, 'Save'),
      );
      final enabled = tester.widget<FilledButton>(save).onPressed != null;
      await tester.tap(enabled
          ? save
          : find.descendant(
              of: find.byType(AlertDialog),
              matching: find.widgetWithText(TextButton, 'Cancel'),
            ));
      await _settle(tester);

      expect(tester.takeException(), isNull,
          reason: '"$typed" crashed the dashboard');
      expect(find.text('Learners'), findsWidgets,
          reason: 'the overview disappeared after "$typed"');
    }

    debugPrint('✓ DEVELOPER · every custom range survives');
  });
}

Future<void> _settle(
  WidgetTester tester, {
  Duration total = const Duration(seconds: 45),
}) async {
  final deadline = DateTime.now().add(total);
  while (DateTime.now().isBefore(deadline)) {
    await tester.pump(const Duration(milliseconds: 250));
    if (find.byType(CircularProgressIndicator).evaluate().isEmpty) {
      await tester.pump(const Duration(milliseconds: 250));
      return;
    }
  }
}

class _MemoryTokenStore extends TokenStore {
  String? _token;
  String? _refresh;

  @override
  String? get token => _token;

  @override
  String? get refreshToken => _refresh;

  @override
  Future<String?> restore() async => _token;

  @override
  Future<void> save(String token, {String? refreshToken}) async {
    _token = token;
    if (refreshToken != null) _refresh = refreshToken;
  }

  @override
  Future<void> clear() async {
    _token = null;
    _refresh = null;
  }
}
