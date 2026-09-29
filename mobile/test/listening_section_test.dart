import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/app/wordos_app.dart';
import 'package:wordos/core/audio/cloud_speech_provider.dart';
import 'package:wordos/core/audio/speech_provider.dart';
import 'package:wordos/core/audio/speech_service.dart';
import 'package:wordos/core/widgets/app_widgets.dart';
import 'package:wordos/core/widgets/speaker_button.dart';
import 'package:wordos/features/session/session_widgets.dart';

import 'support/test_harness.dart';

/// The Listening section, reviewed against Reading.
///
/// Most of what was asked for here already existed, because the two skills
/// share a screen — the header, the level control and the passage direction are
/// one implementation. What did not exist: audio that stops when the learner
/// moves on, and the recording handed back at the end.
void main() {
  late _FakeTts tts;

  Future<void> openListening(
    WidgetTester tester, {
    Locale locale = const Locale('en'),
    _FakeTts? voice,
  }) async {
    tts = voice ?? _FakeTts();
    tester.view.physicalSize = const Size(1200, 6000);
    tester.view.devicePixelRatio = 3;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(ProviderScope(
      overrides: [
        ...testOverrides(locale: locale),
        speechServiceProvider
            .overrideWith((ref) => SpeechService(provider: tts)),
      ],
      child: const WordOsApp(),
    ));
    await tester.pumpAndSettle();
    await tester.tap(find.byType(FilledButton).first);
    await tester.pumpAndSettle();
    await tester.tap(
        find.text(locale.languageCode == 'ar' ? 'الاستماع' : 'Listening'));
    await tester.pumpAndSettle();
  }

  /// Walks the questions until one carries [finder], answering as it goes.
  ///
  /// The first five questions are about the clip as a whole; the ones about a
  /// single word come after them, and those are the ones that carry a sentence
  /// and a word to pronounce.
  Future<void> walkTo(WidgetTester tester, Finder finder) async {
    for (var guard = 0; guard < 30 && finder.evaluate().isEmpty; guard++) {
      if (find.byType(OptionTile).evaluate().isEmpty) break;
      await tester.tap(find.byType(OptionTile).first);
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Check'));
      await tester.pumpAndSettle();
      final next = find.widgetWithText(FilledButton, 'Next');
      final finish = find.widgetWithText(FilledButton, 'Finish');
      await tester.tap(next.evaluate().isNotEmpty ? next : finish);
      await tester.pumpAndSettle();
    }
  }

  testWidgets('the voice of the whole session is fetched ahead, in order',
      (tester) async {
    // ADR-110: play answers at once because the audio was already asked for
    // — the passage first, then each question's sentence and word, in the
    // order the learner meets them — never on the press itself.
    final voice = _FakeRoutedTts();
    await openListening(tester, voice: voice);

    final ids = voice.prepared.map((p) => p.id).toList();
    expect(ids, isNotEmpty);
    expect(ids.first, startsWith('listening:'),
        reason: 'the passage on screen comes before the questions');

    final sentences = voice.prepared
        .where((p) => p.id.startsWith('sentence:'))
        .map((p) => p.text)
        .toList();
    final words = voice.prepared
        .where((p) => p.id.startsWith('pronounce:'))
        .map((p) => p.text)
        .toList();
    expect(sentences, isNotEmpty);
    expect(words, isNotEmpty);
    expect(voice.spoken, isEmpty, reason: 'fetched ahead, never played ahead');

    // The first word question's sentence is the first sentence asked for, so
    // the question the learner reaches first is the one ready first.
    await tester.tap(find.widgetWithText(FilledButton, 'I finished listening'));
    await tester.pumpAndSettle();
    await walkTo(tester, find.byType(SentencePlayer));
    final shown = tester.widget<SentencePlayer>(find.byType(SentencePlayer));
    expect(shown.text, sentences.first);
    final pronounced =
        tester.widget<WordPronunciation>(find.byType(WordPronunciation));
    expect(pronounced.word, words.first);
  });

  testWidgets('the header matches Reading exactly', (tester) async {
    await openListening(tester);

    final title = tester.widget<Text>(find.descendant(
        of: find.byType(AppBar), matching: find.text('Listening')));
    final badge = tester.widget<LevelBadge>(find.byType(LevelBadge).first);

    // The same numbers Reading uses — they share one implementation, and this
    // pins that they have not drifted apart.
    expect(title.style?.fontSize, greaterThanOrEqualTo(22));
    expect(badge.size, greaterThanOrEqualTo(14));
  });

  testWidgets('the level can be changed here too, before the questions',
      (tester) async {
    await openListening(tester);

    expect(find.byIcon(Icons.expand_more_rounded), findsOneWidget);

    await tester.tap(find.byType(LevelBadge).first);
    await tester.pumpAndSettle();
    expect(find.text('Change the level'), findsOneWidget);

    await tester.tap(find.text('A2').last);
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();

    expect(tester.takeException(), isNull);
  });

  testWidgets('the audio stops when the learner leaves the passage',
      (tester) async {
    await openListening(tester);

    // The clip waits to be asked now (ADR-080), so ask.
    await tester.tap(find.byIcon(Icons.play_arrow_rounded));
    await tester.pumpAndSettle();
    expect(tts.spoken, isNotEmpty);

    final stopsBefore = tts.stops;
    final spokenBefore = tts.spoken.length;

    await tester.tap(find.widgetWithText(FilledButton, 'I finished listening'));
    await tester.pumpAndSettle();

    // For Listening this is not merely untidy: a script still playing over the
    // questions is handing the learner the answers.
    expect(tts.stops, greaterThan(stopsBefore),
        reason: 'audio must stop when the section changes');

    // And it must stay stopped. The clip is spoken one sentence at a time
    // (ADR-068), and a cancelled sentence reaches the loop through the very
    // same callback as a finished one — so a loop that does not check goes on
    // to read the *next* line out over the questions. Nothing may be spoken
    // after the section changed (ADR-080).
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();
    expect(tts.spoken.length, spokenBefore,
        reason: 'the clip must not read on after the learner left it');
  });

  testWidgets('nothing is left speaking after moving between questions',
      (tester) async {
    await openListening(tester);
    await tester.tap(find.widgetWithText(FilledButton, 'I finished listening'));
    await tester.pumpAndSettle();

    final speech = ProviderScope.containerOf(
      tester.element(find.byType(Scaffold).first),
    ).read(speechServiceProvider);

    // Start something playing, whether or not the question does it itself.
    await speech.speak('probe', 'a sentence still being read aloud');
    expect(speech.isSpeaking, isTrue);

    await tester.tap(find.byType(OptionTile).first);
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Check'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Next'));
    await tester.pumpAndSettle();

    // The rule, stated generally: leaving a section silences it. A sentence
    // talking over the next question is a page that did not turn.
    expect(speech.isSpeaking, isFalse,
        reason: 'a sentence must not talk over the next question');
  });

  testWidgets('the recording comes back with the result, before the text',
      (tester) async {
    await openListening(tester);
    await tester.tap(find.widgetWithText(FilledButton, 'I finished listening'));
    await tester.pumpAndSettle();

    for (var guard = 0; guard < 30; guard++) {
      if (find.text('Session complete').evaluate().isNotEmpty) break;
      if (find.byType(OptionTile).evaluate().isEmpty) break;

      await tester.tap(find.byType(OptionTile).first);
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Check'));
      await tester.pumpAndSettle();

      final next = find.widgetWithText(FilledButton, 'Next');
      final finish = find.widgetWithText(FilledButton, 'Finish');
      await tester.tap(next.evaluate().isNotEmpty ? next : finish);
      await tester.pumpAndSettle();
    }

    expect(find.text('Session complete'), findsWidgets);

    // During the session the audio was the test; afterwards it is study
    // material, and this is the moment the learner most wants it back.
    expect(find.byType(ReplayPlayer), findsOneWidget,
        reason: 'the recording should be replayable from the result');

    // Nothing plays by itself here — they are reading their score.
    final spokenBefore = tts.spoken.length;
    await tester.pump(const Duration(seconds: 1));
    expect(tts.spoken.length, spokenBefore,
        reason: 'the result screen must not start talking on its own');

    // Order: answers, then audio, then text (§7).
    final replayY = tester.getTopLeft(find.byType(ReplayPlayer)).dy;
    final textY = tester.getTopLeft(find.text('Show transcript')).dy;
    expect(replayY, lessThan(textY),
        reason: 'hearing it again comes before reading it');
  });

  testWidgets('the transcript reads left-to-right in the Arabic app',
      (tester) async {
    await openListening(tester, locale: const Locale('ar'));
    await tester.tap(find.widgetWithText(FilledButton, 'أنهيت الاستماع'));
    await tester.pumpAndSettle();

    for (var guard = 0; guard < 30; guard++) {
      if (find.byType(HighlightedPassage).evaluate().isNotEmpty) break;
      if (find.byType(OptionTile).evaluate().isEmpty) break;

      await tester.tap(find.byType(OptionTile).first);
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'تحقق'));
      await tester.pumpAndSettle();

      final next = find.widgetWithText(FilledButton, 'التالي');
      final finish = find.widgetWithText(FilledButton, 'إنهاء');
      await tester.tap(next.evaluate().isNotEmpty ? next : finish);
      await tester.pumpAndSettle();
    }

    final passage = find.byType(HighlightedPassage);
    expect(passage, findsOneWidget);

    // Exactly what Reading does — the same widget, so the same guarantee.
    expect(
      Directionality.of(tester.element(
          find.descendant(of: passage, matching: find.byType(Text)))),
      TextDirection.ltr,
    );
  });


  // ── What Reading gained, Listening now has too (ADR-068) ────────────────

  testWidgets('the clip is named before it is heard', (tester) async {
    await openListening(tester);

    // A title tells the learner what is coming without giving any of it away,
    // which is what an exam's listening section prints above the audio.
    expect(find.text('A Morning Announcement'), findsOneWidget);
    expect(find.byType(HighlightedPassage), findsNothing,
        reason: 'the transcript itself stays hidden until the test is over');

    // And it is genuinely read *before*: the title is worth nothing if the
    // voice is already talking over it (ADR-080).
    expect(tts.spoken, isEmpty,
        reason: 'the clip waits for the learner, not the other way round');
  });

  testWidgets('an English question is not punctuated backwards in Arabic',
      (tester) async {
    await openListening(tester, locale: const Locale('ar'));
    await tester.tap(find.widgetWithText(FilledButton, 'أنهيت الاستماع'));
    await tester.pumpAndSettle();

    // The question and the options are written by the server in English, and
    // an English sentence inheriting the Arabic paragraph direction loses its
    // punctuation to the front: `What does "x" mean here?` renders as
    // `?What does "x" mean here` (ADR-082).
    final prompt = find.byType(AutoDirectionText).first;
    expect(
      Directionality.of(tester.element(
          find.descendant(of: prompt, matching: find.byType(Text)))),
      TextDirection.ltr,
    );
  });

  /// Drops the session to a band that answers in Arabic (ADR-088).
  ///
  /// From B1 up the options are English definitions, so a test about Arabic
  /// options has to put the learner somewhere Arabic options exist. The level
  /// control on this screen is the learner's own way of doing exactly that.
  Future<void> dropToAnArabicBand(WidgetTester tester) async {
    await tester.tap(find.byType(LevelBadge).first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('A2').last);
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 2));
    await tester.pumpAndSettle();
  }

  testWidgets('an Arabic option still reads right-to-left', (tester) async {
    // Run in the English interface on purpose: the rule is that the direction
    // follows the *text*, so an Arabic meaning must read right-to-left even
    // where nothing around it does.
    await openListening(tester);
    await dropToAnArabicBand(tester);
    await tester.tap(find.widgetWithText(FilledButton, 'I finished listening'));
    await tester.pumpAndSettle();

    // The other half of the rule: a word question asks in English about Arabic
    // meanings, through the same widget. Forcing everything left-to-right
    // would simply move the problem.
    await walkTo(tester, find.byType(WordPronunciation));

    final arabicOption = find.descendant(
      of: find.byType(OptionTile),
      matching: find.byType(AutoDirectionText),
    );
    expect(arabicOption, findsWidgets);
    expect(
      Directionality.of(tester.element(find.descendant(
          of: arabicOption.first, matching: find.byType(Text)))),
      TextDirection.rtl,
    );
  });

  testWidgets('the sentence beside a question waits to be asked too',
      (tester) async {
    await openListening(tester);
    await tester.tap(find.widgetWithText(FilledButton, 'I finished listening'));
    await tester.pumpAndSettle();

    await walkTo(tester, find.byType(SentencePlayer));
    expect(find.byType(SentencePlayer), findsOneWidget);

    // The learner is still reading the question. A card that starts talking
    // unasked is one whose first tap is spent silencing it (ADR-080).
    expect(tts.spoken, isEmpty,
        reason: 'the question opens silently, with a control that offers audio');


    Finder inPlayer(IconData icon) => find.descendant(
          of: find.byType(SentencePlayer),
          matching: find.byIcon(icon),
        );

    expect(inPlayer(Icons.play_arrow_rounded), findsOneWidget);

    await tester.tap(inPlayer(Icons.play_arrow_rounded));
    await tester.pumpAndSettle();

    expect(tts.spoken, hasLength(1));
    expect(inPlayer(Icons.pause_rounded), findsOneWidget,
        reason: 'while it plays, the control pauses it');
  });

  testWidgets('pausing the question sentence continues rather than restarts',
      (tester) async {
    await openListening(tester);
    await tester.tap(find.widgetWithText(FilledButton, 'I finished listening'));
    await tester.pumpAndSettle();

    Finder inPlayer(IconData icon) => find.descendant(
          of: find.byType(SentencePlayer),
          matching: find.byIcon(icon),
        );

    await walkTo(tester, find.byType(SentencePlayer));
    await tester.tap(inPlayer(Icons.play_arrow_rounded));
    await tester.pumpAndSettle();

    // Move off the first line so "continued" and "restarted" differ. The
    // sentence card carries three sentences, not one (demo review §34).
    tts.finish();
    await tester.pumpAndSettle();
    final interrupted = tts.spoken.last;
    expect(interrupted, isNot(tts.spoken.first),
        reason: 'the card should hold more than a single line');

    await tester.tap(inPlayer(Icons.pause_rounded));
    await tester.pumpAndSettle();

    // The ordinary play triangle, not a replay face: the next tap continues.
    expect(inPlayer(Icons.play_arrow_rounded), findsOneWidget);
    expect(inPlayer(Icons.replay_rounded), findsNothing);

    await tester.tap(inPlayer(Icons.play_arrow_rounded));
    await tester.pumpAndSettle();

    expect(tts.spoken.last, interrupted,
        reason: 'it must pick up where it stopped, not at the start');
  });

  testWidgets('the slow voice keeps the place instead of starting over',
      (tester) async {
    await openListening(tester);
    await tester.tap(find.widgetWithText(FilledButton, 'I finished listening'));
    await tester.pumpAndSettle();

    await walkTo(tester, find.byType(SentencePlayer));
    await tester.tap(find.descendant(
      of: find.byType(SentencePlayer),
      matching: find.byIcon(Icons.play_arrow_rounded),
    ));
    await tester.pumpAndSettle();

    tts.finish();
    await tester.pumpAndSettle();
    final current = tts.spoken.last;

    // Slow used to be a second play button, so it always restarted the
    // sentence and there was no way to be playing slowly *and* pause. It is a
    // mode now, applied in place.
    await tester.tap(find.descendant(
      of: find.byType(SentencePlayer),
      matching: find.text('Slow'),
    ));
    await tester.pumpAndSettle();

    expect(tts.lastRate, SpeechRate.slow);
    expect(tts.spoken.last, current,
        reason: 'switching speed answers a question about *this* line');
  });

  testWidgets('the word behind a question can be heard but never seen',
      (tester) async {
    await openListening(tester);
    await tester.tap(find.widgetWithText(FilledButton, 'I finished listening'));
    await tester.pumpAndSettle();

    await walkTo(tester, find.byType(WordPronunciation));

    final found = find.byType(WordPronunciation).evaluate();
    expect(found, isNotEmpty,
        reason: 'a word question should offer the word aloud');
    final control = found.first.widget as WordPronunciation;

    // ── The rule (ADR-085) ────────────────────────────────────────────────
    //
    // Listening is the one skill where the learner must not know how the word
    // is written. They hear it inside a sentence and say what it meant; the
    // spelling is what Reading and Spelling are for. So the word appears
    // nowhere on this screen — not as the button's label, not in the question,
    // not in a tooltip a screen reader would read out.
    expect(control.revealSpelling, isFalse);
    expect(find.text(control.word), findsNothing,
        reason: 'the word must not be written anywhere on a listening question');
    expect(find.textContaining(control.word), findsNothing,
        reason: 'nor inside a longer line — the question names it in no '
            'language, which is what the prompt key is for');

    for (final tip in tester.widgetList<Tooltip>(find.descendant(
      of: find.byType(WordPronunciation),
      matching: find.byType(Tooltip),
    ))) {
      expect(tip.message, isNot(contains(control.word)),
          reason: 'a tooltip is read aloud by a screen reader and shown on a '
              'long press — it is part of the screen');
    }

    // It can still be *heard*, which is the whole point of the control.
    await tester.tap(find.descendant(
      of: find.byType(WordPronunciation),
      matching: find.byType(FilledButton),
    ));
    await tester.pumpAndSettle();
    expect(tts.spoken.last, control.word);
    expect(tts.lastRate, SpeechRate.normal);

    // And slowly, which is the whole point for a word heard once at speed.
    // Scoped to this card: the sentence player above has a Slow of its own,
    // and the two must stay separate — slowing the *sentence* down is not what
    // was asked for here.
    await tester.tap(find.descendant(
      of: find.byType(WordPronunciation),
      matching: find.widgetWithText(OutlinedButton, 'Slow'),
    ));
    await tester.pumpAndSettle();
    expect(tts.spoken.last, control.word);
    expect(tts.lastRate, SpeechRate.slow);
  });

  testWidgets('Reading still names the word it is asking about',
      (tester) async {
    // The other half of the rule, and the reason it is per-skill rather than
    // global: a Reading learner is looking at the word inside its sentences,
    // so a question that refused to name it would be answering a problem
    // Reading does not have (ADR-085).
    tts = _FakeTts();
    tester.view.physicalSize = const Size(1200, 6000);
    tester.view.devicePixelRatio = 3;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(ProviderScope(
      overrides: [
        ...testOverrides(),
        speechServiceProvider.overrideWith((ref) => SpeechService(provider: tts)),
      ],
      child: const WordOsApp(),
    ));
    await tester.pumpAndSettle();
    await tester.tap(find.byType(FilledButton).first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Reading'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'I finished reading'));
    await tester.pumpAndSettle();

    await walkTo(tester, find.byType(ContextPassage));

    expect(find.byType(ContextPassage), findsOneWidget,
        reason: 'Reading shows the word inside its neighbouring sentences');
    expect(find.textContaining('mean here?'), findsOneWidget);
    // Reading offers the word to be heard too since ADR-119 — as the compact
    // speaker pair beside its question, not Listening's card, which is
    // Listening's alone.
    expect(find.byType(WordPronunciation), findsNothing);
    expect(find.byType(WordSpeakerButtons), findsOneWidget);
  });


  testWidgets('an option is only a choice until it is checked', (tester) async {
    await openListening(tester);
    await tester.tap(find.widgetWithText(FilledButton, 'I finished listening'));
    await tester.pumpAndSettle();

    await tester.tap(find.byType(OptionTile).first);
    await tester.pumpAndSettle();

    expect(tester.widget<OptionTile>(find.byType(OptionTile).first).selected,
        isTrue);
    expect(find.widgetWithText(FilledButton, 'Next'), findsNothing);

    await tester.tap(find.widgetWithText(FilledButton, 'Check'));
    await tester.pumpAndSettle();

    expect(find.widgetWithText(FilledButton, 'Next'), findsOneWidget);
  });

  testWidgets('the recording can be reopened once the questions have started',
      (tester) async {
    await openListening(tester);
    await tester.tap(find.widgetWithText(FilledButton, 'I finished listening'));
    await tester.pumpAndSettle();

    expect(find.textContaining('Question 1 of'), findsOneWidget);

    // A comprehension question nobody can re-listen to is a memory test, and
    // this section does not measure memory.
    await tester.tap(find.byIcon(Icons.headphones_rounded));
    await tester.pumpAndSettle();

    expect(find.byType(Slider), findsOneWidget,
        reason: 'the player is back');

    await tester.tap(find.widgetWithText(FilledButton, 'Back to the questions'));
    await tester.pumpAndSettle();

    expect(find.textContaining('Question 1 of'), findsOneWidget);
  });

  testWidgets('each question speaks its own sentence, not the last one\'s',
      (tester) async {
    await openListening(tester);
    await tester.tap(find.widgetWithText(FilledButton, 'I finished listening'));
    await tester.pumpAndSettle();

    await walkTo(tester, find.byType(SentencePlayer));

    /// The sentence this question is carrying, and the sentence the player
    /// actually says when it is asked to play. They must be the same text.
    Future<void> playHere(String where) async {
      final shown =
          tester.widget<SentencePlayer>(find.byType(SentencePlayer)).text;
      tts.spoken.clear();
      await tester.tap(find.descendant(
        of: find.byType(SentencePlayer),
        matching: find.byIcon(Icons.play_arrow_rounded),
      ));
      await tester.pumpAndSettle();

      expect(tts.spoken, isNotEmpty, reason: 'nothing was spoken on $where');
      expect(shown, contains(tts.spoken.first),
          reason: 'on $where the player spoke text this question does not '
              'carry — the audio stayed on the previous question');
    }

    expect(find.byType(SentencePlayer), findsOneWidget);
    final first = tester.widget<SentencePlayer>(find.byType(SentencePlayer)).text;
    await playHere('the first word question');

    // On to the next question that carries a sentence of its own.
    for (var guard = 0; guard < 10; guard++) {
      await tester.tap(find.byType(OptionTile).first);
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Check'));
      await tester.pumpAndSettle();
      final next = find.widgetWithText(FilledButton, 'Next');
      if (next.evaluate().isEmpty) break;
      await tester.tap(next);
      await tester.pumpAndSettle();
      final player = find.byType(SentencePlayer);
      if (player.evaluate().isNotEmpty &&
          tester.widget<SentencePlayer>(player).text != first) {
        break;
      }
    }

    expect(find.byType(SentencePlayer), findsOneWidget,
        reason: 'a second word question was reached');
    await playHere('the second word question');
  });
}

class _FakeTts implements SpeechProvider {
  final List<String> spoken = [];
  int stops = 0;
  SpeechRate? lastRate;
  VoidCallback? _onComplete;

  @override
  bool get isAvailable => true;

  @override
  String? get voiceDescription => 'fake';

  @override
  set onComplete(VoidCallback? callback) => _onComplete = callback;

  /// Word-by-word progress, which a fake voice reports only when a test asks
  /// it to — see [speakWord] where one does.
  @override
  set onWordBoundary(void Function(int start)? callback) =>
      _onWordBoundary = callback;

  void Function(int start)? _onWordBoundary;

  /// Pretends the engine reached a word at [offset] in the current utterance.
  void speakWord(int offset) => _onWordBoundary?.call(offset);

  @override
  Future<void> initialise() async {}

  @override
  Future<bool> speak(String text, {SpeechRate rate = SpeechRate.normal}) async {
    spoken.add(text);
    lastRate = rate;
    return true;
  }

  @override
  Future<void> stop() async => stops++;

  void finish() => _onComplete?.call();

  @override
  Future<void> dispose() async {}
}

/// A voice that is told what is coming, as the server's voice is.
class _FakeRoutedTts extends _FakeTts implements RoutedSpeechProvider {
  final List<({String id, String text})> prepared = [];

  @override
  void prepare(String id, String text) => prepared.add((id: id, text: text));

  @override
  Future<void> ready(String id, String text) async => prepare(id, text);

  @override
  Future<bool> speakFor(String id, String text,
          {SpeechRate rate = SpeechRate.normal}) =>
      speak(text, rate: rate);

  @override
  Duration get startupAllowance => Duration.zero;
}
