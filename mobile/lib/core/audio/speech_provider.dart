import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_tts/flutter_tts.dart';

/// How fast an utterance is spoken.
///
/// Two named speeds rather than a raw number: every screen that slows speech
/// down means the same thing by it, and the actual rates are a property of the
/// provider, not of the caller.
enum SpeechRate { normal, slow }

/// Where spoken English comes from.
///
/// The seam exists so the voice can be replaced without touching a single
/// screen. Today it is the device's own engine; a cloud neural voice would be a
/// second implementation of this interface, proxied through the backend so no
/// API key ever reaches the app.
///
/// Implementations must never throw. A device with no engine, no English voice,
/// or audio focus lost to a phone call is an ordinary condition, and a learner
/// mid-session must not be dropped because of it — failure is reported as
/// `false` and the caller decides what to do.
abstract class SpeechProvider {
  /// Prepares the engine. Safe to call more than once.
  Future<void> initialise();

  /// Starts speaking. Returns false when no audio could be produced.
  Future<bool> speak(String text, {SpeechRate rate = SpeechRate.normal});

  Future<void> stop();

  /// Fires when an utterance ends — finished, cancelled or failed.
  ///
  /// This is what makes a hands-free conversation possible: the microphone
  /// opens on the callback rather than on a guessed delay.
  set onComplete(VoidCallback? callback);

  /// Fires as each word is about to be spoken, with where it starts in the
  /// text that was handed over.
  ///
  /// The real playhead, and the reason a clip does not have to be chopped into
  /// words to be positioned at one. Chopping it would give an exact position
  /// and ruin the audio — every word its own utterance, with a gap after it,
  /// which is not English being spoken but English being dictated. So the
  /// engine keeps whole sentences and reports where inside one it has reached
  /// (ADR-082).
  ///
  /// Platforms that do not report progress simply never call this; a caller
  /// must still work without it.
  set onWordBoundary(void Function(int start)? callback);

  /// False once the engine has proved unusable on this device.
  bool get isAvailable;

  /// A human-readable name for the active voice, for diagnostics.
  String? get voiceDescription;

  Future<void> dispose();
}

/// The device's own text-to-speech engine.
///
/// Free, offline, and available on every phone — but the stock configuration
/// sounds like a screen reader, so three things are tuned deliberately:
///
/// 1. **The best installed voice is chosen**, not the default. iOS ships a
///    basic voice and offers much better "Enhanced"/"Premium" downloads; the
///    default picks the basic one even when a better one is present.
/// 2. **The rate is slowed** from the platform default, which is tuned for
///    screen readers rather than for someone learning the language.
/// 3. **iOS plays through the playback category**, so speech is audible with
///    the ringer switch off — otherwise a learner hears silence and concludes
///    the app is broken.
/// The classic Apple novelty voices. They are speech synthesisers, so they
/// appear in `getVoices` beside the real ones — and they are unusable as a
/// tutor's voice.
const _noveltyVoices = <String>[
  'albert', 'bad news', 'bahh', 'bells', 'boing', 'bubbles', 'cellos',
  'deranged', 'fred', 'good news', 'jester', 'junior', 'kathy', 'organ',
  'ralph', 'superstar', 'trinoids', 'whisper', 'wobble', 'zarvox',
];

/// The voice to switch to, or null to keep whatever the platform chose.
///
/// Separated from the provider so the decision can be tested without a
/// platform channel — it is the part that went wrong, and the part that is
/// easy to get wrong again.
Map<String, String>? bestEnglishVoice(List<Map<String, String>> voices) {
  /// A quality marker, or null when the voice does not claim one.
  int? quality(Map<String, String> voice) {
    final descriptor =
        '${voice['name']} ${voice['identifier'] ?? ''}'.toLowerCase();

    // Novelty voices are excluded outright. Several carry no quality marker
    // at all, so without this they would qualify as "unmarked" under any
    // scheme that accepts unmarked voices — which is exactly how one of them
    // ended up as the tutor.
    for (final novelty in _noveltyVoices) {
      if (descriptor.contains(novelty)) return null;
    }

    if (descriptor.contains('premium')) return 0;
    if (descriptor.contains('enhanced')) return 1;
    if (descriptor.contains('neural')) return 1;
    return null;
  }

  final candidates = voices
      .where((v) => (v['locale'] ?? '').toLowerCase().startsWith('en'))
      .map((v) => (voice: v, rank: quality(v)))
      .where((c) => c.rank != null)
      .toList()
    ..sort((a, b) {
      final byRank = a.rank!.compareTo(b.rank!);
      if (byRank != 0) return byRank;
      // Prefer en-US, for consistency with the content the app generates.
      final aUs = (a.voice['locale'] ?? '').toLowerCase() == 'en-us' ? 0 : 1;
      final bUs = (b.voice['locale'] ?? '').toLowerCase() == 'en-us' ? 0 : 1;
      return aUs.compareTo(bUs);
    });

  return candidates.isEmpty ? null : candidates.first.voice;
}

class DeviceSpeechProvider implements SpeechProvider {
  DeviceSpeechProvider({FlutterTts? tts}) : _tts = tts ?? FlutterTts();

  /// Slower than the platform default: comprehensible for a learner rather
  /// than efficient for a sighted user skimming.
  static const double _normalRate = 0.46;
  static const double _slowRate = 0.30;

  final FlutterTts _tts;

  bool _initialised = false;
  bool _available = true;
  String? _voice;
  VoidCallback? _onComplete;
  void Function(int start)? _onWordBoundary;

  @override
  bool get isAvailable => _available;

  @override
  String? get voiceDescription => _voice;

  @override
  set onComplete(VoidCallback? callback) => _onComplete = callback;

  @override
  set onWordBoundary(void Function(int start)? callback) =>
      _onWordBoundary = callback;

  @override
  Future<void> initialise() async {
    if (_initialised) return;
    _initialised = true;

    await _guard(timeout: null, () async {
      // Speech is content, not a notification: it must be audible even when the
      // ringer switch is off, and it should duck rather than kill other audio.
      if (defaultTargetPlatform == TargetPlatform.iOS) {
        await _tts.setSharedInstance(true);
        await _tts.setIosAudioCategory(
          IosTextToSpeechAudioCategory.playback,
          [
            IosTextToSpeechAudioCategoryOptions.defaultToSpeaker,
            IosTextToSpeechAudioCategoryOptions.duckOthers,
          ],
          IosTextToSpeechAudioMode.spokenAudio,
        );
      }

      await _tts.setLanguage('en-US');
      await _tts.setSpeechRate(_normalRate);
      await _tts.setVolume(1.0);
      await _tts.setPitch(1.0);
      await _selectBestVoice();

      _tts.setCompletionHandler(_completed);
      _tts.setCancelHandler(_cancelled);
      // iOS reports this from `willSpeakRangeOfSpeechString`, Android from
      // `onRangeStart`. Neither is guaranteed, so nothing may depend on it
      // arriving — it refines a position that is already usable without it.
      _tts.setProgressHandler((text, start, end, word) {
        _onWordBoundary?.call(start);
      });
      _tts.setErrorHandler((dynamic _) {
        _available = false;
        _outstanding = 0;
        _stale = 0;
        _finished();
      });
    });
  }

  /// Upgrades to a higher-quality English voice **only if the device has one**.
  ///
  /// The rule is one-directional on purpose: this may replace the platform
  /// default with something better, and must never replace it with something
  /// else. An earlier version ranked every unrecognised voice above the
  /// built-in `compact` one, which on iOS handed the app novelty voices —
  /// Zarvox, Trinoids, Bad News — because those are unrecognised too. The
  /// result was worse than doing nothing at all.
  ///
  /// So: pick a voice that positively identifies itself as premium, enhanced
  /// or neural, and otherwise leave the system's own choice alone.
  Future<void> _selectBestVoice() async {
    try {
      final raw = await _tts.getVoices as List<dynamic>?;
      if (raw == null) return;

      final english = raw
          .whereType<Map<Object?, Object?>>()
          .map((v) => v.map((k, value) => MapEntry('$k', '$value')))
          .toList();

      final best = bestEnglishVoice(english);

      // Nothing demonstrably better than what the platform already picked.
      if (best == null) return;

      await _tts.setVoice({
        'name': best['name'] ?? '',
        'locale': best['locale'] ?? 'en-US',
      });
      _voice = '${best['name']} (${best['locale']})';
    } catch (_) {
      // The platform default remains in force.
    }
  }

  void _finished() => _onComplete?.call();

  /// Utterances handed to the engine that it has not yet reported the end of.
  int _outstanding = 0;

  /// Of those, the ones [stop] cut short: their end is still to be reported,
  /// and is not news when it is.
  int _stale = 0;
  DateTime? _stoppedAt;

  /// How long after a [stop] an ending is still taken to be the stopped
  /// utterance's. They arrive within a frame or two; this only bounds the
  /// damage on a platform that never sends one.
  static const _staleWindow = Duration(seconds: 1);

  /// The platform reports the end of an utterance we stopped — and reports it
  /// **late**, as a message of its own after `stop()` has returned. iOS sends
  /// it as an ordinary completion, Android as a cancellation.
  ///
  /// Every caller stops the old utterance and starts the new one in the same
  /// breath, so by the time the old one's ending arrived the new one was
  /// speaking, and "that utterance is over" was applied to it instead: the
  /// service forgot a voice that was still talking. Nothing could silence it
  /// after that, because every stop meant "stop what you know to be playing"
  /// — so a sentence started over a word's pronunciation followed the learner
  /// into the next question, which is how this was reported. Measured on the
  /// simulator: the second utterance was "finished" within 250 ms of starting
  /// and went on for seven seconds (ADR-135).
  ///
  /// So endings are counted. One that belongs to an utterance [stop] already
  /// accounted for is swallowed; whoever stopped it knows.
  bool _isStaleEnding() {
    final at = _stoppedAt;
    if (_stale > 0 &&
        at != null &&
        DateTime.now().difference(at) < _staleWindow) {
      _stale--;
      return true;
    }
    _stale = 0;
    return false;
  }

  void _completed() {
    if (_isStaleEnding()) return;
    if (_outstanding > 0) _outstanding--;
    _finished();
  }

  /// A cancellation is never the natural end of the utterance now speaking,
  /// so one that answers our own [stop] is dropped whatever the clock says.
  /// One that arrives unasked — a phone call, the system taking the audio —
  /// is still the end of what was playing.
  void _cancelled() {
    if (_stale > 0) {
      _stale--;
      return;
    }
    if (_outstanding > 0) _outstanding--;
    _finished();
  }

  /// Whether the engine has been handed anything since it was last stopped.
  ///
  /// A fact about what this class asked for, not a belief about what the
  /// engine is doing: sound can only exist if a `speak` went out after the
  /// last `stop`. It is what lets [stop] be called unconditionally (ADR-135)
  /// without a platform call — and, under test, a five-second timer — every
  /// time a screen that never spoke goes away.
  bool _handedOver = false;

  /// Bumped by every [speak] and [stop], so a `speak` that was overtaken while
  /// it was still setting the rate does not start talking after the stop that
  /// was meant to prevent it.
  int _ticket = 0;

  @override
  Future<bool> speak(String text, {SpeechRate rate = SpeechRate.normal}) async {
    if (text.trim().isEmpty) return false;
    await initialise();

    final ticket = ++_ticket;
    final ok = await _guard(() async {
      await _tts.setSpeechRate(
          rate == SpeechRate.slow ? _slowRate : _normalRate);
      if (ticket != _ticket) return;
      _handedOver = true;
      await _tts.speak(text);
      _outstanding++;
    });

    if (!ok) _available = false;
    return ok;
  }

  @override
  Future<void> stop() async {
    _ticket++;
    if (!_handedOver) return;
    _handedOver = false;
    // Whatever the engine still owes an ending for is being ended here.
    _stale += _outstanding;
    _outstanding = 0;
    _stoppedAt = DateTime.now();
    await _guard(() => _tts.stop());
  }

  @override
  Future<void> dispose() async {
    _onComplete = null;
    _onWordBoundary = null;
    // No timeout on teardown: a pending timer would outlive the widget tree,
    // and nobody is left to care whether the stop succeeded.
    unawaited(_guard(() => _tts.stop(), timeout: null));
  }

  /// Runs a platform call, reporting failure rather than throwing.
  ///
  /// Deliberately catches everything: `flutter_tts` surfaces
  /// `MissingPluginException`, `PlatformException` or a plain error depending
  /// on the platform, and an unhandled one would take down a session.
  Future<bool> _guard(
    Future<void> Function() call, {
    Duration? timeout = const Duration(seconds: 5),
  }) async {
    try {
      final future = call();
      await (timeout == null ? future : future.timeout(timeout));
      return true;
    } catch (_) {
      return false;
    }
  }
}
