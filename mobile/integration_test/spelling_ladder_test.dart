import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/storage/app_preferences.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/core/storage/preferences_providers.dart';
import 'package:wordos/core/storage/token_store.dart';

/// The spelling hint ladder, through the real UI against the real stack
/// (Part 2 §38–§40).
///
/// The rungs themselves are the backend's decision, so what this proves is the
/// half that unit tests cannot: that the ladder survives the wire, that a press
/// reveals exactly one more rung, and that the learner is never handed the
/// whole thing at once.
///
/// The account is passed in rather than created, because reaching Spelling
/// means passing four skills with two-day gaps between them. Credentials are
/// never written into the source (`--dart-define`), and the run needs a word
/// already due for spelling on that account.
///
/// ```
/// flutter test integration_test/spelling_ladder_test.dart \
///   -d <device> --dart-define=WORDOS_MOCK=false \
///   --dart-define=WORDOS_API_BASE_URL=http://127.0.0.1:5199/api \
///   --dart-define=WORDOS_TEST_EMAIL=… --dart-define=WORDOS_TEST_PASSWORD=…
/// ```
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  const email = String.fromEnvironment('WORDOS_TEST_EMAIL');
  const password = String.fromEnvironment('WORDOS_TEST_PASSWORD');

  testWidgets('a hint press reveals exactly one more rung', (tester) async {
    expect(email.isNotEmpty && password.isNotEmpty, isTrue,
        reason: 'pass WORDOS_TEST_EMAIL and WORDOS_TEST_PASSWORD '
            '(email=${email.length} password=${password.length})');

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          // English only so the assertions can be written against the English
          // strings; the API and the tokens are real.
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

    // The task opens with one clue and nothing else. Everything below it has
    // to be asked for.
    expect(find.text('Something easier').evaluate(), isEmpty,
        reason: 'the ladder has not been started yet');
    expect(find.text('Need a hint?'), findsOneWidget,
        reason: 'a spelling task always offers help. '
            'Visible: ${_visibleText(tester)}');

    // Each press adds exactly one rung, and each rung is labelled by what kind
    // of help it is — so the learner can see the help getting easier.
    const rungLabels = [
      'Definition',
      'In simpler words',
      'Similar word',
      'Meaning',
      'Number of letters',
    ];

    var shown = _rungsOnScreen(tester, rungLabels);
    await tester.tap(find.text('Need a hint?').first);
    await _settle(tester);
    expect(_rungsOnScreen(tester, rungLabels), shown + 1,
        reason: 'the first press reveals one rung');

    var presses = 1;
    while (find.text('Something easier').evaluate().isNotEmpty) {
      shown = _rungsOnScreen(tester, rungLabels);
      await tester.tap(find.text('Something easier').first);
      await _settle(tester);
      presses++;

      expect(_rungsOnScreen(tester, rungLabels), shown + 1,
          reason: 'press $presses revealed more than one rung, or none');
      expect(presses, lessThan(6), reason: 'the ladder has five rungs at most');
    }

    // The bottom of the ladder is the number of letters — the last resort, and
    // the only rung about the spelling rather than the meaning.
    expect(find.text('Number of letters'), findsOneWidget,
        reason: 'the ladder ends at the letter count. '
            'Visible: ${_visibleText(tester)}');
    debugPrint('✓ SPELLING · ladder walked in $presses presses');
  });
}

/// How many rungs are currently on screen, counted by their labels.
int _rungsOnScreen(WidgetTester tester, List<String> labels) => labels
    .map((l) => find.text(l).evaluate().length)
    .fold(0, (sum, count) => sum + count);

/// `pumpAndSettle` gives up on a screen with a running animation, so time is
/// advanced in slices instead.
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
