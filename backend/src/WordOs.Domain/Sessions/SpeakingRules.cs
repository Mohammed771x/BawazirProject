namespace WordOs.Domain.Sessions;

/// <summary>
/// What the AI observed about one spoken word. Deliberately a domain type, not
/// the AI service's own record: the rule below must not shift because a
/// provider changed the shape of its response.
/// </summary>
public sealed record SpokenWordObservation(
    bool Used,
    bool MeaningCorrect,
    bool Understandable,
    bool GrammarAcceptable,
    bool MajorGrammarProblem);

/// <summary>
/// Whether a word passed Speaking, decided here and nowhere else.
/// </summary>
/// <remarks>
/// The model reports what it saw; this is the only place that turns those
/// observations into a verdict (rule R2, ADR-015). Keeping it in C# — and out
/// of the prompt — means a wording tweak can never quietly redefine what
/// passing means, and the rule can be tested without invoking a model at all.
///
/// Two things are deliberately <b>not</b> considered:
///
/// <list type="bullet">
/// <item><b>Pronunciation.</b> The transcript comes from speech recognition, so
/// a "mispronunciation" cannot be told apart from a recogniser error. Scoring
/// it would punish the learner for their microphone.</item>
/// <item><b>Minor grammar.</b> <c>MVP Core.txt</c> §32 is explicit that a small
/// slip must not fail correct usage — "I research about AI yesterday" has the
/// wrong tense and the right meaning, and passes.</item>
/// </list>
/// </remarks>
public static class SpeakingRules
{
    /// <summary>
    /// A word passes when the learner actually said it, meant the right thing
    /// by it, and could be understood.
    /// </summary>
    /// <remarks>
    /// <see cref="SpokenWordObservation.MajorGrammarProblem"/> fails it because
    /// "major" is defined as grammar broken enough to obscure the meaning — at
    /// which point nobody can tell whether the word was used correctly. Ordinary
    /// grammar mistakes are recorded and ignored.
    /// </remarks>
    public static bool Passed(SpokenWordObservation observation) =>
        observation.Used
        && observation.MeaningCorrect
        && observation.Understandable
        && !observation.MajorGrammarProblem;
}
