import 'dart:async';
import 'dart:collection';
import 'dart:io';
import 'dart:math' as math;

import 'package:flutter/foundation.dart';
import 'package:just_audio/just_audio.dart';
import 'package:path_provider/path_provider.dart';

import '../models/models.dart';
import 'speech_provider.dart';

/// A provider that is told *who* is speaking, so it can choose a voice per
/// utterance, and that can be warned about text it will be asked to speak.
///
/// [SpeechService] checks for this rather than widening [SpeechProvider]:
/// every other provider — the phone's, and every fake in the test suite —
/// stays exactly as it was.
abstract class RoutedSpeechProvider implements SpeechProvider {
  /// Speaks [text] on behalf of the utterance [id].
  Future<bool> speakFor(
    String id,
    String text, {
    SpeechRate rate = SpeechRate.normal,
  });

  /// [text] is about to be spoken, piece by piece, as [id]. Starting its audio
  /// now is what keeps a gap from opening between sentences.
  void prepare(String id, String text);

  /// Completes when [text] can start speaking at once — its first piece has
  /// arrived — or when it never will: the fetch failed, or took longer than
  /// a line is allowed to wait, and the phone will say it instead. Never
  /// throws, and never waits longer than [speakFor] itself would.
  Future<void> ready(String id, String text);

  /// How long an utterance may take to *start*, on top of how long it takes
  /// to say — the voice is fetched before it plays.
  Duration get startupAllowance;
}

/// Which utterances the server's voice speaks: every one that says who is
/// speaking (ADR-110). The product owner chose one voice for the whole app —
/// the tutor, Listening, and every word button — once the voice was free.
/// A rule for everything rather than a list of callers, so a speaker button
/// added later cannot quietly keep the phone's voice.
bool cloudSpeaks(String utteranceId) => utteranceId.isNotEmpty;

/// Which utterances may be played as a slice of a longer clip: a Listening
/// sentence, cut from the passage or question audio fetched ahead. Anything
/// else — a word above all — is played only from a clip of exactly its own
/// text. A word cut out of a sentence carries the sentence's rhythm and the
/// first sound of the next word, which is not how a word is pronounced.
bool slicedFromLonger(String utteranceId) =>
    utteranceId.startsWith('listening:') ||
    utteranceId.startsWith('sentence:') ||
    utteranceId.startsWith('replay:');

/// The server's voice for everything, and the phone's for anything the
/// server cannot say in time (ADR-108, ADR-110).
///
/// **What the phone's voice gave for free, rebuilt.** Listening is played a
/// sentence at a time and positioned by the words the engine reports as it
/// goes (ADR-068, ADR-082). This keeps that contract exactly: [speak] still
/// takes one sentence, and [onWordBoundary] still fires as each word starts —
/// from times the server measured in the audio rather than from the engine.
/// Nothing above this layer knows the voice changed.
///
/// **How a sentence is found.** A passage is not fetched sentence by
/// sentence: forty requests per passage would be slow. [prepare] cuts it into a few pieces of several
/// sentences each — the first one short, so the first word arrives quickly —
/// and fetches them ahead. A sentence is then played as a slice of the piece
/// that contains it, from its first word's time to its last.
///
/// **Asked for first, fetched first.** Audio fetched ahead waits its turn
/// behind a small number of fetches in flight; a line the learner has just
/// asked to hear goes to the front of that queue (ADR-110).
///
/// **Failure is the phone's voice, never silence.** A fetch that fails or
/// takes too long hands that line to the phone. After a few in a row the
/// cloud is left alone for a while, so a learner offline is not made to wait
/// on every sentence for an answer that is not coming.
class HybridSpeechProvider implements RoutedSpeechProvider {
  HybridSpeechProvider({
    required SpeechProvider device,
    required Future<SynthesizedSpeech> Function(String text) synthesize,
    bool Function(String utteranceId) cloudFor = cloudSpeaks,
    bool Function(String utteranceId) sliceFor = slicedFromLonger,
    AudioPlayer? player,
    Future<Directory> Function()? directory,
    this.fetchTimeout = const Duration(seconds: 20),
    this.shortTextTimeout = const Duration(seconds: 6),
    this.shortText = 40,
    this.slowSpeed = 0.65,
    this.pauseAfterFailures = 3,
    this.pauseFor = const Duration(minutes: 3),
    this.maxClips = 80,
    this.maxConcurrentFetches = 2,
    this.singleRequestChars = 1400,
    this.splitAbove = 260,
    DateTime Function()? now,
    // The lint would have these be `this._device` and so on; a named
    // parameter may not start with an underscore (as in `ClipPlayback`).
    // ignore: prefer_initializing_formals
  })  : _device = device, _synthesize = synthesize, _cloudFor = cloudFor,
        // ignore: prefer_initializing_formals
        _sliceFor = sliceFor,
        _playerOverride = player,
        _directory = directory ?? getTemporaryDirectory,
        _now = now ?? DateTime.now {
    _device.onComplete = _deviceComplete;
    _device.onWordBoundary = _deviceWord;
  }

  final SpeechProvider _device;
  final Future<SynthesizedSpeech> Function(String text) _synthesize;
  final bool Function(String utteranceId) _cloudFor;
  final bool Function(String utteranceId) _sliceFor;
  final AudioPlayer? _playerOverride;
  final Future<Directory> Function() _directory;
  final DateTime Function() _now;

  /// How long a line waits for its audio before the phone says it instead.
  final Duration fetchTimeout;

  /// The same, for a word or a short phrase: a learner who tapped a word is
  /// not kept waiting as long as for a passage.
  final Duration shortTextTimeout;

  /// At most this many characters counts as short.
  final int shortText;

  /// The slow voice: the same audio played at this speed, pitch kept.
  ///
  /// 0.65 — the ratio of the phone's slow rate to its normal one (0.30 to
  /// 0.46), which is what `ClipPlayback` assumes when it draws the clock.
  final double slowSpeed;

  final int pauseAfterFailures;
  final Duration pauseFor;
  final int maxClips;
  final int maxConcurrentFetches;

  /// The longest text sent in one request; the server refuses past 1500.
  final int singleRequestChars;

  /// Longer than this, a text not already fetched is spoken in pieces.
  final int splitAbove;

  AudioPlayer? _playerInstance;
  AudioPlayer get _player => _playerInstance ??= _playerOverride ?? AudioPlayer();

  VoidCallback? _onComplete;
  void Function(int start)? _onWordBoundary;

  /// Which voice owns the current utterance. Events from the other are stale.
  _Voice _voice = _Voice.none;

  /// Bumped by every utterance and every stop. A fetch that finishes after the
  /// learner moved on sees a different number and does not start talking.
  int _generation = 0;

  final List<StreamSubscription<Object?>> _watching = [];

  /// Fetched and in-flight audio, oldest first, keyed by the exact text sent.
  final LinkedHashMap<String, Future<_Clip>> _clips = LinkedHashMap();

  /// Where the last slice of a piece ended, so a phrase that occurs twice in
  /// one piece resolves to the occurrence after the one just played.
  String? _lastPiece;
  int _lastEnd = 0;

  int _failures = 0;
  DateTime? _pausedUntil;

  int _activeFetches = 0;

  /// Fetches waiting for a slot, in the order they will get one.
  final List<({String text, Completer<void> turn})> _waiting = [];

  bool _disposed = false;

  @override
  Duration get startupAllowance => fetchTimeout + const Duration(seconds: 5);

  @override
  bool get isAvailable => _device.isAvailable || !_cloudPaused;

  @override
  String? get voiceDescription =>
      'Server voice · ${_device.voiceDescription}';

  @override
  set onComplete(VoidCallback? callback) => _onComplete = callback;

  @override
  set onWordBoundary(void Function(int start)? callback) =>
      _onWordBoundary = callback;

  @override
  Future<void> initialise() => _device.initialise();

  bool get _cloudPaused {
    final until = _pausedUntil;
    return until != null && _now().isBefore(until);
  }

  // ── Speaking ──────────────────────────────────────────────────────────────

  /// Without an id there is no telling who is speaking, so the phone does.
  @override
  Future<bool> speak(String text, {SpeechRate rate = SpeechRate.normal}) =>
      speakFor('', text, rate: rate);

  @override
  Future<bool> speakFor(
    String id,
    String text, {
    SpeechRate rate = SpeechRate.normal,
  }) async {
    if (_disposed || text.trim().isEmpty) return false;
    final generation = ++_generation;
    await _silence();
    if (generation != _generation) return true;

    if (!_cloudFor(id) || _cloudPaused) {
      return _speakOnDevice(generation, text, rate);
    }

    final found = _find(text, inside: _sliceFor(id));
    // Long and not already fetched as part of something — a tutor reply of
    // several sentences, or the whole passage replayed after the result.
    // Spoken piece by piece as one utterance: the first piece is short, so
    // the voice starts in about half the time, and the rest is generated
    // while it plays. Measured: a 284-character reply waited 8.5 s whole.
    if (found == null && text.length > splitAbove) {
      return _speakPieces(generation, text, rate);
    }
    final pending = found?.clip ?? _fetch(text);
    _promote(found?.piece ?? text);

    _Clip clip;
    try {
      clip = await pending.timeout(
          text.length <= shortText ? shortTextTimeout : fetchTimeout);
      _failures = 0;
    } catch (e) {
      debugPrint('WordOS voice: the server did not answer in time ($e)');
      _noteFailure(text);
      if (generation != _generation) return true;
      return _speakOnDevice(generation, text, rate);
    }
    // The learner stopped it, or something else took the voice, while the
    // audio was on its way. Not a failure — just no longer wanted.
    if (generation != _generation || _disposed) return true;

    final slice = _slice(clip, text, whole: found == null);
    if (slice == null) return _speakOnDevice(generation, text, rate);

    try {
      _voice = _Voice.cloud;
      await _player.setAudioSource(ClippingAudioSource(
        child: AudioSource.file(clip.path),
        start: Duration(milliseconds: slice.startMs),
        end: Duration(milliseconds: slice.endMs),
      ));
      if (generation != _generation) return true;
      await _player.setSpeed(rate == SpeechRate.slow ? slowSpeed : 1.0);
      _follow(generation, slice, 0, () => _onComplete?.call());
      unawaited(_player.play().catchError((Object e) {
        debugPrint('WordOS voice: playback failed ($e)');
      }));
      return true;
    } catch (e) {
      debugPrint('WordOS voice: could not play the server\'s audio ($e)');
      if (generation != _generation) return true;
      return _speakOnDevice(generation, text, rate);
    }
  }

  /// Plays a long text as its pieces, one after another, reporting word
  /// positions against the whole. A piece that cannot be fetched hands the
  /// rest of the text to the phone.
  Future<bool> _speakPieces(
      int generation, String text, SpeechRate rate) async {
    final pieces = <({String text, int base})>[];
    var cursor = 0;
    for (final piece in speechPieces(text, maximum: singleRequestChars)) {
      final base = text.indexOf(piece, cursor);
      if (base < 0) return _speakOnDevice(generation, text, rate);
      pieces.add((text: piece, base: base));
      cursor = base + piece.length;
    }
    for (final piece in pieces) {
      _fetch(piece.text);
    }
    // Wanted now: ahead of anything fetched in the background, first piece
    // first.
    for (final piece in pieces.reversed) {
      _promote(piece.text);
    }

    Future<bool> playFrom(int i) async {
      if (generation != _generation || _disposed) return true;
      if (i >= pieces.length) {
        _voice = _Voice.none;
        _onComplete?.call();
        return true;
      }
      final piece = pieces[i];
      _Clip clip;
      try {
        clip = await _fetch(piece.text).timeout(fetchTimeout);
        _failures = 0;
      } catch (e) {
        _noteFailure(piece.text);
        if (generation != _generation) return true;
        return _speakOnDevice(generation, text.substring(piece.base), rate);
      }
      if (generation != _generation || _disposed) return true;
      final slice = _slice(clip, piece.text, whole: true);
      if (slice == null) {
        return _speakOnDevice(generation, text.substring(piece.base), rate);
      }
      try {
        _voice = _Voice.cloud;
        await _player.setAudioSource(AudioSource.file(clip.path));
        if (generation != _generation) return true;
        await _player.setSpeed(rate == SpeechRate.slow ? slowSpeed : 1.0);
        _follow(generation, slice, piece.base, () => unawaited(playFrom(i + 1)));
        unawaited(_player.play().catchError((Object e) {
          debugPrint('WordOS voice: playback failed ($e)');
        }));
        return true;
      } catch (e) {
        if (generation != _generation) return true;
        return _speakOnDevice(generation, text.substring(piece.base), rate);
      }
    }

    return playFrom(0);
  }

  Future<bool> _speakOnDevice(
      int generation, String text, SpeechRate rate) async {
    if (generation != _generation) return true;
    _voice = _Voice.device;
    final ok = await _device.speak(text, rate: rate);
    if (!ok && generation == _generation) _voice = _Voice.none;
    return ok;
  }

  /// Turns playback position into "this word is starting" and the end of the
  /// slice into "finished" — the two things the phone's voice reports.
  void _follow(
    int generation,
    _Slice slice,
    int offset,
    VoidCallback done,
  ) {
    _cancelWatching();
    var reported = -1;

    _watching.add(_player
        .createPositionStream(
          minPeriod: const Duration(milliseconds: 40),
          maxPeriod: const Duration(milliseconds: 120),
        )
        .listen((position) {
      if (generation != _generation) return;
      final at = slice.startMs + position.inMilliseconds + 30;
      var index = reported;
      while (index + 1 < slice.words.length &&
          slice.words[index + 1].startMs <= at) {
        index++;
      }
      if (index != reported && index >= 0) {
        reported = index;
        _onWordBoundary
            ?.call(slice.words[index].charStart - slice.base + offset);
      }
    }));

    _watching.add(_player.playerStateStream.listen((state) {
      if (generation != _generation) return;
      if (state.processingState == ProcessingState.completed) {
        _voice = _Voice.none;
        _cancelWatching();
        done();
      }
    }, onError: (Object e) {
      debugPrint('WordOS voice: playback error ($e)');
      if (generation != _generation) return;
      _voice = _Voice.none;
      _cancelWatching();
      done();
    }));
  }

  void _deviceComplete() {
    if (_voice != _Voice.device) return;
    _voice = _Voice.none;
    _onComplete?.call();
  }

  void _deviceWord(int start) {
    if (_voice == _Voice.device) _onWordBoundary?.call(start);
  }

  @override
  Future<void> stop() async {
    _generation++;
    await _silence();
  }

  Future<void> _silence() async {
    final was = _voice;
    _voice = _Voice.none;
    _cancelWatching();
    if (was == _Voice.cloud || _playerInstance != null) {
      try {
        await _player.stop();
      } catch (_) {}
    }
    if (was == _Voice.device) await _device.stop();
  }

  void _cancelWatching() {
    for (final s in _watching) {
      unawaited(s.cancel());
    }
    _watching.clear();
  }

  // ── Fetching ahead ────────────────────────────────────────────────────────

  /// Fetches [text] in the background, behind anything already waiting —
  /// so a session that prepares its questions in order gets them in order.
  @override
  void prepare(String id, String text) {
    if (_disposed || !_cloudFor(id) || _cloudPaused) return;
    if (text.trim().isEmpty) return;
    for (final piece in speechPieces(text, maximum: singleRequestChars)) {
      if (_find(piece, inside: _sliceFor(id)) != null) continue;
      _fetch(piece);
    }
  }

  @override
  Future<void> ready(String id, String text) async {
    if (_disposed || !_cloudFor(id) || _cloudPaused) return;
    if (text.trim().isEmpty) return;
    prepare(id, text);
    // What speakFor will wait on first: the whole line, or — for a line it
    // speaks in pieces — the first piece.
    final found = _find(text, inside: _sliceFor(id));
    final first = found?.piece ??
        (text.length > splitAbove
            ? speechPieces(text, maximum: singleRequestChars).first
            : text);
    _promote(first);
    try {
      await (found?.clip ?? _fetch(first)).timeout(
          first.length <= shortText ? shortTextTimeout : fetchTimeout);
    } catch (_) {
      // Not this method's to report: speakFor meets the same failure and
      // hands the line to the phone.
    }
  }

  Future<_Clip> _fetch(String text) {
    final existing = _clips.remove(text);
    if (existing != null) {
      _clips[text] = existing; // most recently used
      return existing;
    }
    final future = _download(text);
    _clips[text] = future;
    // A failed fetch is forgotten, so the next attempt asks again.
    future.catchError((Object _) {
      if (identical(_clips[text], future)) _clips.remove(text);
      return _Clip.empty;
    });
    _evict();
    return future;
  }

  Future<_Clip> _download(String text) async {
    await _takeSlot(text);
    try {
      final speech = await _synthesize(text);
      if (speech.audio.isEmpty) throw StateError('no audio');
      final folder = Directory('${(await _directory()).path}/voice');
      await folder.create(recursive: true);
      final extension = speech.mimeType.contains('wav') ? 'wav' : 'mp3';
      final file = File(
          '${folder.path}/${DateTime.now().microsecondsSinceEpoch}.$extension');
      await file.writeAsBytes(speech.audio, flush: true);
      return _Clip(text, file.path, speech.durationMs, speech.words);
    } finally {
      _releaseSlot();
    }
  }

  Future<void> _takeSlot(String text) async {
    if (_activeFetches < maxConcurrentFetches) {
      _activeFetches++;
      return;
    }
    final turn = Completer<void>();
    _waiting.add((text: text, turn: turn));
    await turn.future;
  }

  void _releaseSlot() {
    if (_waiting.isNotEmpty) {
      _waiting.removeAt(0).turn.complete();
    } else {
      _activeFetches--;
    }
  }

  /// Moves [text]'s fetch, if it is still waiting, to the front of the queue.
  void _promote(String text) {
    final at = _waiting.indexWhere((w) => w.text == text);
    if (at > 0) _waiting.insert(0, _waiting.removeAt(at));
  }

  void _evict() {
    while (_clips.length > maxClips) {
      final oldest = _clips.keys.first;
      final future = _clips.remove(oldest)!;
      unawaited(future.then((clip) => clip.delete(), onError: (_) {}));
    }
  }

  void _noteFailure(String text) {
    _failures++;
    if (_failures >= pauseAfterFailures) {
      _pausedUntil = _now().add(pauseFor);
      _failures = 0;
      debugPrint(
          'WordOS voice: server voice paused for ${pauseFor.inMinutes} min');
    }
  }

  // ── Finding a sentence in a piece ─────────────────────────────────────────

  ({Future<_Clip> clip, String piece})? _find(String text,
      {required bool inside}) {
    final exact = _clips[text];
    if (exact != null) return (clip: exact, piece: text);
    if (!inside) return null;
    // Newest first: the passage on screen is the one most recently prepared.
    for (final entry in _clips.entries.toList().reversed) {
      if (entry.key.contains(text)) {
        return (clip: entry.value, piece: entry.key);
      }
    }
    return null;
  }

  /// The part of [clip] that says [text], or null when it cannot be found.
  _Slice? _slice(_Clip clip, String text, {required bool whole}) {
    final base = whole ? 0 : _indexIn(clip.text, text);
    if (base < 0) return null;
    final end = base + text.length;

    final words = clip.words
        .where((w) => w.charStart >= base && w.charEnd <= end)
        .toList();

    _lastPiece = clip.text;
    _lastEnd = end;

    // Nothing to time — punctuation alone, or text the timings do not cover.
    // The phone says it rather than the whole piece playing in its place.
    if (words.isEmpty) return null;
    if (whole) return _Slice(base, 0, clip.durationMs, words);
    // A little air either side, so the first consonant and the last breath
    // are not clipped.
    final start = math.max(0, words.first.startMs - 60);
    final stop = math.min(clip.durationMs, words.last.endMs + 250);
    return _Slice(base, start, math.max(stop, start + 1), words);
  }

  int _indexIn(String piece, String text) {
    if (piece == text) return 0;
    if (piece == _lastPiece) {
      final after = piece.indexOf(text, math.max(0, _lastEnd - text.length));
      if (after >= 0) return after;
    }
    return piece.indexOf(text);
  }

  @override
  Future<void> dispose() async {
    _disposed = true;
    _generation++;
    _cancelWatching();
    _onComplete = null;
    _onWordBoundary = null;
    try {
      await _playerInstance?.dispose();
    } catch (_) {}
    for (final future in _clips.values) {
      unawaited(future.then((clip) => clip.delete(), onError: (_) {}));
    }
    _clips.clear();
    await _device.dispose();
  }
}

enum _Voice { none, device, cloud }

class _Clip {
  const _Clip(this.text, this.path, this.durationMs, this.words);

  static const empty = _Clip('', '', 0, []);

  final String text;
  final String path;
  final int durationMs;
  final List<SpokenWord> words;

  Future<void> delete() async {
    if (path.isEmpty) return;
    try {
      final file = File(path);
      if (await file.exists()) await file.delete();
    } catch (_) {}
  }
}

class _Slice {
  const _Slice(this.base, this.startMs, this.endMs, this.words);

  /// Where the spoken text starts inside its piece, so word positions can be
  /// reported relative to what the caller handed over.
  final int base;
  final int startMs;
  final int endMs;
  final List<SpokenWord> words;
}

/// A passage cut into pieces for the voice, each an exact substring of it.
///
/// Cut only where a sentence ends and white space follows — a subset of the
/// places Listening splits its sentences (`splitSentences`), so a sentence
/// never straddles two pieces. The first piece is short so the first word is
/// heard quickly; the rest are longer, so a passage takes a handful of
/// requests rather than one per sentence.
List<String> speechPieces(
  String text, {
  int firstTarget = 180,
  int target = 450,
  int maximum = 1400,
}) {
  final ends = RegExp(r'[.!?]+[")’”]*(?=\s)|\n\s*\n').allMatches(text);
  final pieces = <String>[];
  var from = 0;

  void cut(int to) {
    final piece = text.substring(from, to).trim();
    if (piece.isNotEmpty) pieces.add(piece);
    from = to;
  }

  // Cut at whichever sentence end lands nearest the goal — the one just past
  // it, or the one before when that is closer. Always the one past it made a
  // reply of short sentences and one long one start on almost all of it.
  int? previous;
  for (final end in ends) {
    final length = end.end - from;
    final goal = pieces.isEmpty ? firstTarget : target;
    if (length < goal) {
      previous = end.end;
      continue;
    }
    final before = previous;
    if (before != null && before > from && goal - (before - from) < length - goal) {
      cut(before);
      // This end may itself close the next piece.
      final next = end.end - from;
      previous = next >= target ? null : end.end;
      if (next >= target) cut(end.end);
    } else {
      cut(end.end);
      previous = null;
    }
  }
  if (from < text.length) cut(text.length);

  // A single run-on "sentence" longer than the server accepts is split at a
  // space; rare, and better than a line the voice refuses outright.
  final bounded = <String>[];
  for (final piece in pieces) {
    var rest = piece;
    while (rest.length > maximum) {
      var at = rest.lastIndexOf(' ', maximum);
      if (at <= 0) at = maximum;
      bounded.add(rest.substring(0, at).trim());
      rest = rest.substring(at).trim();
    }
    if (rest.isNotEmpty) bounded.add(rest);
  }
  return bounded;
}
