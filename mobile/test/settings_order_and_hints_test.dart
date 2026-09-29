import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/models/models.dart';

import 'support/test_harness.dart';

/// Settings: the skills stay where they are (ADR-114), and Spelling's hints
/// start where the learner chose (ADR-115).
void main() {
  Future<void> openSettings(WidgetTester tester) async {
    tester.view.physicalSize = const Size(1200, 8000);
    tester.view.devicePixelRatio = 3;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      ProviderScope(overrides: testOverrides(), child: const WordOsApp()),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.byType(FilledButton).first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Settings').last);
    await tester.pumpAndSettle();
  }

  List<String> skillOrder(WidgetTester tester, String prefix) => tester
      .widgetList(
        find.byWidgetPredicate(
          (w) =>
              w.key is ValueKey<String> &&
              (w.key! as ValueKey<String>).value.startsWith(prefix),
        ),
      )
      .map((w) => (w.key! as ValueKey<String>).value.substring(prefix.length))
      .toList();

  testWidgets('the skills keep their order when a daily target changes', (
    tester,
  ) async {
    await openSettings(tester);
    final before = skillOrder(tester, 'target-');
    expect(before, [
      for (final s in [
        SkillType.reading,
        SkillType.listening,
        SkillType.speaking,
        SkillType.spelling,
        SkillType.writing,
      ])
        s.name,
    ]);

    // Drag the first slider — Reading's — and let it save.
    await tester.drag(find.byType(Slider).first, const Offset(200, 0));
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 1));
    await tester.pumpAndSettle();

    expect(skillOrder(tester, 'target-'), before);
    expect(skillOrder(tester, 'level-'), before);
  });

  testWidgets('the hint start is automatic, says what that means, and saves', (
    tester,
  ) async {
    await openSettings(tester);

    expect(find.text('Spelling hints'), findsOneWidget);
    // The mock learner reads at B1, where automatic means a synonym.
    expect(find.text('Automatic — for your level: synonyms'), findsOneWidget);
    // Four rungs to choose from; the letter count is not one of them.
    expect(find.text('Dictionary definition'), findsOneWidget);
    expect(find.text('Simplified definition'), findsOneWidget);
    expect(find.text('Arabic meaning'), findsOneWidget);
    expect(find.byKey(const ValueKey('hint-start-LETTER_COUNT')), findsNothing);

    Icon mark(String wire) => tester.widget<Icon>(
      find.descendant(
        of: find.byKey(ValueKey('hint-start-$wire')),
        matching: find.byType(Icon),
      ),
    );
    expect(mark('AUTO').icon, Icons.radio_button_checked);

    await tester.tap(find.byKey(const ValueKey('hint-start-DEFINITION_EN')));
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 1));
    await tester.pumpAndSettle();

    expect(mark('DEFINITION_EN').icon, Icons.radio_button_checked);
    expect(mark('AUTO').icon, Icons.radio_button_unchecked);
    expect(
      find.text('Saved. Your next Spelling session starts here.'),
      findsOneWidget,
    );
  });
}
