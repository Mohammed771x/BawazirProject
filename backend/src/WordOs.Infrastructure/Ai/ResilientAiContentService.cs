using Microsoft.Extensions.Logging;
using WordOs.Application.Abstractions;

namespace WordOs.Infrastructure.Ai;

/// <summary>
/// Wraps the real AI service with a deterministic fallback.
/// </summary>
/// <remarks>
/// A learner mid-session must not lose their work because Gemini timed out.
/// When the AI is unavailable this produces usable — plainly weaker — content
/// and marks the result <c>FromFallback</c>, so:
///
/// <list type="bullet">
/// <item>the session continues rather than failing;</item>
/// <item>analytics can report how many sessions ran without AI, which is what
/// stops a silent quality collapse from being read as learners getting worse
/// (<c>MVP Core.txt</c> §62).</item>
/// </list>
///
/// The fallback is deliberately not clever. Pretending to be as good as the
/// real thing would hide the outage.
/// </remarks>
/// <param name="inner">
/// The real AI service. Typed as the interface rather than
/// <see cref="HttpAiContentService"/> so the decorator can wrap a test double —
/// otherwise the fallback path could only ever be exercised by taking the
/// network down.
/// </param>
public sealed class ResilientAiContentService(
    IAiContentService inner,
    ILogger<ResilientAiContentService> logger) : IAiContentService
{
    public async Task<GeneratedContent> GenerateContentAsync(
        ContentRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var content = await inner.GenerateContentAsync(request, ct);

            // AI output is never trusted to be well-formed. Content that would
            // leave the learner with nothing to answer is treated as a failure,
            // not passed through (rule R2).
            if (content.Sentences.Count > 0 && content.Comprehension.Count > 0)
                return content;

            logger.LogWarning(
                "AI returned unusable content ({Sentences} sentences, {Questions} questions)",
                content.Sentences.Count, content.Comprehension.Count);
        }
        catch (Exception e) when (e is AiServiceException or HttpRequestException
                                      or TaskCanceledException)
        {
            logger.LogWarning(e, "AI content generation failed; using fallback");
        }

        return FallbackContent(request);
    }

    public async Task<WritingObservation> EvaluateWritingAsync(
        WritingEvaluationRequest request,
        CancellationToken ct = default)
    {
        try
        {
            return await inner.EvaluateWritingAsync(request, ct);
        }
        catch (Exception e) when (e is AiServiceException or HttpRequestException
                                      or TaskCanceledException)
        {
            logger.LogWarning(e, "AI writing evaluation failed; using fallback");
            return FallbackWriting(request);
        }
    }

    public async Task<SpeakingObservation> SpeakingTurnAsync(
        SpeakingTurnRequest request,
        CancellationToken ct = default)
    {
        try
        {
            return await inner.SpeakingTurnAsync(request, ct);
        }
        catch (Exception e) when (e is AiServiceException or HttpRequestException
                                      or TaskCanceledException)
        {
            logger.LogWarning(e, "AI speaking turn failed; using fallback");
            return FallbackSpeaking(request);
        }
    }

    public async Task<SpeakingEvaluation> EvaluateSpeakingAsync(
        SpeakingEvaluationRequest request,
        CancellationToken ct = default)
    {
        try
        {
            return await inner.EvaluateSpeakingAsync(request, ct);
        }
        catch (Exception e) when (e is AiServiceException or HttpRequestException
                                      or TaskCanceledException)
        {
            logger.LogWarning(e, "AI speaking evaluation failed; using fallback");
            return FallbackSpeakingEvaluation(request);
        }
    }

    // ── Fallbacks ────────────────────────────────────────────────────────────

    private static GeneratedContent FallbackContent(ContentRequest request)
    {
        var sentences = new List<string>
        {
            "A student was preparing for an important week of study.",
        };

        var contexts = new List<GeneratedWordContext>();

        foreach (var word in request.Words)
        {
            var index = sentences.Count;
            var definition = string.IsNullOrWhiteSpace(word.Definition)
                ? "an important idea in this topic"
                : word.Definition;

            sentences.Add($"The teacher explained {word.Text}, which is {definition}.");
            sentences.Add("Everyone wrote it down before moving on.");

            contexts.Add(new GeneratedWordContext(
                Word: word.Text,
                Before: sentences[index - 1],
                Sentence: sentences[index],
                After: sentences[index + 1]));
        }

        sentences.Add("At the end of the lesson they reviewed all the new terms.");

        var questions = new List<GeneratedQuestion>
        {
            new("What was the student preparing for?", "An important week of study",
                ["A holiday", "A sports match", "A job interview"]),
            new("Who explained the new terms?", "The teacher",
                ["A classmate", "A visitor", "Nobody"]),
            new("What did everyone do after each explanation?", "They wrote it down",
                ["They left the room", "They argued", "They went home"]),
            new("What happened at the end of the lesson?", "They reviewed the new terms",
                ["They started a test", "They watched a film", "They cancelled the class"]),
            new("How many new terms were introduced?",
                request.Words.Count.ToString(),
                [(request.Words.Count + 3).ToString(),
                 (request.Words.Count + 7).ToString(), "None"]),
        };

        return new GeneratedContent(
            Text: string.Join(' ', sentences),
            Sentences: sentences,
            Comprehension: questions.Take(request.ComprehensionCount).ToList(),
            Contexts: contexts,
            PromptVersion: "fallback",
            Model: "fallback",
            Tokens: 0,
            FromFallback: true);
    }

    /// <summary>
    /// Crude on purpose: it checks that the word is present in some inflected
    /// form and that the sentence is long enough to judge. It does not measure
    /// grammar, coherence or task achievement — and says so by flagging itself.
    /// </summary>
    private static WritingObservation FallbackWriting(WritingEvaluationRequest request)
    {
        var sentence = request.Sentence.Trim();
        var normalized = sentence.ToLowerInvariant();
        var target = request.Word.ToLowerInvariant();

        var stem = target.EndsWith('e') ? target[..^1] : target;
        var used = normalized.Contains(target)
                   || System.Text.RegularExpressions.Regex.IsMatch(
                       normalized, $@"\b{System.Text.RegularExpressions.Regex.Escape(stem)}(e?[sd]|ing)\b");

        var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var understandable = words.Length >= 4;

        return new WritingObservation(
            UsedWord: used,
            MeaningCorrect: used && understandable,
            UsageCorrect: used && understandable,
            Understandable: understandable,
            GrammarNote: "unchecked",
            Feedback: used
                ? "Saved. Detailed feedback is unavailable right now, so this "
                  + "sentence will be reviewed again later."
                : $"Your sentence does not use \"{request.Word}\". Try again and "
                  + "include the word itself.",
            Suggestion: null,
            FromFallback: true);
    }

    /// <summary>
    /// Crude on purpose: it can see whether the learner said the word in a turn
    /// long enough to be a real sentence, and nothing more. Meaning and grammar
    /// are exactly what it cannot judge, so it does not pretend to — the
    /// observations it reports are the ones a regex can honestly support, and
    /// the result is flagged so a session evaluated this way is visible in
    /// analytics rather than silently counted as a normal one.
    /// </summary>
    private static SpeakingEvaluation FallbackSpeakingEvaluation(
        SpeakingEvaluationRequest request)
    {
        var learnerTurns = request.Transcript.Where(t => !t.FromAi).ToList();

        var words = request.Words.Select(word =>
        {
            var evidence = learnerTurns.FirstOrDefault(t =>
                t.Text.Contains(word.Text, StringComparison.OrdinalIgnoreCase)
                && t.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Length >= 5);

            var used = evidence is not null;

            return new SpeakingWordObservation(
                Word: word.Text,
                Used: used,
                // Not observable without a model. Reported as seen only when the
                // word was used in a substantial turn — the same standard the
                // domain applied before this evaluator existed (ADR-016).
                MeaningCorrect: used,
                Understandable: used,
                GrammarAcceptable: used,
                MajorGrammarProblem: false,
                Evidence: evidence?.Text ?? string.Empty,
                Feedback: used
                    ? "Saved. Detailed feedback is unavailable right now."
                    : $"Try to use \"{word.Text}\" next time.");
        }).ToList();

        return new SpeakingEvaluation(
            words,
            Summary: "Detailed feedback is unavailable right now.",
            FromFallback: true);
    }

    private static SpeakingObservation FallbackSpeaking(SpeakingTurnRequest request)
    {
        var last = request.Transcript.LastOrDefault(t => !t.FromAi)?.Text ?? string.Empty;

        var usedNow = request.RemainingWords
            .Where(w => last.Contains(w, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var next = request.RemainingWords
            .FirstOrDefault(w => !usedNow.Contains(w));

        var reply = next is null
            ? "Thanks — that covers every word for today."
            : $"Thank you. Could you say a little more about that, and use "
              + $"\"{next}\" in your answer?";

        return new SpeakingObservation(reply, usedNow, FromFallback: true);
    }
}
