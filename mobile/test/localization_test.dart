import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/l10n/app_strings.dart';
import 'package:wordos/core/models/models.dart';
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

  // ── What follows the app language, and what deliberately does not ─────────

  group('the language line (ADR-035)', () {
    AppStrings stringsFor(String code) {
      final container = ProviderContainer(overrides: [
        appPreferencesProvider
            .overrideWithValue(InMemoryAppPreferences(locale: Locale(code))),
      ]);
      addTearDown(container.dispose);
      return container.read(stringsProvider);
    }

    test('an instruction is said in the language the learner reads', () {
      // The server sends a key, not a sentence, precisely so this can happen.
      expect(
        stringsFor('ar').sessionPrompt(SessionPromptKey.writeTheWord, 'research'),
        'اكتب الكلمة',
      );
      expect(
        stringsFor('en').sessionPrompt(SessionPromptKey.writeTheWord, 'research'),
        'Write the word',
      );
    });

    test('the English word inside an instruction stays English', () {
      final arabic = stringsFor('ar')
          .sessionPrompt(SessionPromptKey.writeASentence, 'research');

      expect(arabic, contains('research'),
          reason: 'the word being practised is the material, not the wrapper');
      expect(arabic, contains('اكتب'));
    });

    test('a failure is reported in the app language, by code', () {
      expect(
        stringsFor('ar').apiError('INVALID_CREDENTIALS', 'Wrong email or password.'),
        'البريد الإلكتروني أو كلمة المرور غير صحيحة.',
      );
      expect(
        stringsFor('en').apiError('INVALID_CREDENTIALS', 'ignored'),
        'Wrong email or password.',
      );
    });

    test('an unknown code falls back to what the server said', () {
      // Better than a shrug: the server's sentence at least names the problem.
      const serverSaid = 'Something specific the client has never seen.';
      expect(stringsFor('ar').apiError('BRAND_NEW_CODE', serverSaid), serverSaid);
    });

    test('content carries no key, so it is shown exactly as it arrived', () {
      // A comprehension question is written for one passage. Translating it
      // would change what the question measures.
      expect(SessionPromptKey.fromWire(null), isNull);
      expect(SessionPromptKey.fromWire('SOMETHING_ELSE'), isNull);
      expect(stringsFor('ar').sessionPrompt(null, 'research'), isEmpty);
    });
  });
}
