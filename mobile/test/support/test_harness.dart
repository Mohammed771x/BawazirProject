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

/// Overrides every provider that would otherwise reach a platform channel.
///
/// Tests pin the locale to English so assertions can be written against the
/// English strings; the Arabic default is asserted separately in
/// `localization_test.dart`.
List<Override> testOverrides({Locale locale = const Locale('en')}) => [
      appPreferencesProvider
          .overrideWithValue(InMemoryAppPreferences(locale: locale)),
      tokenStoreProvider.overrideWith((ref) => FakeTokenStore()),
    ];

/// Pumps the real app with test-safe dependencies.
Future<void> bootApp(
  WidgetTester tester, {
  Locale locale = const Locale('en'),
  Size? surfaceSize,
}) async {
  if (surfaceSize != null) {
    tester.view.physicalSize = surfaceSize;
    tester.view.devicePixelRatio = 3;
    addTearDown(tester.view.reset);
  }

  await tester.pumpWidget(
    ProviderScope(
      overrides: testOverrides(locale: locale),
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
