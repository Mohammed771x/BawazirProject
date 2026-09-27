import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'support/test_harness.dart';

/// One question, one button.
///
/// A writing question used to carry its own "check" inside the scrolling body
/// while the foot of the screen held a disabled "next" — two buttons for one
/// decision, and the live one was further from the thumb than the dead one. The
/// foot of the screen now does both in turn: it reads "check" until the verdict
/// is on screen and "next" afterwards, which is what Reading and Listening
/// already did.
void main() {
  Future<void> openWriting(WidgetTester tester) async {
    await bootApp(tester,
        locale: const Locale('en'), surfaceSize: const Size(1200, 2600));
    await tester.pumpAndSettle();
    await tester.tap(find.byType(FilledButton).first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Writing'));
    await tester.pumpAndSettle();
  }

  Future<void> write(WidgetTester tester, String sentence) async {
    await tester.enterText(find.byType(TextField).first, sentence);
    await tester.pump();
  }

  /// The word this question is about, read off the instruction on screen.
  String wordOnScreen(WidgetTester tester) {
    final task = tester
        .widgetList<Text>(find.textContaining('Write one sentence'))
        .first
        .data!;
    return RegExp(r'"([^"]+)"').firstMatch(task)!.group(1)!;
  }

  /// A sentence that actually uses it — otherwise the answer fails and the
  /// word is requeued, so the next question is the same word again and the
  /// test reads that as the screen not having moved.
  Future<void> answerWell(WidgetTester tester) async {
    await write(tester, 'I will ${wordOnScreen(tester)} it again tomorrow.');
    await tester.tap(find.text('Check'));
    await tester.pumpAndSettle();
  }

  testWidgets('one button is offered, not two', (tester) async {
    await openWriting(tester);

    expect(find.text('Check'), findsOneWidget);
    expect(find.text('Next'), findsNothing,
        reason: 'a dead "next" beside a live "check" is two buttons for one '
            'decision');
    expect(find.text('Finish'), findsNothing);
  });

  testWidgets('there is nothing to check until something is written',
      (tester) async {
    await openWriting(tester);

    FilledButton button() => tester.widget<FilledButton>(
        find.widgetWithText(FilledButton, 'Check'));

    expect(button().onPressed, isNull,
        reason: 'an empty box has nothing to mark');

    await write(tester, 'They propose a shorter route through the valley.');
    expect(button().onPressed, isNotNull);
  });

  testWidgets('the same button turns into "next" once the verdict is in',
      (tester) async {
    await openWriting(tester);
    await write(tester, 'They propose a shorter route through the valley.');

    await tester.tap(find.text('Check'));
    await tester.pumpAndSettle();

    // The verdict is on screen…
    expect(find.byType(TextField).first, findsOneWidget);
    // …and the one button has changed its job rather than a second appearing.
    expect(find.text('Check'), findsNothing);
    expect(find.text('Next'), findsOneWidget);
  });

  testWidgets('pressing it again moves on, with the box cleared',
      (tester) async {
    await openWriting(tester);

    // Answered badly on purpose: the word is requeued, so there is a question
    // after this one to move on to. The seeded learner has a single word due
    // for Writing, and with a good answer the button reads "finish" instead —
    // which is the same control doing the same job.
    await write(tester, 'A sentence that ignores what was asked.');
    await tester.tap(find.text('Check'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Next'));
    await tester.pumpAndSettle();

    expect(find.text('Check'), findsOneWidget,
        reason: 'the button is back to asking for an answer');
    expect(find.text('Next'), findsNothing);
    expect(
      tester.widget<TextField>(find.byType(TextField).first).controller!.text,
      isEmpty,
      reason: 'the last answer must not be sitting in the box',
    );
  });

  testWidgets('the last question ends on "finish", still one button',
      (tester) async {
    await openWriting(tester);
    await answerWell(tester);

    expect(find.text('Finish'), findsOneWidget);
    expect(find.text('Check'), findsNothing);
  });
}
