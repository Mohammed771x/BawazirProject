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
}

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
    bool FromFallback);

public sealed record WritingEvaluationRequest(
    string Word,
    string Meaning,
    string Definition,
    CefrLevel Level,
    string Sentence);

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
    IReadOnlyList<SpeakingTranscriptTurn> Transcript);

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
