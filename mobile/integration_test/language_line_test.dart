import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/core/storage/app_preferences.dart';
import 'package:wordos/core/storage/preferences_providers.dart';
import 'package:wordos/core/storage/token_store.dart';

/// Where the language line falls, on a real session from the real backend
/// (ADR-035): the instruction is Arabic, the word inside it is not.
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  const email = String.fromEnvironment('WORDOS_TEST_EMAIL');
  const password = String.fromEnvironment('WORDOS_TEST_PASSWORD');

  testWidgets('an Arabic app gives an Arabic instruction about an English word',
      (tester) async {
    expect(email.isNotEmpty && password.isNotEmpty, isTrue,
        reason: 'pass WORDOS_TEST_EMAIL and WORDOS_TEST_PASSWORD');

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          // Arabic, which is the product's default (ADR-010).
          appPreferencesProvider.overrideWithValue(
              InMemoryAppPreferences(locale: const Locale('ar'))),
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
    await tester.tap(find.widgetWithText(FilledButton, 'تسجيل الدخول').first);
    await _settle(tester);

    await tester.tap(find.text('الكتابة').first);
    await _settle(tester);

    final visible = _visibleText(tester);

    // The instruction is the app talking, so it is Arabic…
    expect(visible.any((t) => t.contains('اكتب جملة')), isTrue,
        reason: 'the writing instruction should be in Arabic. '
            'Visible: ${visible.take(10)}');

    // …and the word being practised is the material, so it is not translated.
    expect(visible.any((t) => t.contains('cancer')), isTrue,
        reason: 'the English word must survive inside the Arabic sentence');

    expect(visible.any((t) => t.contains('Write one sentence')), isFalse,
        reason: 'no English instruction should be left on screen');

    debugPrint('✓ LANGUAGE · Arabic instruction, English word');
  });
}

Future<void> _settle(
  WidgetTester tester, {
  Duration total = const Duration(seconds: 60),
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

List<String> _visibleText(WidgetTester tester) => tester
    .widgetList<Text>(find.byType(Text))
    .map((t) => t.data ?? '')
    .where((t) => t.isNotEmpty)
    .toList();

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
