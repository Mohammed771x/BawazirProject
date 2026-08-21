import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/core/storage/app_preferences.dart';
import 'package:wordos/core/storage/preferences_providers.dart';
import 'package:wordos/core/storage/token_store.dart';

/// Opening Spelling against the real backend — reported as stuck on loading.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  const email = String.fromEnvironment('WORDOS_TEST_EMAIL');
  const password = String.fromEnvironment('WORDOS_TEST_PASSWORD');

  testWidgets('spelling opens instead of spinning', (tester) async {
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

    await tester.tap(find.text('Spelling').first);
    await _settle(tester);

    debugPrint('VISIBLE: ${_visible(tester).take(14)}');
    final thrown = tester.takeException();
    debugPrint('THROWN: $thrown');
    expect(thrown, isNull, reason: 'spelling threw while opening');
    expect(find.byType(CircularProgressIndicator), findsNothing,
        reason: 'still loading after 45 seconds');
  });
}

Future<void> _settle(WidgetTester tester,
    {Duration total = const Duration(seconds: 45)}) async {
  final deadline = DateTime.now().add(total);
  while (DateTime.now().isBefore(deadline)) {
    await tester.pump(const Duration(milliseconds: 250));
    if (find.byType(CircularProgressIndicator).evaluate().isEmpty) {
      await tester.pump(const Duration(milliseconds: 250));
      return;
    }
  }
}

List<String> _visible(WidgetTester tester) => tester
    .widgetList<Text>(find.byType(Text))
    .map((t) => t.data ?? '')
    .where((t) => t.isNotEmpty)
    .toList();

class _MemoryTokenStore extends TokenStore {
  String? _t;
  String? _r;
  @override
  String? get token => _t;
  @override
  String? get refreshToken => _r;
  @override
  Future<String?> restore() async => _t;
  @override
  Future<void> save(String token, {String? refreshToken}) async {
    _t = token;
    if (refreshToken != null) _r = refreshToken;
  }
  @override
  Future<void> clear() async {
    _t = null;
    _r = null;
  }
}
