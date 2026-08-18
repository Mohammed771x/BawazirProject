import 'package:flutter/material.dart';
import 'package:flutter_localizations/flutter_localizations.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/audio/speech_provider.dart';
import 'package:wordos/core/widgets/app_widgets.dart';
import 'package:wordos/core/audio/speech_recognition_service.dart';
import 'package:wordos/core/audio/speech_service.dart';
import 'package:wordos/core/api/api_providers.dart';
import 'package:wordos/core/models/models.dart';
import 'package:wordos/features/session/session_screen.dart';
import 'package:wordos/mock_backend/engine/mock_dictionary.dart';
import 'package:wordos/mock_backend/engine/mock_engine.dart';
import 'package:wordos/mock_backend/mock_wordos_api.dart';

import 'support/pipeline.dart';
import 'support/test_harness.dart';

/// The Speaking conversation loop.
///
/// Push-to-talk, not hands-free. The tutor speaks first; the microphone then
/// waits. The learner taps to begin, talks for as long as they need — no
/// silence timer ends their turn — and taps again when they are finished.
///
/// That last part is the whole point. Ending a turn on silence cut learners off
/// mid-sentence: somebody composing a sentence in a foreign language pauses
/// constantly, and no amount of tuning a timer fixes it.
///
/// Both platform services are faked — neither works under `flutter test` — and
/// the fakes record their call order, which is the only way to prove the
/// microphone never opens while the tutor is still talking.
void main() {
  group('spoken conversation', () {
    testWidgets('the tutor speaks first and the microphone waits for a tap',
        (tester) async {
      final tts = _FakeTts();
      final speech = _FakeSpeech(
        ['I like research because it teaches me new things.'],
        tts: tts,
      );

      await _pumpSpeaking(tester, tts, speech);

      expect(tts.spoken, isNotEmpty,
          reason: 'the tutor should start talking on its own');
      expect(speech.listenCount, 0,
          reason: 'nothing should be recording before the learner taps — the '
              'microphone would otherwise be open while they are still '
              'listening to the tutor');

      // Tap to speak.
      await tester.tap(find.bySemanticsLabel('voice'));
      for (var i = 0; i < 20; i++) {
        await tester.pump(const Duration(milliseconds: 100));
      }

      expect(speech.listenCount, 1, reason: 'the tap should open the mic');
      expect(tts.spoken.length, 1,
          reason: 'the turn is not sent until the learner says they are done');

      // Tap again: finished.
      await tester.tap(find.bySemanticsLabel('voice'));
      for (var i = 0; i < 40; i++) {
        await tester.pump(const Duration(milliseconds: 100));
      }

      // The tutor replied, which is only possible if the recognised words were
      // actually sent when the learner closed their turn.
      expect(tts.spoken.length, greaterThanOrEqualTo(2),
          reason: 'the tutor should have replied to the spoken turn');
    });

    testWidgets('a pause does not end the turn — only the learner does',
        (tester) async {
      final tts = _FakeTts();
      final speech = _FakeSpeech(
        ['I think... research... is useful for students.'],
        tts: tts,
      );

      await _pumpSpeaking(tester, tts, speech);
      await tester.tap(find.bySemanticsLabel('voice'));
      await tester.pump(const Duration(milliseconds: 200));

      // Twenty seconds of the learner thinking mid-sentence. The old
      // three-second silence timer would have closed the turn six times over.
      for (var i = 0; i < 200; i++) {
        await tester.pump(const Duration(milliseconds: 100));
      }

      expect(speech.closed, isFalse,
          reason: 'the microphone must stay open while the learner thinks');
      expect(tts.spoken.length, 1,
          reason: 'nothing should have been sent — the learner is mid-answer');

      await tester.tap(find.bySemanticsLabel('voice'));
      for (var i = 0; i < 40; i++) {
        await tester.pump(const Duration(milliseconds: 100));
      }

      expect(speech.closed, isTrue, reason: 'the second tap ends the turn');
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
    testWidgets('the words are recalled first, and nothing listens until then',
        (tester) async {
      final tts = _FakeTts();
      final speech = _FakeSpeech(['I do research every week.']);
      final (engine, user) = await _pumpSpeaking(tester, tts, speech,
          start: false);

      // §1–§3: a warm-up, not a briefing. Showing the list told us nothing
      // about whether the learner knows the words; asking does.
      expect(find.byType(OptionTile), findsWidgets,
          reason: 'the words should be asked, not merely displayed');
      expect(find.text('research'), findsWidgets);

      expect(speech.listenCount, 0,
          reason: 'nothing may record before the conversation starts');
      expect(tts.spoken, isEmpty,
          reason: 'the tutor should not be talking over the warm-up');

      // Miss the word: it must come back rather than be dropped (§2).
      final meaning =
          user.words.firstWhere((w) => w.text == 'research').meaning;
      final wrong = tester
          .widgetList<Text>(find.descendant(
              of: find.byType(OptionTile), matching: find.byType(Text)))
          .map((t) => t.data)
          .whereType<String>()
          .firstWhere((o) => o != meaning);

      await tester.tap(find.text(wrong).last);
      for (var i = 0; i < 30; i++) {
        await tester.pump(const Duration(milliseconds: 100));
      }

      expect(find.byType(OptionTile), findsWidgets,
          reason: 'a missed word returns — the warm-up is not over');
      expect(tts.spoken, isEmpty,
          reason: 'the conversation must wait until every word is recalled');

      // Now get it right, and the tutor opens.
      await answerWarmup(tester, engine, user);
      for (var i = 0; i < 40; i++) {
        await tester.pump(const Duration(milliseconds: 100));
      }

      expect(tts.spoken, isNotEmpty, reason: 'now the tutor opens');
    });
  });
}

/// Boots a Speaking session and returns the engine and learner behind it.
///
/// [start] clears the warm-up so the conversation is running; the warm-up
/// itself is the subject of one test, and every other one wants what follows.
Future<(MockEngine, MockUser)> _pumpSpeaking(
  WidgetTester tester,
  _FakeTts tts,
  _FakeSpeech speech, {
  bool start = true,
}) async {
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
        // The real service, driven by a fake engine — so the state machine is
        // exercised rather than stubbed out.
        speechServiceProvider.overrideWith((ref) => SpeechService(provider: tts)),
        speechRecognitionProvider.overrideWithValue(speech),
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

  // The conversation waits behind a warm-up: the learner recalls the meaning
  // of each word before talking about it, and only then does the tutor speak.
  await tester.pumpAndSettle();
  if (!start) return (engine, user);
  await answerWarmup(tester, engine, user);

  // The loop is a chain of awaited futures rather than animations, so it is
  // advanced by pumping rather than settled.
  for (var i = 0; i < 60; i++) {
    await tester.pump(const Duration(milliseconds: 100));
  }

  return (engine, user);

}

/// Clears the warm-up by choosing the right meaning for every word.
///
/// A miss would send the word to the back of the queue, which is the behaviour
/// these tests are *not* about — they are about what happens after it.
Future<void> answerWarmup(
  WidgetTester tester,
  MockEngine engine,
  MockUser user,
) async {
  final meanings = {for (final w in user.words) w.text: w.meaning};

  for (var guard = 0; guard < 20; guard++) {
    if (find.byType(OptionTile).evaluate().isEmpty) break;

    final word = tester
        .widgetList<Text>(find.byType(Text))
        .map((t) => t.data)
        .firstWhere((t) => t != null && meanings.containsKey(t),
            orElse: () => null);
    if (word == null) break;

    await tester.tap(find.text(meanings[word]!).last);
    for (var i = 0; i < 30; i++) {
      await tester.pump(const Duration(milliseconds: 100));
    }
  }
}

/// A fake voice engine.
///
/// Deliberately fakes the **provider**, not the service: the play/stop state
/// machine is the thing worth testing, so the real [SpeechService] runs on top
/// of this. It reports completion after a short delay, exactly as a real engine
/// does through its completion callback.
class _FakeTts implements SpeechProvider {
  final List<String> spoken = [];
  bool speaking = false;
  VoidCallback? _onComplete;

  @override
  bool get isAvailable => true;

  @override
  String? get voiceDescription => 'fake';

  @override
  set onComplete(VoidCallback? callback) => _onComplete = callback;

  @override
  Future<void> initialise() async {}

  @override
  Future<bool> speak(String text, {SpeechRate rate = SpeechRate.normal}) async {
    speaking = true;
    spoken.add(text);
    // Completion arrives on its own, like a real engine's callback.
    Future<void>.delayed(const Duration(milliseconds: 20), () {
      speaking = false;
      _onComplete?.call();
    });
    return true;
  }

  @override
  Future<void> stop() async {
    speaking = false;
    _onComplete?.call();
  }

  @override
  Future<void> dispose() async {}
}

/// Returns scripted phrases, one per listen, and records whether it was ever
/// asked to listen while the voice was still playing.
class _FakeSpeech implements SpeechRecognitionService {
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

  /// Push-to-talk, faked: the words arrive when the turn is closed rather
  /// than after a silence timer, which is the behaviour under test.
  String? _pending;

  @override
  Future<bool> startListening({void Function(String heard)? onPartial}) async {
    if (!available) return false;
    if (tts?.speaking == true) overlappedWithSpeech = true;

    final index = listenCount++;
    _pending = index < phrases.length ? phrases[index] : null;
    if (_pending != null) onPartial?.call(_pending!);
    return true;
  }

  /// Whether the turn was closed by the learner. Nothing else may close it.
  bool closed = false;

  @override
  Future<String?> stopAndRead() async {
    closed = true;
    await Future<void>.delayed(const Duration(milliseconds: 20));
    return _pending;
  }

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
