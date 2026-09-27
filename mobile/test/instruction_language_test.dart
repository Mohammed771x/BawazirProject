import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/l10n/app_strings.dart';
import 'package:wordos/core/models/models.dart';

import 'support/test_harness.dart';

/// Which language a session sets its own task in (ADR-088).
///
/// From B1 up a Writing task is given in English. A learner about to write
/// English has already started in it, and an instruction in the language of the
/// work is one less translation between them and the task. Below B1 the
/// instruction is scaffolding: it has to be understood instantly, or the task
/// becomes a reading test with a writing task attached.
///
/// Only the instruction moves. Everything around it — the buttons, the
/// headings, the errors — is the app talking, and the app keeps speaking the
/// learner's language.
void main() {
  Future<void> openWriting(WidgetTester tester, {required Locale locale}) async {
    await bootApp(tester,
        locale: locale, surfaceSize: const Size(1200, 2600));
    await tester.pumpAndSettle();
    await tester.tap(find.byType(FilledButton).first);
    await tester.pumpAndSettle();
    await tester
        .tap(find.text(locale.languageCode == 'ar' ? 'الكتابة' : 'Writing'));
    await tester.pumpAndSettle();
  }

  testWidgets('the writing task is set in English in the Arabic app',
      (tester) async {
    await openWriting(tester, locale: const Locale('ar'));

    // The seeded learner is at B1, so the instruction is English…
    expect(find.textContaining('Write one sentence'), findsOneWidget);
    expect(find.textContaining('اكتب جملة واحدة'), findsNothing);
  });

  testWidgets('everything around the task still speaks Arabic', (tester) async {
    await openWriting(tester, locale: const Locale('ar'));

    // …and the app has not switched language with it. This is the half that
    // would be easy to get wrong by rendering the whole screen in English.
    expect(find.text('الكتابة'), findsWidgets);
    expect(find.textContaining('تحقق'), findsWidgets,
        reason: 'the buttons are the app talking, not the exercise');
  });

  group('the rule itself', () {
    const arabic = AppStrings(Locale('ar'));

    test('a session that names no language keeps the learner\'s', () {
      expect(
        arabic.forInstructions(null).sessionPrompt(
            SessionPromptKey.writeASentence, 'research'),
        contains('اكتب'),
      );
    });

    test('EN moves the instruction and nothing else', () {
      final said = arabic.forInstructions('EN');

      expect(
        said.sessionPrompt(SessionPromptKey.writeASentence, 'research'),
        contains('Write one sentence'),
      );
      // The same object is not the app's voice: `arabic` is untouched, so the
      // screen around the instruction is unaffected.
      expect(arabic.isArabic, isTrue);
    });

    test('a language this build has never heard of changes nothing', () {
      // The server may one day say something newer than this app. A value it
      // cannot honour must leave the learner in their own language rather than
      // guess — an instruction in the wrong language is worse than a plain one.
      expect(arabic.forInstructions('FR').isArabic, isTrue);
      expect(arabic.forInstructions('').isArabic, isTrue);
    });
  });
}
