import 'dart:convert';
import 'dart:typed_data';

/// A piece of English spoken in Gemini's voice, with a time on every word
/// (ADR-108).
///
/// The times are what the phone's voice used to report as it went — "this
/// word is starting now" — so Listening can still highlight the word being
/// said and draw a playhead through the passage (ADR-082). They were measured
/// from the audio on the server, not guessed here.
class SynthesizedSpeech {
  const SynthesizedSpeech({
    required this.audio,
    required this.mimeType,
    required this.durationMs,
    required this.words,
    required this.timing,
  });

  factory SynthesizedSpeech.fromJson(Map<String, dynamic> json) =>
      SynthesizedSpeech(
        audio: base64Decode(json['audio'] as String? ?? ''),
        mimeType: json['mimeType'] as String? ?? 'audio/mpeg',
        durationMs: (json['durationMs'] as num?)?.toInt() ?? 0,
        words: ((json['words'] as List?) ?? const [])
            .map((w) => SpokenWord.fromJson(w as Map<String, dynamic>))
            .toList(),
        timing: json['timing'] as String? ?? 'estimated',
      );

  final Uint8List audio;
  final String mimeType;
  final int durationMs;

  /// Every word of the text that was sent, in order.
  final List<SpokenWord> words;

  /// `aligned` when measured from the audio; `estimated` when the server could
  /// only spread the words by their length.
  final String timing;
}

/// When one word is heard, and where it sits in the text that was sent.
class SpokenWord {
  const SpokenWord({
    required this.startMs,
    required this.endMs,
    required this.charStart,
    required this.charEnd,
  });

  factory SpokenWord.fromJson(Map<String, dynamic> json) => SpokenWord(
        startMs: (json['start'] as num).toInt(),
        endMs: (json['end'] as num).toInt(),
        charStart: (json['charStart'] as num).toInt(),
        charEnd: (json['charEnd'] as num).toInt(),
      );

  final int startMs;
  final int endMs;
  final int charStart;
  final int charEnd;
}
