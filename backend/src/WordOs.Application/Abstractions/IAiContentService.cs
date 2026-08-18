using WordOs.Domain.Common;

namespace WordOs.Application.Abstractions;

/// <summary>
/// The seam between WordOS and the AI service.
/// </summary>
/// <remarks>
/// Everything crossing this boundary is an <b>observation</b>, never a verdict.
/// The AI reports what it saw — whether a word was used, whether a sentence is
/// understandable — and the domain applies the rule (rule R2). No method here
/// returns "passed", because that is not the AI's decision to make.
///
/// An interface, so an outage degrades a session instead of breaking it, and so
/// the provider can be replaced without touching a single rule.
/// </remarks>
public interface IAiContentService
{
    /// <summary>Generates a Reading or Listening passage with its questions.</summary>
    Task<GeneratedContent> GenerateContentAsync(
        ContentRequest request,
        CancellationToken ct = default);

    /// <summary>Reports observations about one learner sentence.</summary>
    Task<WritingObservation> EvaluateWritingAsync(
        WritingEvaluationRequest request,
        CancellationToken ct = default);

    /// <summary>Produces one conversational turn.</summary>
    Task<SpeakingObservation> SpeakingTurnAsync(
        SpeakingTurnRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Reports observations about a finished conversation, one row per target
    /// word.
    /// </summary>
    /// <remarks>
    /// Called once, at the end. A learner who fumbles a word early and uses it
    /// well later is judged on the whole exchange, and evaluating per turn would
    /// multiply a session's cost by however much the learner says.
    /// </remarks>
    Task<SpeakingEvaluation> EvaluateSpeakingAsync(
        SpeakingEvaluationRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Rates the productive half of the placement test — Speaking and Writing.
    /// </summary>
    /// <remarks>
    /// Reading and Listening are deliberately absent: their answers are matched
    /// against a known key, so a model would add cost, latency and disagreement
    /// to a question that already has a right answer.
    ///
    /// Speaking and Writing have no key. Scoring them on length and lexical
    /// variety cannot tell a short fluent answer from a padded weak one, which
    /// is the gap this fills. The model estimates; the band is still computed
    /// here from the scores it returns (rule R2).
    /// </remarks>
    Task<PlacementEvaluation> EvaluatePlacementAsync(
        PlacementEvaluationRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Re-tells an existing passage at a different CEFR level.
    /// </summary>
    /// <remarks>
    /// The same story, in different language — not a new passage on a similar
    /// topic. A learner who asks for something easier has already invested in
    /// this text; replacing the story reads as though the app ignored them.
    /// </remarks>
    Task<GeneratedContent> RelevelContentAsync(
        RelevelRequest request,
        CancellationToken ct = default);
}

/// <param name="Skill">SPEAKING or WRITING — never a receptive skill.</param>
public sealed record PlacementEvaluationRequest(
    SkillType Skill,
    IReadOnlyList<PlacementAnswerToRate> Answers);

public sealed record PlacementAnswerToRate(
    string ItemId,
    CefrLevel Level,
    string Prompt,
    string Answer);

/// <param name="Score">Partial credit in <c>[0, 1]</c>, which is what the
/// ability estimator consumes — the level label is for the evidence view.</param>
public sealed record PlacementAnswerRating(
    string ItemId,
    CefrLevel? EstimatedLevel,
    double Score,
    string Evidence);

public sealed record PlacementEvaluation(
    IReadOnlyList<PlacementAnswerRating> Answers,
    CefrLevel? OverallLevel,
    string Summary,
    bool FromFallback,
    string PromptVersion = "",
    string Model = "",
    int Tokens = 0);

public sealed record AiTargetWord(
    string Text,
    string Meaning,
    string Definition,
    string PartOfSpeech);

/// <param name="ReuseWords">
/// Active vocabulary the generator should weave in where it fits naturally.
/// These are <b>not</b> tested — the learner has already proven them. They are
/// here so Active words keep circulating instead of going quiet the moment they
/// mature (<c>Word Life Cycle.txt</c> §24–26), and they are chosen
/// exposure-first: the least-met words are offered first (rule R8).
///
/// Whether they were actually used is decided by the server afterwards, by
/// reading the returned text.
/// </param>
public sealed record ContentRequest(
    CefrLevel Level,
    IReadOnlyList<string> Interests,
    IReadOnlyList<AiTargetWord> Words,
    bool Listening,
    int ComprehensionCount,
    IReadOnlyList<string> ReuseWords);

/// <summary>Three sentences around a target word, for inference from context.</summary>
public sealed record GeneratedWordContext(
    string Word,
    string? Before,
    string Sentence,
    string? After);

public sealed record GeneratedQuestion(
    string Prompt,
    string Correct,
    IReadOnlyList<string> Distractors);

public sealed record GeneratedContent(
    string Text,
    IReadOnlyList<string> Sentences,
    IReadOnlyList<GeneratedQuestion> Comprehension,
    IReadOnlyList<GeneratedWordContext> Contexts,
    string PromptVersion,
    string Model,
    int Tokens,
    bool FromFallback,
    IReadOnlyList<GlossaryEntry>? Glossary = null);

/// <summary>
/// One word of the passage, with the meaning it carries <b>there</b>.
/// </summary>
/// <remarks>
/// Written during generation, when the model still knows which sense it meant.
/// A dictionary consulted at tap time can only offer every sense the word has
/// ever had — "bank" has six, and five of them are wrong in any given sentence.
///
/// It also carries the part of speech, which a learner needs in order to
/// understand what they are adding: <c>will</c> as an auxiliary is a different
/// thing to learn than <c>will</c> as a noun.
/// </remarks>
public sealed record GlossaryEntry(
    string Word,
    string Meaning,
    string PartOfSpeech);

/// <param name="Text">The passage to re-tell, exactly as the learner saw it.</param>
public sealed record RelevelRequest(
    string Text,
    CefrLevel FromLevel,
    CefrLevel ToLevel,
    IReadOnlyList<AiTargetWord> Words,
    int ComprehensionCount);

public sealed record WritingEvaluationRequest(
    string Word,
    string Meaning,
    string Definition,
    CefrLevel Level,
    string Sentence,
    /// <summary>
    /// The language the learner reads the app in, for the feedback only. Their
    /// sentence and the word they used are untouched (ADR-035).
    /// </summary>
    string FeedbackLanguage = "ar");

/// <summary>
/// What the evaluator observed. Deliberately no <c>Passed</c> field: the rule
/// that a small grammar slip must not fail correct usage
/// (<c>MVP Core.txt</c> §32) is applied in the domain, where a prompt change
/// cannot silently alter it.
/// </summary>
public sealed record WritingObservation(
    bool UsedWord,
    bool MeaningCorrect,
    bool UsageCorrect,
    bool Understandable,
    string GrammarNote,
    string Feedback,
    string? Suggestion,
    bool FromFallback,
    // Which model and which prompt produced this. Recorded on the session so a
    // shift in pass rates can be traced to a prompt edit rather than blamed on
    // learners (`MVP Core.txt` §62) — the same attribution Reading and
    // Listening already carry.
    string PromptVersion = "",
    string Model = "",
    int Tokens = 0);

public sealed record SpeakingTranscriptTurn(bool FromAi, string Text);

public sealed record SpeakingTurnRequest(
    string LearnerName,
    CefrLevel Level,
    IReadOnlyList<string> RemainingWords,
    IReadOnlyList<string> UsedWords,
    IReadOnlyList<SpeakingTranscriptTurn> Transcript);

public sealed record SpeakingObservation(
    string Reply,
    IReadOnlyList<string> WordsUsedNaturally,
    bool FromFallback,
    string PromptVersion = "",
    string Model = "",
    int Tokens = 0);

public sealed record SpeakingEvaluationRequest(
    string LearnerName,
    CefrLevel Level,
    IReadOnlyList<AiTargetWord> Words,
    IReadOnlyList<SpeakingTranscriptTurn> Transcript,
    /// <summary>
    /// The language the feedback is written in. The conversation itself stays
    /// English — that is the skill being practised (ADR-035).
    /// </summary>
    string FeedbackLanguage = "ar");

/// <summary>
/// What was observed about one word across the whole conversation.
/// </summary>
/// <remarks>
/// No <c>Passed</c> field, by construction — the same rule as writing
/// (ADR-015). And no pronunciation field: the transcript comes from speech
/// recognition, so a "mispronunciation" is indistinguishable from a recogniser
/// error, and scoring it would punish the learner for the microphone.
/// </remarks>
public sealed record SpeakingWordObservation(
    string Word,
    bool Used,
    bool MeaningCorrect,
    bool Understandable,
    bool GrammarAcceptable,
    bool MajorGrammarProblem,
    string Evidence,
    string Feedback);

public sealed record SpeakingEvaluation(
    IReadOnlyList<SpeakingWordObservation> Words,
    string Summary,
    bool FromFallback,
    string PromptVersion = "",
    string Model = "",
    int Tokens = 0);
