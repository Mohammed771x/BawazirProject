import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter_test/flutter_test.dart';

import 'support/test_harness.dart';

/// QA prompt 06 (campaign 20261111-0121-d365f09): the screens a learner
/// reaches, walked across language × theme × phone size × text scale, plus
/// Flutter's own accessibility guidelines on each screen.
///
/// Results are collected rather than failing on the first one, so the report
/// can show the whole matrix; the test fails at the end if any overflow was
/// seen. Accessibility findings are recorded, not asserted: a tap target or a
/// contrast ratio below a guideline is a finding to triage, not an automatic
/// release blocker. Mock backend only (see `testOverrides`).
void main() {
  // Written only when asked for, so an ordinary `flutter test` leaves no file:
  //   QA_UI_MATRIX_OUT=../reports/qa/<campaign>/raw/part2/ui-matrix.json flutter test test/qa_ui_matrix_test.dart
  final out = Platform.environment['QA_UI_MATRIX_OUT'];
  final rows = <Map<String, Object?>>[];

  const tabs = {
    'en': ['Skills Hub', 'My words', 'Settings'],
    'ar': ['مركز المهارات', 'كلماتي', 'الإعدادات'],
  };
  const signUp = {'en': 'Create account', 'ar': 'إنشاء حساب'};
  const addWord = {'en': 'Add word', 'ar': 'إضافة كلمة'};

  // Logical sizes: iPhone SE (1st gen), iPhone 8/SE 2, iPhone Pro Max.
  const sizes = [Size(320, 568), Size(375, 667), Size(430, 932)];
  const scales = [1.0, 1.5, 2.0];

  Future<List<String>> visit(WidgetTester tester, Future<void> Function() go) async {
    final seen = <String>[];
    final previous = FlutterError.onError;
    FlutterError.onError = (details) {
      final text = details.exceptionAsString();
      final where = RegExp(r'file:///[^ \n]*lib/[^ \n:]*:\d+').firstMatch(details.toString())?.group(0) ?? '?';
      seen.add('${text.split('\n').first} @ ${where.split('/lib/').last}');
    };
    try {
      await go();
      await tester.pumpAndSettle();
    } finally {
      FlutterError.onError = previous;
    }
    final pending = tester.takeException();
    if (pending != null) seen.add(pending.toString().split('\n').first);
    return seen;
  }

  void record(String label, Map<String, List<String>> screens) {
    bool over(String m) => m.contains('overflowed') || m.contains('RenderFlex');
    final overflows = {for (final e in screens.entries) e.key: e.value.where(over).toList()}
      ..removeWhere((_, v) => v.isEmpty);
    final other = {for (final e in screens.entries) e.key: e.value.where((m) => !over(m)).toList()}
      ..removeWhere((_, v) => v.isEmpty);
    rows.add({'matrix': label, 'screens': screens.keys.toList(), 'overflows': overflows, 'other': other});
  }

  for (final lang in ['en', 'ar']) {
    for (final theme in [ThemeMode.light, ThemeMode.dark]) {
      for (final size in sizes) {
        for (final scale in scales) {
          final label = '$lang/${theme.name}/${size.width.toInt()}x${size.height.toInt()}@${scale}x';
          Future<void> setUp(WidgetTester tester) async {
            tester.view.physicalSize = size * 3;
            tester.view.devicePixelRatio = 3;
            tester.platformDispatcher.textScaleFactorTestValue = scale;
            addTearDown(tester.view.reset);
            addTearDown(tester.platformDispatcher.clearTextScaleFactorTestValue);
          }

          testWidgets('create account at $label', (tester) async {
            await setUp(tester);
            final screens = <String, List<String>>{};
            screens['sign-in'] = await visit(tester, () async {
              await bootApp(tester, locale: Locale(lang), themeMode: theme);
            });
            screens['create-account'] = await visit(tester, () async {
              final link = find.text(signUp[lang]!);
              if (link.evaluate().isEmpty) throw StateError('no create-account link');
              await tester.ensureVisible(link.first);
              await tester.tap(link.first);
            }).catchError((Object e) => ['NOT REACHED: $e']);
            record(label, screens);
          });

          testWidgets('signed-in screens at $label', (tester) async {
            await setUp(tester);
            final screens = <String, List<String>>{};
            await visit(tester, () async {
              await bootApp(tester, locale: Locale(lang), themeMode: theme);
              await tester.pumpAndSettle();
              final button = find.byType(FilledButton).first;
              await tester.ensureVisible(button);
              await tester.tap(button);
            });
            for (final tab in tabs[lang]!) {
              screens[tab] = await visit(tester, () async {
                final t = find.text(tab);
                if (t.evaluate().isEmpty) throw StateError('tab "$tab" not on screen');
                await tester.tap(t.last);
              }).catchError((Object e) => ['NOT REACHED: $e']);
              if (tab == tabs[lang]![1]) {
                screens['add-word'] = await visit(tester, () async {
                  final b = find.text(addWord[lang]!);
                  if (b.evaluate().isEmpty) throw StateError('no add-word button');
                  await tester.tap(b.first);
                }).catchError((Object e) => ['NOT REACHED: $e']);
                await visit(tester, () async {
                  final nav = Navigator.maybeOf(tester.element(find.byType(Scaffold).last));
                  nav?.maybePop();
                });
              }
            }

            record(label, screens);
          });
        }
      }
    }
  }

  // Accessibility guidelines, at 100% text on a 375-wide phone, per screen.
  for (final lang in ['en', 'ar']) {
    for (final theme in [ThemeMode.light, ThemeMode.dark]) {
      testWidgets('accessibility guidelines $lang/${theme.name}', (tester) async {
        final semantics = tester.ensureSemantics();
        tester.view.physicalSize = const Size(375, 667) * 3;
        tester.view.devicePixelRatio = 3;
        addTearDown(tester.view.reset);

        Future<Map<String, Object?>> check(String screen) async {
          final result = <String, Object?>{'screen': screen};
          for (final g in <String, AccessibilityGuideline>{
            'androidTapTarget48': androidTapTargetGuideline,
            'iOSTapTarget44': iOSTapTargetGuideline,
            'labeledTapTarget': labeledTapTargetGuideline,
            'textContrast': textContrastGuideline,
          }.entries) {
            final e = await g.value.evaluate(tester);
            result[g.key] = e.passed ? 'pass' : (e.reason ?? '').split('\n').where((l) => l.trim().isNotEmpty).take(6).toList();
          }
          return result;
        }

        final found = <Map<String, Object?>>[];
        await bootApp(tester, locale: Locale(lang), themeMode: theme);
        await tester.pumpAndSettle();
        found.add(await check('sign-in'));
        await tester.tap(find.byType(FilledButton).first);
        await tester.pumpAndSettle();
        for (final tab in tabs[lang]!) {
          final t = find.text(tab);
          if (t.evaluate().isEmpty) continue;
          await tester.tap(t.last);
          await tester.pumpAndSettle();
          found.add(await check(tab));
        }
        rows.add({'a11y': '$lang/${theme.name}', 'results': found});
        semantics.dispose();
      });
    }
  }

  tearDownAll(() {
    if (out != null) File(out).writeAsStringSync(const JsonEncoder.withIndent(' ').convert(rows));
    final bad = rows.where((r) => (r['overflows'] as Map?)?.isNotEmpty ?? false).length;
    // ignore: avoid_print
    print('UI MATRIX: ${rows.where((r) => r.containsKey('matrix')).length} combinations, $bad with an overflow');
    expect(bad, 0, reason: 'No screen in the UI matrix may overflow.');
  });

  // Keeps `rendering` imported for debugPaintSizeEnabled when investigating.
  debugPaintSizeEnabled = false;
}
