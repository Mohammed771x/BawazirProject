import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/core/storage/app_preferences.dart';
import 'package:wordos/core/storage/preferences_providers.dart';
import 'package:wordos/core/storage/token_store.dart';

/// Searching for a word to add, through the real UI against the real lexicon.
///
/// The three cases a learner reported or would hit next: a function word that
/// WordNet does not carry (ADR-033), a word typed in the form they met it in,
/// and a word they only know in Arabic (ADR-034).
///
/// ```
/// flutter test integration_test/add_word_search_test.dart \
///   -d <device> --dart-define=WORDOS_MOCK=false \
///   --dart-define=WORDOS_API_BASE_URL=http://127.0.0.1:5199/api \
///   --dart-define=WORDOS_TEST_EMAIL=… --dart-define=WORDOS_TEST_PASSWORD=…
/// ```
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  const email = String.fromEnvironment('WORDOS_TEST_EMAIL');
  const password = String.fromEnvironment('WORDOS_TEST_PASSWORD');

  testWidgets('every kind of word can be found from the Add Word field',
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

    await tester.tap(find.widgetWithText(FloatingActionButton, 'Add word'));
    await _settle(tester);

    // A function word: absent from WordNet, so before ADR-033 this screen had
    // nothing to offer and the learner concluded the word was not English.
    await _search(tester, 'is');
    expect(_visibleText(tester), contains('is'),
        reason: 'the auxiliary "is" must be offered. '
            'Visible: ${_visibleText(tester).take(10)}');
    expect(_visibleText(tester).any((t) => t.contains('is not in the dictionary')),
        isFalse);

    // An irregular past tense: no rule turns it into its base form.
    await _search(tester, 'went');
    expect(_visibleText(tester), contains('go'),
        reason: '"went" should resolve to "go". '
            'Visible: ${_visibleText(tester).take(10)}');

    // Arabic in, English out.
    await _search(tester, 'مدرسة');
    expect(_visibleText(tester), contains('school'),
        reason: 'typing the Arabic meaning should offer the English word. '
            'Visible: ${_visibleText(tester).take(10)}');

    debugPrint('✓ ADD WORD · function words, irregulars and Arabic all resolve');
  });
}

Future<void> _search(WidgetTester tester, String query) async {
  await tester.enterText(find.byType(TextField).first, query);
  // The field debounces before it asks the server.
  await tester.pump(const Duration(milliseconds: 400));
  await _settle(tester);
}

Future<void> _settle(
  WidgetTester tester, {
  Duration total = const Duration(seconds: 30),
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
