import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/audio/speech_provider.dart';
import 'package:wordos/core/widgets/app_widgets.dart';
import 'package:wordos/core/audio/speech_service.dart';
import 'package:wordos/features/onboarding/placement_screen.dart';
import 'package:wordos/features/onboarding/spoken_answer_field.dart';

import 'support/test_harness.dart';

/// Leaving a placement question has to end it (ADR-058).
///
/// Three reports from the device, all the same fault in different clothes: the
/// listening clip played on over the next question, the microphone stayed open
/// into it, and the previous answer's text was still in the box. Nothing told
/// the question it was over.
void main() {
  /// Walks a fresh account as far as the placement test's first question.
  Future<void> reachPlacement(WidgetTester tester) async {
    await tester.pumpAndSettle();

    // A new account, because the seeded learner has already been placed.
    final create = find.widgetWithText(TextButton, 'Create account');
    await tester.ensureVisible(create);
    await tester.tap(create);
    await tester.pumpAndSettle();

    // Name, email, phone, password — the country code is a picker, not a
    // field, so the phone is the third box and not the fourth.
    final fields = find.byType(TextFormField);
    await tester.enterText(fields.at(0), 'Placement Tester');
    await tester.enterText(fields.at(1), 'placement@wordos.test');
    await tester.enterText(fields.at(2), '770112233');
    await tester.enterText(fields.last, 'wordos123');

    final submit = find.widgetWithText(FilledButton, 'Create account');
    await tester.ensureVisible(submit);
    await tester.tap(submit);
    await tester.pumpAndSettle();

    // Interests must be chosen before Continue does anything.
    final interest = find.textContaining('Technology');
    if (interest.evaluate().isNotEmpty) {
      await tester.tap(interest.first, warnIfMissed: false);
      await tester.pumpAndSettle();
    }

    for (var step = 0; step < 5; step++) {
      final start = find.text('Start the test');
      if (start.evaluate().isNotEmpty) {
        await tester.ensureVisible(start.first);
        await tester.tap(start.first, warnIfMissed: false);
        await tester.pumpAndSettle();
        return;
      }

      final next = find.byType(FilledButton);
      if (next.evaluate().isEmpty) break;
      await tester.ensureVisible(next.first);
      await tester.tap(next.first, warnIfMissed: false);
      await tester.pumpAndSettle();
    }
  }

  /// Answers whatever question is on screen and moves on.
  Future<void> answerAndAdvance(WidgetTester tester) async {
    // A spoken question has neither options nor a box until the learner asks
    // to type. Without this the walk taps a disabled Next for ever and never
    // reaches the questions after it.
    final typeInstead = find.text('Type instead');
    if (typeInstead.evaluate().isNotEmpty) {
      await tester.ensureVisible(typeInstead.first);
      await tester.tap(typeInstead.first, warnIfMissed: false);
      await tester.pumpAndSettle();
    }

    // The option, not the first InkWell on screen — on a listening question
    // that is the play button, and tapping it leaves the question unanswered
    // and the walk stuck on it.
    final option = find.byType(OptionTile);
    if (option.evaluate().isNotEmpty) {
      await tester.ensureVisible(option.first);
      await tester.tap(option.first, warnIfMissed: false);
      await tester.pumpAndSettle();
    }

    final next = find.byType(FilledButton);
    if (next.evaluate().isNotEmpty) {
      await tester.ensureVisible(next.last);
      await tester.tap(next.last, warnIfMissed: false);
      await tester.pumpAndSettle();
    }
  }

  testWidgets('moving to the next question stops the clip', (tester) async {
    final tts = _CountingTts();
    final speech = SpeechService(provider: tts);

    await bootApp(tester, surfaceSize: const Size(1200, 2600), overrides: [
      speechServiceProvider.overrideWith((ref) => speech),
    ]);
    await reachPlacement(tester);

    // A clip playing on the question the learner is about to leave. Started
    // through the same service the listening item uses, so the assertion is
    // about the screen's behaviour and not about which item the adaptive test
    // happened to serve first.
    await speech.speak('placement:clip', 'The shop opens at nine.');
    expect(speech.isSpeaking, isTrue, reason: 'the clip should be playing');

    await answerAndAdvance(tester);

    // Reported from the device: it carried on talking over the question after
    // it. Answering ends the question that owned the audio.
    expect(speech.isSpeaking, isFalse,
        reason: 'answering should silence the question being left');
    expect(tts.stops, greaterThan(0));
  });

  testWidgets('leaving the test stops the clip', (tester) async {
    final tts = _CountingTts();
    final speech = SpeechService(provider: tts);

    await bootApp(tester, surfaceSize: const Size(1200, 2600), overrides: [
      speechServiceProvider.overrideWith((ref) => speech),
    ]);
    await reachPlacement(tester);

    await speech.speak('placement:clip', 'The shop opens at nine.');

    // The case that was missed entirely: the screen held no reference to the
    // speech service at all, so nothing was ever asked to stop when it went.
    await tester.pumpWidget(const SizedBox.shrink());
    await tester.pumpAndSettle();

    expect(speech.isSpeaking, isFalse,
        reason: 'leaving the test should stop it talking');
    expect(tts.stops, greaterThan(0));
  });

  testWidgets('each question builds its own answer field', (tester) async {
    await bootApp(tester, surfaceSize: const Size(1200, 2600));
    await reachPlacement(tester);

    /// The element holding the current question.
    ///
    /// Found by its key, which is the fix itself: the question view is keyed
    /// by its item, so Flutter builds a new element — and therefore a new
    /// State, a new controller and a closed microphone — for every question.
    /// Unkeyed, the same element is reused and the last answer stays in it.
    Element? questionElement() {
      Element? found;
      void visit(Element element) {
        if (found != null) return;
        if (element.widget.key is ValueKey<String>) {
          found = element;
          return;
        }
        element.visitChildren(visit);
      }

      tester.element(find.byType(PlacementScreen)).visitChildren(visit);
      return found;
    }

    final first = questionElement();
    expect(first, isNotNull,
        reason: 'the question view must be keyed by its item');

    await answerAndAdvance(tester);

    final second = questionElement();
    expect(second, isNotNull);

    expect(identical(first, second), isFalse,
        reason: 'a new question must build a new field, not inherit the old '
            'one with its text and its open microphone');

    // And nothing typed before is still on screen.
    for (final field in tester.widgetList<TextField>(find.byType(TextField))) {
      expect(field.controller?.text ?? '', isEmpty);
    }
  });

  testWidgets('a transcript that arrives late belongs to the question it left',
      (tester) async {
    await bootApp(tester, surfaceSize: const Size(1200, 2600));
    await reachPlacement(tester);

    SpokenAnswerField? spoken() {
      final finder = find.byType(SpokenAnswerField);
      return finder.evaluate().isEmpty
          ? null
          : tester.widget<SpokenAnswerField>(finder);
    }

    // Walk to a spoken question: the transcript box is the one that showed the
    // previous answer, because it renders what the screen holds rather than a
    // controller of its own.
    for (var step = 0; step < 25 && spoken() == null; step++) {
      if (find.byType(FilledButton).evaluate().isEmpty) break;
      await answerAndAdvance(tester);
    }

    final leaving = spoken();
    expect(leaving, isNotNull,
        reason: 'the placement test should ask for a spoken answer');

    // The learner speaks and leaves the microphone open.
    final stale = leaving!.onTranscript;
    stale('what I said on the previous question');
    await tester.pumpAndSettle();

    await answerAndAdvance(tester);

    final arrived = spoken();
    if (arrived == null) {
      // The next question is not spoken; nothing to assert about its box.
      await tester.pumpAndSettle(const Duration(seconds: 11));
      return;
    }

    // Now the recogniser reports what it heard on the question just left. The
    // microphone had already stopped — this is the result that was in flight
    // when it did, and with nothing to say otherwise it landed here (ADR-058).
    stale('what I said on the previous question');
    await tester.pumpAndSettle();

    expect(spoken()!.transcript, isEmpty,
        reason: 'a late transcript must not fill the next question\'s box');
    expect(find.text('what I said on the previous question'), findsNothing);

    // Asking whether a microphone exists is given ten seconds to answer, and in
    // a test binding nothing ever does — so the timeout is still pending when
    // the tree goes unless it is allowed to expire.
    await tester.pumpAndSettle(const Duration(seconds: 11));
  });
}

/// Counts what the screen asked the voice engine to do.
class _CountingTts implements SpeechProvider {
  int stops = 0;

  @override
  bool get isAvailable => true;

  @override
  String? get voiceDescription => 'fake';

  // Never called back: a clip that ends the instant it starts could not carry
  // into the next question, which is the fault under test.
  @override
  set onComplete(VoidCallback? callback) {}

  @override
  Future<void> initialise() async {}

  @override
  Future<bool> speak(String text, {SpeechRate rate = SpeechRate.normal}) async {
    // Deliberately does not complete: a clip that ends the instant it starts
    // could never carry into the next question, which is the whole fault under
    // test.
    return true;
  }

  @override
  Future<void> stop() async => stops++;

  @override
  Future<void> dispose() async {}
}
