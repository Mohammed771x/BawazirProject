using WordOs.Application.Abstractions;

namespace WordOs.Api.Tests;

/// <summary>A clock the tests move by hand.</summary>
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);

    /// <summary>Crosses one spaced gap, plus a margin.</summary>
    public void SkipDays(int days) => Advance(TimeSpan.FromDays(days) + TimeSpan.FromMinutes(1));
}

/// <summary>
/// Deterministic stand-in for the AI service.
/// </summary>
/// <remarks>
/// It produces well-formed content with predictable strings so a test can
/// assert on the <i>rules</i> — five comprehension questions, one context
/// question per word, a wrong answer returning to the queue — without any of
/// them depending on model output.
///
/// <see cref="FromFallback"/> stays false: this stands in for a healthy AI
/// service, so a test that sees <c>usedAiFallback</c> knows the resilience path
/// really did fire.
/// </remarks>
public sealed class StubAiContentService : IAiContentService
{
    /// <summary>Set by a test to prove a session survives an AI outage.</summary>
    public bool Fail { get; set; }

    /// <summary>
    /// When false the generator ignores the Active words it was offered —
    /// which is a legitimate outcome (they may not fit the passage), and must
    /// therefore produce no exposure at all.
    /// </summary>
    public bool ReuseActiveWords { get; set; } = true;

    /// <summary>How many times the passage repeats each reused word.</summary>
    public int RepeatReusedWords { get; set; } = 1;

    public int ContentCalls { get; private set; }

    /// <summary>The Active words the backend offered on the last call.</summary>
    public IReadOnlyList<string> LastReuseWords { get; private set; } = [];

    /// <summary>An Active word every speaking turn drops into its reply.</summary>
    public string? SpeakingReuseWord { get; set; }

    public Task<GeneratedContent> GenerateContentAsync(
        ContentRequest request,
        CancellationToken ct = default)
    {
        ContentCalls++;
        LastReuseWords = request.ReuseWords;
        if (Fail) throw new WordOs.Infrastructure.Ai.AiServiceException("stub outage");

        var sentences = new List<string> { "The class began early on a bright morning." };

        // Stands in for a generator weaving Active vocabulary into the passage.
        if (ReuseActiveWords)
        {
            foreach (var reused in request.ReuseWords)
            {
                for (var i = 0; i < RepeatReusedWords; i++)
                    sentences.Add($"Someone mentioned {reused} again during the lesson.");
            }
        }
        var contexts = new List<GeneratedWordContext>();

        foreach (var word in request.Words)
        {
            var index = sentences.Count;
            sentences.Add($"Then the teacher used the word {word.Text} in a sentence.");
            sentences.Add("The students copied it into their notebooks.");
            contexts.Add(new GeneratedWordContext(
                word.Text, sentences[index - 1], sentences[index], sentences[index + 1]));
        }

        var questions = Enumerable.Range(1, request.ComprehensionCount)
            .Select(i => new GeneratedQuestion(
                $"Stub question {i}?",
                $"correct-{i}",
                [$"wrong-{i}-a", $"wrong-{i}-b", $"wrong-{i}-c"]))
            .ToList();

        return Task.FromResult(new GeneratedContent(
            Text: string.Join(' ', sentences),
            Sentences: sentences,
            Comprehension: questions,
            Contexts: contexts,
            PromptVersion: "stub-v1",
            Model: "stub",
            Tokens: 0,
            FromFallback: false));
    }

    public Task<WritingObservation> EvaluateWritingAsync(
        WritingEvaluationRequest request,
        CancellationToken ct = default)
    {
        if (Fail) throw new WordOs.Infrastructure.Ai.AiServiceException("stub outage");

        var used = request.Sentence.Contains(
            request.Word, StringComparison.OrdinalIgnoreCase);

        // Deliberately reports a grammar problem on every sentence, so a test
        // can prove §32: a grammar note alone never fails correct usage.
        return Task.FromResult(new WritingObservation(
            UsedWord: used,
            MeaningCorrect: used,
            UsageCorrect: used,
            Understandable: request.Sentence.Split(' ').Length >= 3,
            GrammarNote: "missing article",
            Feedback: "stub feedback",
            Suggestion: "stub suggestion",
            FromFallback: false,
            // The real service reports these on every call; a double that omits
            // them would let an attribution regression pass unnoticed.
            PromptVersion: "stub-writing-v1",
            Model: "stub",
            Tokens: 0));
    }

    public Task<SpeakingObservation> SpeakingTurnAsync(
        SpeakingTurnRequest request,
        CancellationToken ct = default)
    {
        if (Fail) throw new WordOs.Infrastructure.Ai.AiServiceException("stub outage");

        var next = request.RemainingWords.FirstOrDefault();

        // A conversation reuses Active vocabulary too — every turn, which is
        // exactly the case that must not accumulate exposures.
        var reuse = ReuseActiveWords && SpeakingReuseWord is not null
            ? $" I remember you know {SpeakingReuseWord}."
            : string.Empty;

        return Task.FromResult(new SpeakingObservation(
            Reply: (next is null
                ? "That covers everything for today."
                : $"Tell me more, and try to use \"{next}\".") + reuse,
            WordsUsedNaturally: [],
            FromFallback: false,
            PromptVersion: "stub-speaking-v1",
            Model: "stub",
            Tokens: 0));
    }

    /// <summary>
    /// What the stub reports for every target word at the end of a
    /// conversation. Tests set this to drive the pass/fail rule without a model.
    /// </summary>
    public SpeakingWordObservation? SpeakingVerdict { get; set; }

    /// <summary>How many end-of-conversation evaluations were requested.</summary>
    public int SpeakingEvaluations { get; private set; }

    public Task<SpeakingEvaluation> EvaluateSpeakingAsync(
        SpeakingEvaluationRequest request,
        CancellationToken ct = default)
    {
        SpeakingEvaluations++;
        if (Fail) throw new WordOs.Infrastructure.Ai.AiServiceException("stub outage");

        var words = request.Words.Select(w =>
        {
            if (SpeakingVerdict is { } verdict) return verdict with { Word = w.Text };

            // Default: whatever the learner actually said, judged generously —
            // used and understandable, with a grammar slip that must not matter.
            var used = request.Transcript.Any(t =>
                !t.FromAi &&
                t.Text.Contains(w.Text, StringComparison.OrdinalIgnoreCase));

            return new SpeakingWordObservation(
                Word: w.Text,
                Used: used,
                MeaningCorrect: used,
                Understandable: used,
                GrammarAcceptable: false,
                MajorGrammarProblem: false,
                Evidence: used ? "stub evidence" : string.Empty,
                Feedback: "stub feedback");
        }).ToList();

        return Task.FromResult(new SpeakingEvaluation(
            words, "stub summary", FromFallback: false,
            PromptVersion: "stub-eval-v1", Model: "stub", Tokens: 0));
    }
}