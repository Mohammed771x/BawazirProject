import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/audio/speech_service.dart';
import 'package:wordos/core/audio/tts_service.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/core/models/models.dart';
import 'package:wordos/features/session/session_screen.dart';
import 'package:wordos/mock_backend/engine/mock_dictionary.dart';
import 'package:wordos/mock_backend/engine/mock_engine.dart';
import 'package:wordos/mock_backend/mock_wordos_api.dart';

import 'support/pipeline.dart';
import 'support/test_harness.dart';

/// The hands-free Speaking loop.
///
/// The property under test is that the learner presses **nothing**: the tutor
/// speaks, the microphone opens by itself when the voice stops, the recognised
/// words are sent, the reply is spoken, and the microphone opens again.
///
/// Both platform services are faked — neither works under `flutter test` — and
/// the fakes record their call order, which is the only way to prove the
/// microphone never opens while the tutor is still talking.
void main() {
  group('hands-free conversation', () {
    testWidgets('the tutor speaks and the microphone opens by itself',
        (tester) async {
      final tts = _FakeTts();
      final speech = _FakeSpeech(
        ['I like research because it teaches me new things.'],
        tts: tts,
      );

      await _pumpSpeaking(tester, tts, speech);

      expect(tts.spoken, isNotEmpty,
          reason: 'the tutor should start talking on its own');
      expect(speech.listenCount, greaterThanOrEqualTo(1),
          reason: 'the microphone should have opened without a tap');
      // The tutor spoke twice: the opening, then its reply to what the learner
      // said. That round trip is only possible if the recognised words were
      // actually sent — and it does not depend on the conversation still being
      // on screen, since the mock ends the session once the word has been used.
      expect(tts.spoken.length, greaterThanOrEqualTo(2),
          reason: 'the tutor should have replied to the spoken turn');
    });

    testWidgets('the microphone never opens while the tutor is talking',
        (tester) async {
      final tts = _FakeTts();
      final speech = _FakeSpeech(
        ['I did some research yesterday about sleep.'],
        tts: tts,
      );

      await _pumpSpeaking(tester, tts, speech);

      // An open microphone during playback records the tutor's own voice and
      // sends it back as the learner's answer. Ordering is the only guard.
      expect(speech.overlappedWithSpeech, isFalse,
          reason: 'listening began before the tutor had finished speaking');
    });

    testWidgets('a device that cannot listen offers typing instead',
        (tester) async {
      final tts = _FakeTts();
      final speech = _FakeSpeech([], available: false, tts: tts);

      await _pumpSpeaking(tester, tts, speech);

      // No microphone is not an error state — the learner types, and every
      // other part of the session is unchanged.
      expect(find.byType(TextField), findsWidgets,
          reason: 'typing should be offered when the device cannot listen');
      expect(speech.listenCount, 0);
    });

    testWidgets('an unheard turn is offered again, not sent empty',
        (tester) async {
      final tts = _FakeTts();
      // Silence, or noise the recogniser could not resolve.
      final speech = _FakeSpeech([null], tts: tts);

      await _pumpSpeaking(tester, tts, speech);

      // Sending an empty transcript would spend an AI call and confuse the
      // tutor, so the turn is offered again.
      expect(find.text('Tap to speak'), findsWidgets);
    });
  });
}

Future<void> _pumpSpeaking(
  WidgetTester tester,
  _FakeTts tts,
  _FakeSpeech speech,
) async {
  tester.view.physicalSize = const Size(1200, 2600);
  tester.view.devicePixelRatio = 3;
  addTearDown(tester.view.reset);

  // A learner with one word already waiting on Speaking. Driving the whole
  // pipeline through the UI is what the integration journeys are for; this test
  // is about the voice loop, so the word is walked there through the engine.
  final engine = MockEngine();
  final auth = engine.register('voice@test.dev', 'wordos123', 'Voice');
  final user = engine.requireUser(auth.token);
  engine.addWord(user, MockDictionary.entries['research']!.first);

  advanceToSkill(engine, user, SkillType.speaking);

  // `latencyScale: 0` so no artificial timer outlives the widget tree.
  final api = MockWordOsApi(
    tokenReader: () => auth.token,
    engine: engine,
    latencyScale: 0,
  );

  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        ...testOverrides(),
        wordOsApiProvider.overrideWithValue(api),
        ttsServiceProvider.overrideWithValue(tts),
        speechServiceProvider.overrideWithValue(speech),
      ],
      // The same localisation delegates the real app installs: they are what
      // initialise date symbols, which the session result screen formats with.
      child: const MaterialApp(
        locale: Locale('en'),
        supportedLocales: [Locale('en'), Locale('ar')],
        localizationsDelegates: [
          GlobalMaterialLocalizations.delegate,
          GlobalWidgetsLocalizations.delegate,
          GlobalCupertinoLocalizations.delegate,
        ],
        home: SessionScreen(skill: SkillType.speaking),
      ),
    ),
  );

  // The loop is a chain of awaited futures rather than animations, so it is
  // advanced by pumping rather than settled.
  for (var i = 0; i < 60; i++) {
    await tester.pump(const Duration(milliseconds: 100));
  }

}

/// Records what was spoken and completes immediately instead of waiting for a
/// real voice.
///
/// `implements` rather than `extends`: extending would run the real
/// constructor, which reaches for a platform channel that does not exist under
/// `flutter test`.
class _FakeTts implements TtsService {
  final List<String> spoken = [];
  bool speaking = false;

  @override
  bool get isAvailable => true;

  @override
  bool get isSpeaking => speaking;

  @override
  set onStateChanged(void Function()? callback) {}

  @override
  Future<bool> speak(String text, {bool slow = false}) async {
    spoken.add(text);
    return true;
  }

  @override
  Future<bool> speakToCompletion(String text) async {
    speaking = true;
    spoken.add(text);
    await Future<void>.delayed(const Duration(milliseconds: 20));
    speaking = false;
    return true;
  }

  @override
  Future<void> stop() async => speaking = false;

  @override
  void dispose() {}
}

/// Returns scripted phrases, one per listen, and records whether it was ever
/// asked to listen while the voice was still playing.
class _FakeSpeech implements SpeechService {
  _FakeSpeech(this.phrases, {this.available = true, this.tts});

  final List<String?> phrases;
  final bool available;
  final _FakeTts? tts;

  int listenCount = 0;
  bool overlappedWithSpeech = false;

  @override
  bool get isAvailable => available;

  @override
  bool get isListening => false;

  @override
  Future<bool> initialise() async => available;

  @override
  Future<String?> listenOnce({
    Duration pauseFor = const Duration(seconds: 3),
    Duration listenFor = const Duration(seconds: 45),
  }) async {
    if (tts?.speaking == true) overlappedWithSpeech = true;

    final index = listenCount++;
    await Future<void>.delayed(const Duration(milliseconds: 20));
    return index < phrases.length ? phrases[index] : null;
  }

  @override
  Future<void> stop() async {}

  @override
  Future<void> cancel() async {}
}
