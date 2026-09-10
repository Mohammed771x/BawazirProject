import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/core/storage/app_preferences.dart';
import 'package:wordos/core/storage/preferences_providers.dart';
import 'package:wordos/core/storage/token_store.dart';

/// In-memory token store — widget tests must not touch the platform keystore.
class FakeTokenStore extends TokenStore {
  String? _value;
  String? _refresh;

  @override
  String? get token => _value;

  @override
  String? get refreshToken => _refresh;

  @override
  Future<String?> restore() async => _value;

  @override
  Future<void> save(String token, {String? refreshToken}) async {
    _value = token;
    if (refreshToken != null) _refresh = refreshToken;
  }

  @override
  Future<void> clear() async {
    _value = null;
    _refresh = null;
  }
}

/// Overrides every provider that would otherwise reach a platform channel —
/// or the network.
///
/// Tests pin the locale to English so assertions can be written against the
/// English strings; the Arabic default is asserted separately in
/// `localization_test.dart`.
///
/// The environment is pinned too, and that one is not a convenience. Since
/// `WORDOS_MOCK` began defaulting to `false` — right, for a device build —
/// every widget test that signed in was pointing at a real server that is not
/// running, and the whole suite failed on a `--dart-define` nobody had passed.
/// The failure did not say so: it said "Found 0 widgets with text 'Skills
/// Hub'", which reads as a UI regression and is not one.
///
/// A widget test must never depend on something outside the process being up,
/// so the choice is made here rather than left to how the runner was invoked.
List<Override> testOverrides({Locale locale = const Locale('en')}) => [
      appEnvironmentProvider.overrideWithValue(
        const AppEnvironment(useMockBackend: true, baseUrl: ''),
      ),
      appPreferencesProvider
          .overrideWithValue(InMemoryAppPreferences(locale: locale)),
      tokenStoreProvider.overrideWith((ref) => FakeTokenStore()),
    ];

/// Pumps the real app with test-safe dependencies.
Future<void> bootApp(
  WidgetTester tester, {
  Locale locale = const Locale('en'),
  Size? surfaceSize,
  /// Extra overrides, for a test that needs to watch what a service was asked
  /// to do — a fake voice engine, say.
  List<Override> overrides = const [],
}) async {
  if (surfaceSize != null) {
    tester.view.physicalSize = surfaceSize;
    tester.view.devicePixelRatio = 3;
    addTearDown(tester.view.reset);
  }

  await tester.pumpWidget(
    ProviderScope(
      overrides: [...testOverrides(locale: locale), ...overrides],
      child: const WordOsApp(),
    ),
  );
}

/// Boots the app and signs in with the seeded demo account.
Future<void> bootAndSignIn(
  WidgetTester tester, {
  Size surfaceSize = const Size(1200, 2600),
}) async {
  await bootApp(tester, surfaceSize: surfaceSize);
  await tester.pumpAndSettle();
  await tester.tap(find.widgetWithText(FilledButton, 'Sign in'));
  await tester.pumpAndSettle();
}
