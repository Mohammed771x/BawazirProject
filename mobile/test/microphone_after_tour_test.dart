import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/audio/speech_recognition_service.dart';
import 'package:wordos/core/storage/app_preferences.dart';
import 'package:wordos/core/storage/preferences_providers.dart';
import 'package:wordos/core/tour/tour.dart';
import 'package:wordos/features/onboarding/spoken_answer_field.dart';

/// The microphone prompt and the guided tour must not land together (ADR-128).
///
/// Reported from the simulator: the speaking question's tour and the system's
/// permission prompts came up at once, the learner took longer than the
/// recogniser's ten seconds to get through them, and the question said "this
/// device cannot listen" until the app was restarted.
void main() {
  Future<void> pumpField(WidgetTester tester, _CountingSpeech speech) =>
      tester.pumpWidget(
        ProviderScope(
          overrides: [
            appPreferencesProvider.overrideWithValue(
              InMemoryAppPreferences(locale: const Locale('en')),
            ),
            speechRecognitionProvider.overrideWithValue(speech),
          ],
          child: MaterialApp(
            home: Scaffold(
              body: SpokenAnswerField(
                transcript: '',
                enabled: true,
                onTranscript: (_) {},
              ),
            ),
          ),
        ),
      );

  testWidgets('the microphone is not asked for while a tour is on its way',
      (tester) async {
    final speech = _CountingSpeech([true]);
    AppTour.waitingStarted();
    addTearDown(AppTour.waitingEnded);

    await pumpField(tester, speech);
    await tester.pump(const Duration(seconds: 1));
    expect(speech.asked, 0, reason: 'the tour comes first');

    AppTour.waitingEnded();
    await tester.pump(const Duration(milliseconds: 300));
    expect(speech.asked, 1);
  });

  testWidgets('a "no" is asked again, not kept for the rest of the run',
      (tester) async {
    final speech = _CountingSpeech([false, true]);
    await pumpField(tester, speech);
    await tester.pump();
    expect(find.text('Use voice'), findsOneWidget);

    // Back from the permission prompt, or from Settings.
    tester.binding
        .handleAppLifecycleStateChanged(AppLifecycleState.inactive);
    tester.binding.handleAppLifecycleStateChanged(AppLifecycleState.resumed);
    await tester.pump();

    expect(speech.asked, 2);
    expect(find.byIcon(Icons.mic_rounded), findsOneWidget,
        reason: 'the microphone is offered once it is allowed');
  });
}

/// Answers each `initialise` from a script and counts the questions.
class _CountingSpeech implements SpeechRecognitionService {
  _CountingSpeech(this.answers);

  final List<bool> answers;
  int asked = 0;

  @override
  Future<bool> initialise() async =>
      answers[(asked++).clamp(0, answers.length - 1)];

  @override
  bool get isAvailable => false;

  @override
  bool get isListening => false;

  @override
  String get heard => '';

  @override
  String? get lastFailure => null;

  @override
  Future<bool> startListening({void Function(String heard)? onPartial}) async =>
      false;

  @override
  Future<String?> stopAndRead() async => null;

  @override
  Future<String?> listenOnce({
    Duration pauseFor = const Duration(seconds: 3),
    Duration listenFor = const Duration(seconds: 45),
  }) async =>
      null;

  @override
  Future<void> stop() async {}

  @override
  Future<void> cancel() async {}
}
