import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/l10n/app_strings.dart';
import 'package:wordos/core/storage/app_preferences.dart';
import 'package:wordos/core/storage/preferences_providers.dart';

import 'support/test_harness.dart';

void main() {
  test('a fresh install defaults to Arabic, not the device locale', () {
    expect(AppPreferences.defaultLocale, const Locale('ar'));

    final container = ProviderContainer(
      overrides: [
        appPreferencesProvider.overrideWithValue(InMemoryAppPreferences()),
      ],
    );
    addTearDown(container.dispose);

    expect(container.read(localeProvider), const Locale('ar'));
    expect(container.read(stringsProvider).isArabic, isTrue);
  });

  test('an explicit language choice is persisted and wins on next launch',
      () async {
    final prefs = InMemoryAppPreferences();
    final container = ProviderContainer(
      overrides: [appPreferencesProvider.overrideWithValue(prefs)],
    );
    addTearDown(container.dispose);

    await container.read(localeProvider.notifier).setLocale(const Locale('en'));

    expect(prefs.locale, const Locale('en'));

    // A second "launch" reading the same store starts in English.
    final relaunched = ProviderContainer(
      overrides: [appPreferencesProvider.overrideWithValue(prefs)],
    );
    addTearDown(relaunched.dispose);
    expect(relaunched.read(localeProvider), const Locale('en'));
  });

  testWidgets('the app boots in Arabic and lays out right-to-left',
      (tester) async {
    await bootApp(tester, locale: AppPreferences.defaultLocale);
    await tester.pumpAndSettle();

    expect(find.text('أهلًا بعودتك'), findsOneWidget);
    expect(Directionality.of(tester.element(find.text('أهلًا بعودتك'))),
        TextDirection.rtl);
  });
}
