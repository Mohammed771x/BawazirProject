import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/storage/app_preferences.dart';
import 'package:wordos/core/storage/preferences_providers.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/features/session/session_widgets.dart';

import 'support/test_harness.dart';

/// The final QA sweep (Part 3 §44–§56).
///
/// Every screen a learner can reach, walked in both languages and both themes,
/// looking for the two failures that a feature test never catches: an overflow
/// at a narrow width or a long Arabic string, and a layout that silently stays
/// left-to-right in an RTL locale.
///
/// Flutter reports overflows as exceptions during layout, so `takeException`
/// after each screen is the actual assertion — there is nothing to eyeball.
void main() {
  /// The screens a signed-in learner reaches from the shell.
  const tabs = {
    'en': ['Skills Hub', 'My words', 'Settings'],
    'ar': ['مركز المهارات', 'كلماتي', 'الإعدادات'],
  };

  /// Every string on screen, for failure messages worth reading.
  List<String> visibleOf(WidgetTester tester) => tester
      .widgetList<Text>(find.byType(Text))
      .map((t) => t.data ?? '')
      .where((t) => t.trim().isNotEmpty)
      .toList();

  Future<void> sweep(
    WidgetTester tester, {
    required Locale locale,
    required ThemeMode theme,
    required Size size,
  }) async {
    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 3;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          appPreferencesProvider.overrideWithValue(
            InMemoryAppPreferences(locale: locale, themeMode: theme),
          ),
          tokenStoreProvider.overrideWith((ref) => FakeTokenStore()),
        ],
        child: const WordOsApp(),
      ),
    );
    await tester.pumpAndSettle();

    // Sign in with whatever the sign-in button is called in this language.
    final signIn = find.byType(FilledButton).first;
    await tester.tap(signIn);
    await tester.pumpAndSettle();

    expect(tester.takeException(), isNull,
        reason: 'sign-in overflowed at ${size.width}x${size.height} '
            'in ${locale.languageCode}/${theme.name}');

    for (final tab in tabs[locale.languageCode]!) {
      final target = find.text(tab);
      if (target.evaluate().isEmpty) continue;

      await tester.tap(target.last);
      await tester.pumpAndSettle();

      expect(tester.takeException(), isNull,
          reason: '"$tab" overflowed at ${size.width}x${size.height} '
              'in ${locale.languageCode}/${theme.name}');

      // RTL is a layout property, not a font: an Arabic build that lays out
      // left-to-right looks translated and behaves wrong.
      if (locale.languageCode == 'ar') {
        expect(
          Directionality.of(tester.element(find.text(tab).last)),
          TextDirection.rtl,
          reason: '"$tab" should lay out right-to-left',
        );
      }
    }

    // The screens below the tabs, which is where the risk actually is: a
    // generated passage, a long Arabic meaning beside a level badge, a row of
    // letter tiles. Each is opened and then backed out of.
    final deep = locale.languageCode == 'ar'
        ? const ['القراءة', 'المراجعة الأسبوعية', 'إضافة كلمة']
        : const ['Reading', 'Weekly Review', 'Add word'];

    var opened = 0;
    for (final screen in deep) {
      // Back to the Hub first — the previous iteration left us elsewhere.
      final hub = tabs[locale.languageCode]!.first;
      if (find.text(hub).evaluate().isNotEmpty) {
        await tester.tap(find.text(hub).last);
        await tester.pumpAndSettle();
      }

      final entry = find.text(screen);
      if (entry.evaluate().isEmpty) continue;

      await tester.tap(entry.last, warnIfMissed: false);
      await tester.pumpAndSettle();
      opened++;

      expect(tester.takeException(), isNull,
          reason: '"$screen" overflowed at ${size.width}x${size.height} '
              'in ${locale.languageCode}/${theme.name}. '
              'Visible: ${visibleOf(tester).take(6)}');
    }

    // Guards the guard: a loop that silently found none of its screens would
    // pass while testing nothing.
    expect(opened, greaterThan(0),
        reason: 'none of $deep was reachable — the sweep tested only the tabs');
  }

  // A small phone and a large one. The narrow case is where labels overflow;
  // the tall case is where a fixed-height row stops being fixed.
  const sizes = [Size(1080, 1920), Size(1290, 2796)];

  for (final size in sizes) {
    for (final locale in [Locale('en'), Locale('ar')]) {
      for (final theme in [ThemeMode.light, ThemeMode.dark]) {
        testWidgets(
          'every learner screen survives ${locale.languageCode}/'
          '${theme.name} at ${size.width.toInt()}px',
          (tester) async =>
              sweep(tester, locale: locale, theme: theme, size: size),
        );
      }
    }
  }

  testWidgets('the reading passage marks its target words in dark mode',
      (tester) async {
    // §16: the underline is the only signal telling a learner which words the
    // session is about, and a washed-out one disappears against a dark
    // surface. Full-strength decoration is the fix, and it is worth pinning.
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          appPreferencesProvider.overrideWithValue(
            InMemoryAppPreferences(
                locale: const Locale('en'), themeMode: ThemeMode.dark),
          ),
          tokenStoreProvider.overrideWith((ref) => FakeTokenStore()),
        ],
        child: const WordOsApp(),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.byType(FilledButton).first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Reading'));
    await tester.pumpAndSettle();

    final spans = (tester
            .widget<Text>(find.descendant(
              of: find.byType(HighlightedPassage),
              matching: find.byType(Text),
            ))
            .textSpan! as TextSpan)
        .children!
        .whereType<TextSpan>();

    final underlined =
        spans.where((s) => s.style?.decoration == TextDecoration.underline);

    expect(underlined, isNotEmpty, reason: 'target words should be underlined');
    for (final span in underlined) {
      expect(span.style!.decorationColor!.a, 1.0,
          reason: 'a faded underline is invisible on the dark surface (§16)');
    }
  });
}
