using System.Text.RegularExpressions;
using WordOs.Application.Abstractions;
using WordOs.Domain.Common;
using WordOs.Infrastructure.Ai;

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

    /// <summary>Set by a test to make the meaning checker reject (ADR-074).</summary>
    public bool RejectMeanings { get; set; }

    /// <summary>The correction the checker offers, when it offers one.</summary>
    public string? MeaningCorrection { get; set; }

    /// <summary>How many meanings were checked. A test asserts this is zero
    /// for the paths that must not call the checker at all.</summary>
    public int MeaningChecks { get; private set; }

    /// <summary>Set by a test to make the checker deny the word is English
    /// (ADR-075).</summary>
    public bool RejectWords { get; set; }

    /// <summary>The spelling the checker offers for a rejected word.</summary>
    public string? WordCorrection { get; set; }

    /// <summary>
    /// Whether the lexicon was consulted on the last check — i.e. whether the
    /// backend found the word before asking.
    /// </summary>
    /// <remarks>
    /// The distinction is the whole of ADR-075, and it is invisible in the
    /// response: a test that only looked at the returned word could not tell
    /// "found it and asked about the meaning" from "did not find it and asked
    /// about both".
    /// </remarks>
    public bool? LastCheckKnownWord { get; private set; }

    /// <summary>The part of speech the checker reports for an unknown word.</summary>
    public string UnknownWordPartOfSpeech { get; set; } = "verb";

    /// <summary>The CEFR band the checker reports for an unknown word.</summary>
    public string UnknownWordLevel { get; set; } = "C1";

    public Task<MeaningCheck> CheckMeaningAsync(
        MeaningCheckRequest request,
        CancellationToken ct = default)
    {
        MeaningChecks++;
        LastCheckKnownWord = request.KnownWord;

        // `Fail` is the AI outage switch the whole double shares. The check has
        // no fallback (ADR-074), so it throws where the others degrade.
        if (Fail) throw new AiServiceException("stub failure");

        return Task.FromResult(new MeaningCheck(
            Matches: !RejectMeanings,
            Corrected: MeaningCorrection,
            Suggestions: RejectMeanings ? ["كتاب", "مؤلف"] : [],
            Note: RejectMeanings ? "هذا المعنى غير صحيح." : "المعنى صحيح.",
            // Mirrors the real service: a word the backend already had is never
            // questioned, whatever this double is configured to say.
            WordRecognized: request.KnownWord || !RejectWords,
            CorrectedWord: request.KnownWord ? null : WordCorrection,
            DefinitionEn: request.KnownWord ? null : "a stub definition",
            PartOfSpeech: request.KnownWord ? null : UnknownWordPartOfSpeech,
            Level: request.KnownWord ? null : UnknownWordLevel));
    }

    public Task<GeneratedContent> GenerateContentAsync(
        ContentRequest request,
        CancellationToken ct = default)
    {
        ContentCalls++;
        // The texts alone: the tests that read this ask which words were
        // offered for reuse, not what shape they were offered in.
        LastReuseWords = request.ReuseWords.Select(w => w.Text).ToList();
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

        // Every word of what it just wrote, because that is what the rule asks
        // of the real generator and what the service now guarantees by
        // repairing whatever the model left out (ADR-065). A stub that glossed
        // three words would let a test pass on a passage most of whose taps
        // cannot be answered — which is the bug, not the fixture.
        var glossary = GlossaryFor(sentences, request.Words);

        return Task.FromResult(new GeneratedContent(
            Text: string.Join(' ', sentences),
            Sentences: sentences,
            Comprehension: questions,
            Contexts: contexts,
            PromptVersion: "stub-v1",
            Model: "stub",
            Tokens: 0,
            FromFallback: false,
            Glossary: glossary));
    }

    /// <summary>
    /// A glossary covering every word of the passage the stub just wrote.
    /// </summary>
    /// <remarks>
    /// Tokenised with the client's own rule, so "the words a learner can tap"
    /// and "the words that have an entry" are the same set by construction.
    /// The learner's own words keep the Arabic they were added with; every
    /// other word gets a placeholder, because what matters to a test is that
    /// an answer exists.
    /// </remarks>
    public static List<GlossaryEntry> GlossaryFor(
        IEnumerable<string> sentences,
        IReadOnlyList<AiTargetWord> words)
    {
        var glossary = new List<GlossaryEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var word in words)
        {
            if (!seen.Add(word.Text)) continue;
            glossary.Add(new GlossaryEntry(
                word.Text, word.Meaning, word.PartOfSpeech));
        }

        foreach (var sentence in sentences)
        {
            foreach (var match in PassageWords.Matches(sentence).Cast<Match>())
            {
                if (!seen.Add(match.Value)) continue;
                glossary.Add(new GlossaryEntry(
                    match.Value, $"معنى \"{match.Value}\" هنا", "other"));
            }
        }

        return glossary;
    }

    /// <summary>
    /// What counts as a tappable word, copied from the Flutter passage widget:
    /// letters and digits, plus the apostrophes and hyphens that live inside a
    /// word. The two must agree — a glossary keyed on anything else has holes.
    /// </summary>
    public static readonly Regex PassageWords =
        new(@"[\p{L}\p{N}][\p{L}\p{N}'’\-]*", RegexOptions.Compiled);

    /// <summary>
    /// The language the last evaluation was asked to write its feedback in.
    /// </summary>
    /// <remarks>
    /// Recorded because the header that carries it crosses four layers before
    /// it reaches the prompt, and every one of them is a place it could be
    /// dropped silently (ADR-035).
    /// </remarks>
    public string? LastFeedbackLanguage { get; private set; }

    public Task<WritingObservation> EvaluateWritingAsync(
        WritingEvaluationRequest request,
        CancellationToken ct = default)
    {
        LastFeedbackLanguage = request.FeedbackLanguage;

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
            // Echoed so a test can see which language was asked for without
            // reaching into the double.
            Feedback: $"stub feedback [{request.FeedbackLanguage}]",
            // Echoed so a test can see which level the rewrite was asked for:
            // that level is the whole point of the control on a writing task
            // (ADR-038).
            Suggestion: $"stub rewrite at {request.Level.ToWire()}",
            FromFallback: false,
            // The real service reports these on every call; a double that omits
            // them would let an attribution regression pass unnoticed.
            PromptVersion: "stub-writing-v1",
            Model: "stub",
            Tokens: 0));
    }

    /// <summary>How many conversational turns were asked for.</summary>
    public int SpeakingTurns { get; private set; }

    /// <summary>The last turn's remaining words, so a test can see the closing.</summary>
    public IReadOnlyList<string> LastRemainingWords { get; private set; } = [];

    /// <summary>The near misses the last turn was told about (ADR-050).</summary>
    public IReadOnlyList<SpeakingFormReminder> LastFormReminders { get; private set; } = [];

    /// <summary>The shape each remaining word was described with (ADR-047).</summary>
    public IReadOnlyList<AiTargetWord> LastRemainingShapes { get; private set; } = [];

    /// <summary>Words the closing turn was told the learner never reached.</summary>
    public IReadOnlyList<string> LastUnusedWords { get; private set; } = [];

    public Task<SpeakingObservation> SpeakingTurnAsync(
        SpeakingTurnRequest request,
        CancellationToken ct = default)
    {
        SpeakingTurns++;
        LastRemainingWords = request.RemainingWords;
        LastFormReminders = request.FormReminders ?? [];
        LastRemainingShapes = request.RemainingShapes ?? [];
        LastUnusedWords = request.UnusedWords ?? [];

        if (Fail) throw new WordOs.Infrastructure.Ai.AiServiceException("stub outage");

        var next = request.RemainingWords.FirstOrDefault();

        // A conversation reuses Active vocabulary too — every turn, which is
        // exactly the case that must not accumulate exposures.
        var reuse = ReuseActiveWords && SpeakingReuseWord is not null
            ? $" I remember you know {SpeakingReuseWord}."
            : string.Empty;

        return Task.FromResult(new SpeakingObservation(
            Reply: (next is null
                ? "That covers everything for today. It was good talking to you."
                : $"Tell me more, and try to use \"{next}\".") + reuse,
            // The real service reports only the words the learner *named*
            // rather than used — whether a word appears at all is read from the
            // transcript by the endpoint (ADR-048). This stands in for that one
            // judgement: "so I can use research in a sentence" names it.
            WordsOnlyNamed: request.Transcript
                .Where(t => !t.FromAi)
                .Reverse()
                .Take(1)
                .SelectMany(t => request.RemainingWords.Where(w =>
                    t.Text.Contains($"use {w}", StringComparison.OrdinalIgnoreCase)))
                .ToList(),
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

    /// <summary>How many placement evaluations were requested.</summary>
    public int PlacementEvaluations { get; private set; }

    /// <summary>
    /// Forces a specific score for every placement answer, so a test can prove
    /// the AI's judgement actually reaches the band rather than being computed
    /// around.
    /// </summary>
    public double? PlacementScore { get; set; }

    /// <summary>Re-tellings requested, and the level each asked for.</summary>
    public List<CefrLevel> RelevelRequests { get; } = [];

    public Task<GeneratedContent> RelevelContentAsync(
        RelevelRequest request,
        CancellationToken ct = default)
    {
        RelevelRequests.Add(request.ToLevel);
        if (Fail) throw new WordOs.Infrastructure.Ai.AiServiceException("stub outage");

        // Recognisably the *same* story, told differently — which is the
        // property the endpoint exists to provide.
        var sentences = new List<string>
        {
            $"[{request.ToLevel.ToWire()}] A student was preparing for an "
            + "important week of study.",
            "They read about it every evening.",
        };

        foreach (var word in request.Words)
        {
            sentences.Add($"The teacher explained {word.Text} again, simply.");
        }

        var questions = Enumerable.Range(0, request.ComprehensionCount)
            .Select(i => new GeneratedQuestion(
                $"Re-told question {i + 1}?",
                "The right answer",
                ["Wrong one", "Another wrong one", "A third"]))
            .ToList();

        return Task.FromResult(new GeneratedContent(
            Text: string.Join(' ', sentences),
            Sentences: sentences,
            Comprehension: questions,
            Contexts: request.Words
                .Select(w => new GeneratedWordContext(
                    w.Text, sentences[0], sentences[^1], null))
                .ToList(),
            PromptVersion: "stub-relevel-v1",
            Model: "stub",
            Tokens: 7,
            FromFallback: false,
            // A re-telling is glossed as completely as a fresh passage. It is
            // the path a learner takes to reach another level, so a thin
            // glossary here is a tap that stops working the moment they do.
            Glossary: GlossaryFor(sentences, request.Words)));
    }

    public Task<PlacementEvaluation> EvaluatePlacementAsync(
        PlacementEvaluationRequest request,
        CancellationToken ct = default)
    {
        PlacementEvaluations++;
        if (Fail) throw new WordOs.Infrastructure.Ai.AiServiceException("stub outage");

        var ratings = request.Answers.Select(a => new PlacementAnswerRating(
            a.ItemId,
            a.Level,
            // A crude stand-in for judgement: an empty or near-empty answer
            // rates badly, a substantial one rates well. Enough for a test to
            // tell a strong run from a weak one without pretending to be a
            // language model.
            PlacementScore ?? RateStub(a.Answer),
            "stub rating")).ToList();

        return Task.FromResult(new PlacementEvaluation(
            ratings,
            OverallLevel: null,
            Summary: "stub placement summary",
            FromFallback: false,
            PromptVersion: "stub-placement-v1",
            Model: "stub",
            Tokens: 5));
    }

    private static double RateStub(string answer)
    {
        var words = answer.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return words switch
        {
            0 => 0,
            < 6 => 0.15,
            _ => 0.85,
        };
    }

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