namespace WordOs.Application.Abstractions;

/// <summary>
/// Turns a learner's recorded turn into text (ADR-107).
/// </summary>
/// <remarks>
/// Replaces the phone's own recogniser, which misheard learners — Android's
/// most of all — badly enough that the transcript Speaking is judged on was
/// often not what they said.
///
/// The transcript is not an answer. It goes back to the learner, who reads it
/// and corrects it before anything is sent (ADR-069); only then does it reach
/// the tutor through the existing Speaking turn. So nothing here can move a
/// word, and no rule lives behind this seam (rule R2).
/// </remarks>
public interface ISpeechTranscriber
{
    /// <summary>
    /// The words heard in <paramref name="audio"/>; empty when nobody spoke.
    /// </summary>
    /// <exception cref="SpeechUnavailableException">
    /// No engine could answer. Deliberately not an empty string: "you said
    /// nothing" and "we could not listen" need different answers.
    /// </exception>
    Task<string> TranscribeAsync(
        byte[] audio, string mimeType, CancellationToken ct = default);
}

/// <summary>Every speech engine failed, or none is configured.</summary>
public sealed class SpeechUnavailableException(string message) : Exception(message);

/// <summary>
/// Gemini's voice for the tutor and for Listening (ADR-108).
/// </summary>
/// <remarks>
/// Presentation, not state: the audio is rendered on request, cached for
/// minutes by the AI service and nowhere else, and a failure here costs the
/// learner nothing — the app speaks with the phone's voice instead.
/// </remarks>
public interface ISpeechSynthesizer
{
    /// <exception cref="SpeechUnavailableException">No voice could speak it.</exception>
    Task<SynthesizedSpeech> SynthesizeAsync(string text, CancellationToken ct = default);
}

/// <param name="Audio">Base64, passed through untouched.</param>
/// <param name="Timing"><c>aligned</c> when measured from the audio,
/// <c>estimated</c> when spread by word length.</param>
public sealed record SynthesizedSpeech(
    string Audio,
    string MimeType,
    int DurationMs,
    IReadOnlyList<SpokenWord> Words,
    string Timing);

/// <summary>When a word of the text is heard, and where it is in the text.</summary>
public sealed record SpokenWord(int StartMs, int EndMs, int CharStart, int CharEnd);
