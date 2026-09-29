import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:just_audio/just_audio.dart';
import 'package:wordos/core/api/wordos_api.dart';
import 'package:wordos/core/audio/cloud_speech_provider.dart';
import 'package:wordos/core/audio/speech_provider.dart';
import 'package:wordos/core/audio/speech_service.dart';
import 'package:wordos/core/models/models.dart';
import 'package:wordos/features/session/clip_playback.dart';

/// The server's voice behind the phone's contract (ADR-108, ADR-110).
///
/// The player and the phone's voice are fakes — a test binary has neither —
/// and so is the server, which answers with audio and a time for every word
/// computed the way the real one reports them. What is under test is what the
/// rest of the app relies on: who speaks, which slice of audio is played for
/// a sentence, which word is reported when, and that a failure is the phone's
/// voice rather than silence.
void main() {
  late Directory temp;
  late _FakePlayer player;
  late _FakeDevice device;
  late _FakeServer server;
  late DateTime now;

  setUp(() async {
    temp = await Directory.systemTemp.createTemp('wordos_voice_test');
    player = _FakePlayer();
    device = _FakeDevice();
    server = _FakeServer();
    now = DateTime(2026, 9, 29, 12);
  });

  tearDown(() async {
    // A fetch the test did not wait for — audio prepared ahead, the rest of
    // a long line — may still be writing its clip here. Let it land, and do
    // not fail a test over a race in deleting a temporary folder.
    await Future<void>.delayed(const Duration(milliseconds: 50));
    try {
      if (await temp.exists()) await temp.delete(recursive: true);
    } on FileSystemException {
      // A clip arrived mid-delete; the system reaps its temp folder anyway.
    }
  });

  HybridSpeechProvider build({
    Duration fetchTimeout = const Duration(seconds: 5),
    Duration? shortTextTimeout,
    int maxConcurrentFetches = 2,
  }) {
    final provider = HybridSpeechProvider(
      device: device,
      synthesize: server.synthesize,
      player: player,
      directory: () async => temp,
      fetchTimeout: fetchTimeout,
      shortTextTimeout: shortTextTimeout ?? fetchTimeout,
      maxConcurrentFetches: maxConcurrentFetches,
      now: () => now,
    );
    return provider;
  }

  group('who speaks', () {
    test('everything that says who is speaking is the server voice', () {
      // One voice for the whole app, by the product owner's decision.
      expect(cloudSpeaks('tutor:123'), isTrue);
      expect(cloudSpeaks('sentence:9'), isTrue);
      expect(cloudSpeaks('word:abandon'), isTrue);
      expect(cloudSpeaks('slow:word:abandon'), isTrue);
      expect(cloudSpeaks('pronounce:deliver'), isTrue);
      expect(cloudSpeaks('placement:Hello'), isTrue);
      // No id, no telling who is speaking: the phone.
      expect(cloudSpeaks(''), isFalse);
    });

    test('only Listening may be cut out of a longer clip', () {
      expect(slicedFromLonger('listening:1'), isTrue);
      expect(slicedFromLonger('sentence:1'), isTrue);
      expect(slicedFromLonger('replay:1'), isTrue);
      expect(slicedFromLonger('word:1'), isFalse);
      expect(slicedFromLonger('pronounce:tea'), isFalse);
      expect(slicedFromLonger('tutor:1'), isFalse);
    });

    test('a word button is fetched and played whole in the server voice',
        () async {
      final provider = build();

      expect(await provider.speakFor('word:tea', 'tea'), isTrue);

      expect(server.requests, ['tea']);
      expect(device.spoken, isEmpty);
      final source = player.sources.single;
      expect(source.start, Duration.zero);
      expect(source.end, Duration(milliseconds: _duration('tea')));
    });

    test('a word is never cut out of a passage that contains it', () async {
      final provider = build();
      const passage = 'I like green tea. It is warm.';
      provider.prepare('listening:1', passage);
      await pumpEventQueue();

      await provider.speakFor('pronounce:tea', 'tea');

      // Asked for on its own, not sliced from "green tea. It".
      expect(server.requests, [passage, 'tea']);
      expect(player.sources.single.start, Duration.zero);
    });

    test('the slow word is the same audio, not a second request', () async {
      final provider = build();

      await provider.speakFor('word:tea', 'tea');
      await provider.speakFor('slow:word:tea', 'tea', rate: SpeechRate.slow);

      expect(server.requests, ['tea']);
      expect(player.speed, 0.65);
    });

    test('a word that is slow to arrive is said by the phone sooner',
        () async {
      server.delay = const Duration(seconds: 2);
      final provider = build(
        fetchTimeout: const Duration(seconds: 5),
        shortTextTimeout: const Duration(milliseconds: 50),
      );

      final started = DateTime.now();
      await provider.speakFor('word:tea', 'tea');

      expect(device.spoken, ['tea']);
      expect(DateTime.now().difference(started),
          lessThan(const Duration(seconds: 1)));
    });
  });

  group("ready: the tutor's text waits for its voice (ADR-116)", () {
    test('completes when the line can start, and it then starts at once',
        () async {
      server.gate = Completer<void>();
      final provider = build();
      var ready = false;
      unawaited(provider.ready('tutor:1', 'Hello there.')
          .then((_) => ready = true));
      await pumpEventQueue();
      expect(ready, isFalse, reason: 'the voice has not arrived yet');

      server.gate!.complete();
      for (var i = 0; i < 100 && !ready; i++) {
        await Future<void>.delayed(const Duration(milliseconds: 10));
      }
      expect(ready, isTrue);

      await provider.speakFor('tutor:1', 'Hello there.');
      expect(server.requests, ['Hello there.'], reason: 'fetched once');
      expect(player.playing, isTrue);
    });

    test('a long line is ready once its first piece is', () async {
      final provider = build();
      const reply = 'That is very kind of you, and I think your grandmother '
          'will be happy. Many people like to cook for their family. '
          'Do you often cook at home? What is your favourite dish to make? '
          'Try to use the word "deliver" in your answer.';

      await provider.ready('tutor:1', reply);

      expect(server.requests.first, speechPieces(reply).first);
    });

    test('a failing voice does not hold the line back', () async {
      server.fails = true;
      final provider = build();

      await provider.ready('tutor:1', 'Hello there.');

      await provider.speakFor('tutor:1', 'Hello there.');
      expect(device.spoken, isNotEmpty, reason: 'the phone says it instead');
    });

    test('a slow voice is waited for no longer than the line itself would',
        () async {
      server.delay = const Duration(seconds: 2);
      final provider = build(fetchTimeout: const Duration(milliseconds: 50));

      final started = DateTime.now();
      await provider.ready('tutor:1', 'Hello there.');

      expect(DateTime.now().difference(started),
          lessThan(const Duration(seconds: 1)));
    });
  });

  group('fetching ahead', () {
    test('prepared audio is fetched in the order it was asked for', () async {
      server.gate = Completer<void>();
      final provider = build(maxConcurrentFetches: 1);

      for (final line in ['One.', 'Two.', 'Three.']) {
        provider.prepare('sentence:$line', line);
      }
      await pumpEventQueue();
      expect(server.requests, ['One.'], reason: 'one slot, the rest queue');

      server.gate!.complete();
      for (var i = 0; i < 100 && server.requests.length < 3; i++) {
        await Future<void>.delayed(const Duration(milliseconds: 10));
      }
      expect(server.requests, ['One.', 'Two.', 'Three.']);
    });

    test('what the learner asks for jumps the background queue', () async {
      server.gate = Completer<void>();
      final provider = build(maxConcurrentFetches: 1);
      for (final line in ['One.', 'Two.', 'Three.']) {
        provider.prepare('sentence:$line', line);
      }
      await pumpEventQueue();

      // The learner presses play on the third before the first has arrived.
      unawaited(provider.speakFor('sentence:3', 'Three.'));
      await pumpEventQueue();
      server.gate!.complete();
      // Each answer is written to disk before its slot is freed — real I/O,
      // which an event-queue pump does not wait for.
      for (var i = 0; i < 100 && server.requests.length < 3; i++) {
        await Future<void>.delayed(const Duration(milliseconds: 10));
      }

      expect(server.requests, ['One.', 'Three.', 'Two.']);
    });

    test('prepared audio plays without asking the server again', () async {
      final provider = build();
      provider.prepare('pronounce:deliver', 'deliver');
      await pumpEventQueue();

      await provider.speakFor('pronounce:deliver', 'deliver');

      expect(server.requests, ['deliver']);
      expect(player.playing, isTrue);
    });

    test('a prepared line that failed is asked for again when played',
        () async {
      server.fails = true;
      final provider = build();
      provider.prepare('sentence:1', 'Hello there.');
      await pumpEventQueue();

      server.fails = false;
      await provider.speakFor('sentence:1', 'Hello there.');

      expect(server.requests, ['Hello there.', 'Hello there.']);
      expect(device.spoken, isEmpty);
    });
  });

  group('the tutor', () {
    test('a reply is fetched whole and played whole in the server voice',
        () async {
      final provider = build();
      const reply = 'That is very kind of you.';

      expect(await provider.speakFor('tutor:1', reply), isTrue);

      expect(server.requests, [reply]);
      expect(device.spoken, isEmpty);
      final source = player.sources.single;
      expect(source.start, Duration.zero);
      expect(source.end, Duration(milliseconds: _duration(reply)));
      expect(player.speed, 1.0);
      expect(player.playing, isTrue);
    });

    test('each word is reported as it starts, then the end', () async {
      final provider = build();
      final starts = <int>[];
      var completed = 0;
      provider
        ..onWordBoundary = starts.add
        ..onComplete = () => completed++;
      const reply = 'I like green tea.';

      await provider.speakFor('tutor:1', reply);
      // Positions are relative to the slice, as the platform reports them.
      player.moveTo(const Duration(milliseconds: 0));
      player.moveTo(const Duration(milliseconds: 420));
      player.moveTo(const Duration(milliseconds: 1250));
      await pumpEventQueue();

      // "I" at 0, then "green" at 7 — by 450 ms the voice is on "green", and
      // "like", passed between two position reports, is not reported late:
      // the playhead goes where the voice is. Then "tea" at 13.
      expect(starts, [0, 7, 13]);
      expect(completed, 0);

      player.finish();
      await pumpEventQueue();
      expect(completed, 1);
    });

    test('a long reply starts on a short first piece', () async {
      final provider = build();
      const reply = 'It is nice that City shop helps you with your errands. '
          'My city is getting very busy, so the owners want to build a bigger '
          'shop to help more people. Do you think the shop owners should '
          'expand their business next year, or keep it small and friendly? '
          'Try to use the word expand in your answer.';

      await provider.speakFor('tutor:1', reply);

      expect(server.requests.length, greaterThan(1));
      // Two short sentences, not everything up to the first long one.
      expect(server.requests.first,
          'It is nice that City shop helps you with your errands. My city is '
          'getting very busy, so the owners want to build a bigger shop to '
          'help more people.');
      expect(server.requests.join(' '), reply);
      expect(player.playing, isTrue);
    });

    test('the slow voice is the same audio played slower', () async {
      final provider = build();

      await provider.speakFor('tutor:1', 'Hello there.', rate: SpeechRate.slow);

      expect(player.speed, provider.slowSpeed);
      expect(server.requests.length, 1);
    });
  });

  group('Listening', () {
    const passage = 'Sara lives in a small village. She walks to school '
        'every morning. The road is long but beautiful. In spring the fields '
        'turn green and the air smells of flowers. She never complains about '
        'the walk.';

    test('a passage is fetched in a few pieces, not one per sentence', () async {
      final provider = build();
      final long = List.filled(3, passage).join(' ');

      provider.prepare('listening:1', long);
      await pumpEventQueue();

      expect(server.requests.length, inInclusiveRange(2, 3));
      expect(server.requests.join(' '), long,
          reason: 'the pieces together are the passage, nothing lost');
      expect(server.requests.first.length, lessThan(long.length ~/ 2),
          reason: 'the first piece is short, so the first word comes fast');
    });

    test('a sentence is played as its slice of the piece that holds it',
        () async {
      final provider = build();
      final starts = <int>[];
      provider.onWordBoundary = starts.add;
      provider.prepare('listening:1', passage);
      await pumpEventQueue();
      final requestsBefore = server.requests.length;

      const sentence = 'The road is long but beautiful.';
      await provider.speakFor('listening:1', sentence);

      expect(server.requests.length, requestsBefore,
          reason: 'already fetched as part of a piece');
      final piece = server.requests.firstWhere((p) => p.contains(sentence));
      final base = piece.indexOf(sentence);
      final firstWord = _timeOf(piece, base);
      final source = player.sources.single;
      // A little air before the first word and after the last.
      expect(source.start, Duration(milliseconds: firstWord - 60));
      expect(source.end!.inMilliseconds,
          greaterThan(_timeOf(piece, base + sentence.length - 10)));

      // Word positions are reported relative to the sentence handed over.
      player.moveTo(const Duration(milliseconds: 60));
      await pumpEventQueue();
      expect(starts, [0]);
    });

    test('resuming part-way through a sentence plays from that word',
        () async {
      final provider = build();
      provider.prepare('listening:1', passage);
      await pumpEventQueue();

      await provider.speakFor('listening:1', 'long but beautiful.');

      final piece = server.requests
          .firstWhere((p) => p.contains('The road is long but beautiful.'));
      final at = piece.indexOf('long but beautiful.');
      expect(player.sources.single.start,
          Duration(milliseconds: _timeOf(piece, at) - 60));
    });

    test('ClipPlayback asks for the passage before its first sentence',
        () async {
      final provider = build();
      final speech = SpeechService(provider: provider);
      final clip = ClipPlayback(
        speech: speech,
        text: passage,
        idPrefix: 'listening',
        prepareEarly: true,
      );
      await pumpEventQueue();

      expect(server.requests, isNotEmpty,
          reason: 'fetched while the learner reads, never played early');
      expect(player.sources, isEmpty);
      clip.dispose();
      speech.dispose();
    });

    test('the whole passage replayed at the end plays piece after piece',
        () async {
      final provider = build();
      final starts = <int>[];
      var completed = 0;
      provider
        ..onWordBoundary = starts.add
        ..onComplete = () => completed++;
      final long = List.filled(8, passage).join(' ');

      await provider.speakFor('replay:1', long);
      final pieces = server.requests;
      expect(pieces.length, greaterThan(1));
      expect(pieces.every((p) => p.length <= 1400), isTrue);

      // Through every piece; positions are into the whole text.
      var played = 0;
      while (completed == 0 && played < 50) {
        player.moveTo(const Duration(milliseconds: 1));
        await pumpEventQueue();
        player.finish();
        await pumpEventQueue();
        played++;
      }
      expect(completed, 1);
      expect(player.sources.length, pieces.length);
      expect(starts.last, greaterThan(long.length ~/ 2));
    });
  });

  group('failure is the phone\'s voice, never silence', () {
    test('a server that cannot speak hands the line to the phone', () async {
      server.fails = true;
      final provider = build();

      expect(await provider.speakFor('tutor:1', 'Hello.'), isTrue);

      expect(device.spoken, ['Hello.']);
      expect(player.playing, isFalse);
    });

    test('a server too slow to answer hands the line to the phone', () async {
      server.delay = const Duration(seconds: 2);
      final provider = build(fetchTimeout: const Duration(milliseconds: 50));

      expect(await provider.speakFor('tutor:1', 'Hello.'), isTrue);

      expect(device.spoken, ['Hello.']);
    });

    test('after a few failures the cloud is left alone for a while', () async {
      server.fails = true;
      final provider = build();

      for (var i = 0; i < 3; i++) {
        await provider.speakFor('tutor:$i', 'Line $i.');
      }
      final asked = server.requests.length;
      await provider.speakFor('tutor:9', 'Line nine.');

      expect(server.requests.length, asked,
          reason: 'no wait on a request that is not going to be answered');
      expect(device.spoken.last, 'Line nine.');

      // And tried again once the pause is over.
      server.fails = false;
      now = now.add(const Duration(minutes: 4));
      await provider.speakFor('tutor:10', 'Back again.');
      expect(server.requests.last, 'Back again.');
      expect(player.playing, isTrue);
    });

    test('the phone finishing is reported; a stale phone event is not',
        () async {
      final provider = build();
      var completed = 0;
      provider.onComplete = () => completed++;

      // No id: nobody to route by, so the phone speaks it.
      await provider.speak('tea');
      device.finish();
      expect(completed, 1);

      await provider.speakFor('tutor:1', 'Hello there.');
      // The phone reporting a cancel while the server's voice is speaking.
      device.finish();
      expect(completed, 1);
    });
  });

  group('stopping', () {
    test('stopped while the voice is on its way, nothing starts talking',
        () async {
      final gate = server.gate = Completer<void>();
      final provider = build();

      final speaking = provider.speakFor('tutor:1', 'Hello there.');
      await pumpEventQueue();
      await provider.stop();
      gate.complete(); // the audio arrives after the learner stopped it
      expect(await speaking, isTrue, reason: 'stopped is not failed');

      expect(player.playing, isFalse);
      expect(device.spoken, isEmpty);
    });

    test('speakToCompletion returns at once when stopped during the fetch',
        () async {
      final gate = server.gate = Completer<void>();
      final speech = SpeechService(provider: build());

      final done = speech.speakToCompletion('tutor:1', 'Hello there.');
      await pumpEventQueue();
      expect(server.requests, hasLength(1), reason: 'the fetch is under way');
      await speech.stop();
      gate.complete();

      // Not held until the completion timeout.
      expect(await done.timeout(const Duration(seconds: 1)), isTrue);
      expect(speech.isSpeaking, isFalse);
      speech.dispose();
    });
  });

  test('word starts are counted, so a clip times speech, not the fetch',
      () async {
    final speech = SpeechService(provider: build());

    await speech.speak('tutor:1', 'I like green tea.');
    final before = speech.wordEvents;
    player.moveTo(const Duration(milliseconds: 10));
    await pumpEventQueue();

    expect(speech.wordEvents, before + 1);
    speech.dispose();
  });

  group('speechPieces', () {
    test('every piece is an exact part of the text, in order', () {
      const text = 'One. Two two. Three three three!\n\nFour? "Five," she said. Six.';
      final pieces = speechPieces(text, firstTarget: 5, target: 12);

      var cursor = 0;
      for (final piece in pieces) {
        final at = text.indexOf(piece, cursor);
        expect(at, greaterThanOrEqualTo(0), reason: piece);
        cursor = at + piece.length;
      }
    });

    test('never cuts inside a sentence Listening plays as one', () {
      const text = 'Mr. Smith arrived at 3.30 today. He was late. It rained.';
      final sentences = splitSentences(text);

      for (final piece in speechPieces(text, firstTarget: 1, target: 1)) {
        // Each piece is a run of whole sentences.
        expect(sentences.any((s) => piece.contains(s) || s.contains(piece)),
            isTrue,
            reason: piece);
      }
    });

    test('a run-on longer than one request is split at a space', () {
      final text = List.filled(400, 'word').join(' ');
      final pieces = speechPieces(text, maximum: 300);

      expect(pieces.every((p) => p.length <= 300), isTrue);
      expect(pieces.join(' '), text);
    });
  });
}

// ── Fakes ────────────────────────────────────────────────────────────────────

/// 60 ms a character, starting at 0: easy to reason about in assertions.
int _timeOf(String text, int char) => char * 60;
int _duration(String text) => text.length * 60 + 200;

class _FakeServer {
  final requests = <String>[];
  bool fails = false;
  Duration delay = Duration.zero;

  /// Holds every answer until the test opens it — for "stopped while the
  /// audio was on its way", without depending on how fast the machine is.
  Completer<void>? gate;

  Future<SynthesizedSpeech> synthesize(String text) async {
    requests.add(text);
    if (gate != null) await gate!.future;
    if (delay > Duration.zero) await Future<void>.delayed(delay);
    if (fails) {
      throw const ApiException('VOICE_UNAVAILABLE', 'down', statusCode: 503);
    }
    final words = RegExp(r"[A-Za-z0-9]+(?:['’\-][A-Za-z0-9]+)*")
        .allMatches(text)
        .map((m) => SpokenWord(
              startMs: _timeOf(text, m.start),
              endMs: _timeOf(text, m.end),
              charStart: m.start,
              charEnd: m.end,
            ))
        .toList();
    return SynthesizedSpeech(
      audio: Uint8List.fromList([1, 2, 3, 4]),
      mimeType: 'audio/mpeg',
      durationMs: _duration(text),
      words: words,
      timing: 'aligned',
    );
  }
}

class _FakeDevice implements SpeechProvider {
  final spoken = <String>[];
  VoidCallback? _complete;

  void finish() => _complete?.call();

  @override
  Future<bool> speak(String text, {SpeechRate rate = SpeechRate.normal}) async {
    spoken.add(text);
    return true;
  }

  @override
  Future<void> stop() async {}

  @override
  Future<void> initialise() async {}

  @override
  set onComplete(VoidCallback? callback) => _complete = callback;

  @override
  set onWordBoundary(void Function(int start)? callback) {}

  @override
  bool get isAvailable => true;

  @override
  String? get voiceDescription => 'fake';

  @override
  Future<void> dispose() async {}
}

class _FakePlayer implements AudioPlayer {
  final sources = <ClippingAudioSource>[];
  @override
  double speed = 1.0;
  @override
  bool playing = false;
  final _positions = StreamController<Duration>.broadcast();
  final _states = StreamController<PlayerState>.broadcast();

  void moveTo(Duration position) => _positions.add(position);

  void finish() {
    playing = false;
    _states.add(PlayerState(false, ProcessingState.completed));
  }

  @override
  Future<Duration?> setAudioSource(
    AudioSource audioSource, {
    bool preload = true,
    int? initialIndex,
    Duration? initialPosition,
  }) async {
    sources.add(audioSource is ClippingAudioSource
        ? audioSource
        : ClippingAudioSource(
            child: audioSource as UriAudioSource,
            start: Duration.zero,
          ));
    return null;
  }

  @override
  Future<void> setSpeed(double speed) async => this.speed = speed;

  @override
  Future<void> play() async => playing = true;

  @override
  Future<void> stop() async => playing = false;

  @override
  Future<void> dispose() async {}

  @override
  Stream<Duration> createPositionStream({
    int steps = 800,
    Duration minPeriod = const Duration(milliseconds: 200),
    Duration maxPeriod = const Duration(milliseconds: 200),
  }) =>
      _positions.stream;

  @override
  Stream<PlayerState> get playerStateStream => _states.stream;

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}
