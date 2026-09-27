import 'dart:async';

import 'package:flutter/foundation.dart';

import '../../core/audio/speech_provider.dart';
import '../../core/audio/speech_service.dart';

/// Playing a piece of English aloud, with a place in it.
///
/// Text-to-speech has no notion of a playhead: it is handed a string and it
/// talks until it runs out. So a clip is spoken **one sentence at a time** and
/// the sentence boundaries *are* the positions — pausing, continuing, seeking
/// and switching to the slow voice all land on one (ADR-068).
///
/// This is the engine, with no opinion about how it is drawn. It exists
/// because there were three of these — the Listening clip, the sentence beside
/// a question, the recording handed back with the result — each with its own
/// copy of the flags, and they disagreed with each other about what a stop
/// meant and what the button should say afterwards. One of those copies also
/// carried a bug the others did not (ADR-080). Now there is one place to fix.
///
/// Deliberately **no auto-play**. Every clip in this app waits to be asked; the
/// reasoning is in ADR-080, and a controller that could start on its own would
/// re-open that decision one call site at a time.
class ClipPlayback extends ChangeNotifier {
  ClipPlayback({
    required SpeechService speech,
    required this.text,
    required String idPrefix,
    // The lint would have this be `this._speech`; a named parameter may not
    // start with an underscore, so it is assigned here instead.
    // ignore: prefer_initializing_formals
  })  : _speech = speech,
        _id = '$idPrefix:${text.hashCode}',
        sentences = splitSentences(text) {
    starts = cumulativeStarts(sentences);
    totalChars =
        sentences.isEmpty ? 1 : starts.last + sentences.last.length;
    // The service is the single source of truth for what is audible, so a
    // clip that ends by itself has to repaint the control without being told.
    _speech.addListener(_onVoiceChanged);
  }

  final SpeechService _speech;

  /// Identifies this clip's utterances. Two players speaking different things
  /// must differ here, or both would light up together.
  final String _id;

  /// The whole clip, kept for the transcript shown when audio fails.
  final String text;

  /// The clip, cut where a listener would hear a break.
  final List<String> sentences;

  /// Where each sentence starts, in characters, and the total. A scrubber is
  /// drawn against these so a long sentence takes proportionally longer to
  /// cross than a short one.
  late final List<int> starts;
  late final int totalChars;

  bool _slow = false;
  bool _started = false;
  bool _finished = false;
  bool _audioFailed = false;
  int _index = 0;
  bool _disposed = false;

  /// Where inside the current sentence the voice has reached, in characters.
  ///
  /// Reported by the engine as each word begins (ADR-082). It is what makes
  /// "continue where I stopped" mean the word rather than the sentence — and
  /// it is an *at least*: an engine that reports nothing leaves it at the
  /// offset playback started from, which is still true, just coarser.
  int _offset = 0;

  /// Where the current utterance began inside its sentence. A resumed or
  /// sought sentence is spoken from part-way in, so the engine's offsets are
  /// relative to that cut and have to be put back.
  int _utteranceStart = 0;

  /// Measured speaking speed, characters per second.
  ///
  /// Text-to-speech will not say how long a clip runs — there is no file and no
  /// duration to ask for. So the clock is a projection of the position, and
  /// the rate behind it is *measured while the clip plays* rather than assumed:
  /// the first estimate is a constant, and each finished sentence corrects it.
  /// That keeps the elapsed time and the bar in step with each other by
  /// construction, since both are the same number read two ways (ADR-082).
  double _charsPerSecond = _defaultCharsPerSecond;
  int _measuredChars = 0;
  final Stopwatch _measured = Stopwatch();

  /// The app's normal rate (0.46 on the device engine), **measured** rather
  /// than guessed: an iOS run of a B2 passage settled at just over 16.
  ///
  /// It is only the opening estimate — the first sentence corrects it — but the
  /// estimate is on screen before any sentence has finished, so being close
  /// matters. The first guess here was 13.5, and the total visibly fell from
  /// 1:59 to 1:40 in the first fifteen seconds of playback. A clock that
  /// rewrites itself that far is worse than a slightly wrong one.
  static const double _defaultCharsPerSecond = 16.0;

  /// The slow voice covers less ground per second, by about this much.
  static const double _slowFactor = 0.65;

  /// Invalidates the playback loop.
  ///
  /// The loop awaits one sentence at a time, so a seek or a pause cannot
  /// simply set a flag and expect the next iteration to notice — it has to be
  /// able to tell "I am the current loop" from "I was replaced while I was
  /// waiting".
  int _run = 0;

  /// The slow voice: an accessibility aid, never part of the score.
  bool get slow => _slow;

  /// Whether the clip has ever been started. Before that the control offers
  /// "play"; after it, "continue" — which is not the same promise.
  bool get started => _started;

  /// The clip reached its end on its own. The only state in which offering to
  /// **replay** is honest: there is nothing left to continue.
  bool get finished => _finished;

  /// The device proved it cannot speak. The caller shows the text instead
  /// rather than leaving the learner stuck on something they can never hear
  /// (demo review §51).
  bool get audioFailed => _audioFailed;

  /// Which sentence is being spoken, or would be if play were pressed.
  int get index => _index;

  bool get isPlaying => _speech.isSpeakingId(_id);

  /// Where the clip has reached, in characters — for a scrubber.
  ///
  /// Sentence start plus how far into it the voice has got, so the bar crosses
  /// the track a word at a time instead of jumping between sentences.
  double get position {
    if (starts.isEmpty) return 0;
    final base = starts[_index] + _offset;
    return base.clamp(0, totalChars).toDouble();
  }

  /// How long the clip has been running and how long it lasts, both estimated
  /// from [position] and the measured rate. They cannot disagree: the clock is
  /// the bar in another unit.
  Duration get elapsed => timeAt(position);
  Duration get total => timeAt(totalChars.toDouble());

  /// How far into the clip a point on the bar is, in time.
  ///
  /// Used for the playhead and for the finger while it drags, so a learner
  /// scrubbing sees where they are going rather than where the audio still is.
  Duration timeAt(double chars) {
    final rate = _charsPerSecond * (_slow ? _slowFactor : 1);
    if (rate <= 0) return Duration.zero;
    return Duration(milliseconds: (chars / rate * 1000).round());
  }

  /// The sentence that contains a point on the bar.
  int sentenceAt(double chars) {
    for (var i = sentences.length - 1; i >= 0; i--) {
      if (chars >= starts[i]) return i;
    }
    return 0;
  }

  void _onVoiceChanged() {
    // The engine's offsets are into the text it was handed, which for a
    // resumed sentence is only the tail of one — so the cut is added back.
    if (_speech.isSpeakingId(_id)) {
      _offset = _utteranceStart + _speech.spokenOffset;
    }
    _notify();
  }

  /// The start of the word containing [offset] in [sentence].
  ///
  /// Continuing from the middle of a word would have the voice say half of it,
  /// which is worse than the sentence-sized jump this replaced.
  static int wordStart(String sentence, int offset) {
    var i = offset.clamp(0, sentence.length);
    while (i > 0 && !_isBreak(sentence.codeUnitAt(i - 1))) {
      i--;
    }
    return i;
  }

  static bool _isBreak(int codeUnit) =>
      codeUnit == 0x20 || codeUnit == 0x0A || codeUnit == 0x09;

  void _notify() {
    if (_disposed) return;
    notifyListeners();
  }

  /// Play, or suspend where it stands.
  ///
  /// Pausing is not stopping: the next press continues from **the word** the
  /// voice had reached rather than starting the clip, or even the sentence,
  /// again. At the very end there is nothing to continue, so it plays from the
  /// top — which is what the replay face on the control promises (ADR-080).
  Future<void> toggle() async {
    if (isPlaying) return pause();
    return playAt(_finished ? 0 : position);
  }

  /// Suspends the clip where it stands.
  ///
  /// [position] is left exactly where the last word put it, and that is what
  /// the next press resumes from.
  Future<void> pause() async {
    _run++;
    _measured.stop();
    await _speech.stop();
    _notify();
  }

  /// Speaks from a point in the clip to its end.
  ///
  /// The first sentence is spoken from the word containing [chars]; every
  /// sentence after it whole. This is what keeps the audio natural while the
  /// position stays word-accurate: the alternative — one utterance per word —
  /// gives an exact playhead and turns the clip into dictation, with a gap
  /// after every word (ADR-082).
  Future<void> playAt(double chars) async {
    if (sentences.isEmpty || _disposed) return;

    final run = ++_run;
    final from = sentenceAt(chars);
    // Where inside that sentence, snapped back to the start of a word.
    var into = wordStart(
      sentences[from],
      (chars - starts[from]).round().clamp(0, sentences[from].length),
    );

    _started = true;
    _finished = false;
    _index = from;
    _offset = into;
    _notify();

    // Take the voice first, then note where the interruption count stands:
    // from here on any change to it means somebody else stopped us, and a
    // cancelled sentence is otherwise indistinguishable from a finished one
    // (ADR-080).
    await _speech.stop();
    if (_disposed || run != _run) return;
    final generation = _speech.interruptions;

    for (var i = from; i < sentences.length; i++) {
      if (_disposed || run != _run) return;

      final sentence = sentences[i];
      // Only the first sentence of a resumed run starts part-way in.
      final cut = i == from ? into.clamp(0, sentence.length) : 0;
      _index = i;
      _utteranceStart = cut;
      _offset = cut;
      _notify();

      final spoken = sentence.substring(cut);
      // A cut that lands on the last character leaves nothing to say; treat it
      // as already done rather than handing the engine an empty string, which
      // it reports as a failure.
      if (spoken.trim().isEmpty) {
        into = 0;
        continue;
      }

      _measured
        ..reset()
        ..start();

      final ok = await _speech.speakToCompletion(
        _id,
        spoken,
        rate: _slow ? SpeechRate.slow : SpeechRate.normal,
      );

      _measured.stop();

      // A device with no voice at all: say so once and stop, rather than
      // walking silently through every remaining sentence.
      if (!ok) {
        _audioFailed = true;
        _notify();
        return;
      }
      // Someone outside this player silenced the voice — the learner leaving
      // the passage, another player taking over. Without this the loop hears
      // "that sentence ended" and reads the next line out over whatever the
      // learner opened next (ADR-080).
      if (_disposed || run != _run || _speech.interruptions != generation) {
        return;
      }

      _learnRate(spoken.length, _measured.elapsed);
      // The sentence is behind the learner now, so the bar sits at its end
      // rather than at its last reported word.
      _offset = sentence.length;
      into = 0;
      _notify();
    }

    if (run == _run) {
      _finished = true;
      _index = sentences.length - 1;
      _offset = sentences.last.length;
      _notify();
    }
  }

  /// Corrects the speaking-rate estimate from a sentence that actually ran.
  ///
  /// Guarded rather than trusted: a sentence cut short, or one whose completion
  /// the platform reported late, would otherwise drag the clock somewhere
  /// absurd. Anything outside half to twice the current estimate is treated as
  /// evidence about the platform rather than about the voice, and ignored.
  void _learnRate(int chars, Duration took) {
    if (chars < 20 || took.inMilliseconds < 500) return;

    final rate = chars / (took.inMilliseconds / 1000) / (_slow ? _slowFactor : 1);
    if (rate < _charsPerSecond / 2 || rate > _charsPerSecond * 2) return;

    // Smoothed, and smoothed on the *first* measurement too. Taking one
    // sentence at full weight moves the total in a single visible step, which
    // is what the learner reads as the clock being wrong; easing into it
    // converges just as fast over a passage and never lurches.
    final weight = _measuredChars == 0 ? 0.5 : 0.3;
    _charsPerSecond = _charsPerSecond * (1 - weight) + rate * weight;
    _measuredChars += chars;
  }

  /// Moves the playhead to a point on the bar.
  ///
  /// Only follows the audio when the audio is running: seeking while paused
  /// moves the place without starting to talk, which is what a learner
  /// dragging a stopped player means.
  Future<void> seekTo(double chars) async {
    if (isPlaying) return playAt(chars);

    _run++;
    final target = sentenceAt(chars);
    _index = target;
    _offset = wordStart(
      sentences[target],
      (chars - starts[target]).round().clamp(0, sentences[target].length),
    );
    _utteranceStart = _offset;
    _finished = false;
    _notify();
  }

  /// Switches speed, carrying on from the sentence being spoken rather than
  /// starting the clip again: a learner switches to the slow voice *because*
  /// of the line they are on, and sending them back to the beginning answers
  /// the wrong request.
  void setSlow(bool value) {
    if (_slow == value) return;
    _slow = value;
    _notify();
    if (isPlaying) unawaited(playAt(position));
  }

  @override
  void dispose() {
    _disposed = true;
    // Nothing may keep talking after the player is gone. Bumping the run stops
    // the loop; the sentence already in the speaker's mouth needs telling.
    _run++;
    _speech.removeListener(_onVoiceChanged);
    unawaited(_speech.stop());
    super.dispose();
  }
}

/// Cuts a clip where a listener would hear a break.
///
/// Sentence-ended, and a paragraph break counts as one too: the generator
/// marks them, and a learner scrubbing through a structured text expects the
/// paragraph starts to be places they can land on.
List<String> splitSentences(String text) {
  final out = <String>[];
  for (final paragraph in text.split(RegExp(r'\n\s*\n'))) {
    for (final match
        in RegExp(r'[^.!?]+[.!?]+[")’”]*').allMatches(paragraph)) {
      final sentence = match.group(0)!.trim();
      if (sentence.isNotEmpty) out.add(sentence);
    }
    // Whatever was left after the last full stop — a clip that ends without
    // one must not lose its final words.
    final tail = paragraph
        .substring(
          RegExp(r'[^.!?]+[.!?]+[")’”]*')
              .allMatches(paragraph)
              .fold<int>(0, (end, m) => m.end),
        )
        .trim();
    if (tail.isNotEmpty) out.add(tail);
  }
  return out.isEmpty ? [text.trim()] : out;
}

List<int> cumulativeStarts(List<String> sentences) {
  final starts = <int>[];
  var running = 0;
  for (final sentence in sentences) {
    starts.add(running);
    running += sentence.length + 1;
  }
  return starts;
}

/// `m:ss`, or `h:mm:ss` for a clip long enough to need it.
///
/// A C2 passage runs to some 720 words, which is minutes rather than seconds —
/// and a clock that reads "0:412" would be worse than no clock.
String formatClipTime(Duration d) {
  final seconds = d.inSeconds.clamp(0, 86400);
  final h = seconds ~/ 3600;
  final m = (seconds % 3600) ~/ 60;
  final s = seconds % 60;
  final ss = s.toString().padLeft(2, '0');
  return h > 0 ? '$h:${m.toString().padLeft(2, '0')}:$ss' : '$m:$ss';
}
