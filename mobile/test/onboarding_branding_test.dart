import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/core/storage/app_preferences.dart';
import 'package:wordos/core/storage/preferences_providers.dart';
import 'package:wordos/core/widgets/brand.dart';

import 'support/test_harness.dart';

/// Branding position, and who sees the product tour.
void main() {
  group('branding', () {
    testWidgets('the brand sits on the left in English', (tester) async {
      await _boot(tester, locale: const Locale('en'));
      expect(_brandLeftEdge(tester), lessThan(80),
          reason: 'the W logo should be near the left edge');
    });

    testWidgets('Arabic does not move the brand to the right', (tester) async {
      await _boot(tester, locale: const Locale('ar'));

      // The content flips to RTL — that is the point of Arabic — but the brand
      // is an identity, not a paragraph, so it stays put.
      expect(
        tester.widget<Directionality>(
          find.ancestor(
            of: find.byType(Scaffold).first,
            matching: find.byType(Directionality),
          ).first,
        ).textDirection,
        TextDirection.rtl,
        reason: 'Arabic should still lay the app out right-to-left',
      );

      expect(_brandLeftEdge(tester), lessThan(80),
          reason: 'the brand must not follow the reading direction');
    });

    testWidgets('the logo precedes the wordmark in both languages',
        (tester) async {
      for (final locale in [const Locale('en'), const Locale('ar')]) {
        await _boot(tester, locale: locale);

        final mark = tester.getCenter(find.text('W'));
        final wordmark = tester.getCenter(find.text('WordOS'));

        expect(mark.dx, lessThan(wordmark.dx),
            reason: 'the W should sit left of the wordmark in $locale');
      }
    });
  });

  group('onboarding', () {
    testWidgets('a first-time learner sees the product tour', (tester) async {
      await _boot(tester, onboardingSeen: false);

      expect(find.text('Welcome to WordOS'), findsOneWidget);
      // Explanation first: nothing is asked for on the opening screen.
      expect(find.text('Sign in'), findsNothing);
    });

    testWidgets('the tour ends at account creation, not before it',
        (tester) async {
      await _boot(tester, onboardingSeen: false);

      for (var i = 0; i < 3; i++) {
        await tester.tap(find.text('Next'));
        await tester.pumpAndSettle();
      }

      expect(find.text('Create your account'), findsWidgets,
          reason: 'authentication comes after the explanation');
    });

    testWidgets('a returning learner never sees it again', (tester) async {
      final prefs = InMemoryAppPreferences(
          locale: const Locale('en'), onboardingSeen: false);

      await _boot(tester, preferences: prefs);
      expect(find.text('Welcome to WordOS'), findsOneWidget);

      // Skipping is still "seen": the learner has decided they do not need it.
      await tester.tap(find.text('Skip'));
      await tester.pumpAndSettle();

      expect(prefs.onboardingSeen, isTrue,
          reason: 'the decision must be recorded on the device');

      // A fresh launch, carrying only what the device kept.
      await _boot(tester, preferences: prefs);
      expect(find.text('Welcome to WordOS'), findsNothing);
    });
  });
}

int _bootCount = 0;

Future<void> _boot(
  WidgetTester tester, {
  Locale locale = const Locale('en'),
  bool onboardingSeen = true,
  InMemoryAppPreferences? preferences,
}) async {
  tester.view.physicalSize = const Size(1200, 2600);
  tester.view.devicePixelRatio = 3;
  addTearDown(tester.view.reset);

  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        appPreferencesProvider.overrideWithValue(
          preferences ??
              InMemoryAppPreferences(
                  locale: locale, onboardingSeen: onboardingSeen),
        ),
        tokenStoreProvider.overrideWith((ref) => FakeTokenStore()),
      ],
      child: KeyedSubtree(
        key: ValueKey('boot-${_bootCount++}'),
        child: const WordOsApp(),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

/// The left edge of the brand mark in logical pixels.
double _brandLeftEdge(WidgetTester tester) {
  expect(find.byType(WordOsBrand), findsWidgets,
      reason: 'every entry screen shows the brand');
  return tester.getTopLeft(find.text('W').first).dx;
}
