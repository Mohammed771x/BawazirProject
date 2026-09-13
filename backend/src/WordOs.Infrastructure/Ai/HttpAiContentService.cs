using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WordOs.Application.Abstractions;
using WordOs.Domain.Common;

namespace WordOs.Infrastructure.Ai;

public sealed class AiServiceOptions
{
    public const string SectionName = "AiService";

    [Required]
    public string BaseUrl { get; init; } = "http://127.0.0.1:8099";

    /// <summary>
    /// Shared secret proving the caller is this backend.
    /// </summary>
    /// <remarks>
    /// Never reaches Flutter: the mobile client talks to this API, which talks
    /// to the AI service. Without it, anyone who can reach the AI service's
    /// port could spend the Gemini budget.
    /// </remarks>
    public string Token { get; init; } = string.Empty;

    /// <summary>
    /// How long one AI call may take before the fallback answers instead.
    /// </summary>
    /// <remarks>
    /// This has to stay comfortably <b>under</b> the client's own receive
    /// timeout, and it did not. Both were 90 seconds, so a hung AI service was
    /// measured returning at 90.08s while the Flutter client
    /// (<c>http_wordos_api.dart</c>) gave up at 90.00s — the learner saw "the
    /// server took too long" and the whole
    /// <see cref="ResilientAiContentService"/> fallback, which had done its job
    /// perfectly, was never seen by anyone.
    ///
    /// It must stay *below* the client's receive timeout, so the
    /// degraded-but-usable session actually arrives: they are one setting in
    /// two places and they move together. Raising this above the client's
    /// re-creates the bug where the learner saw a timeout instead of a lesson.
    ///
    /// Twenty-five seconds was right when a passage was a dozen sentences and
    /// the measured healthy call took eight or nine. Passages are now sized
    /// like the exam texts they stand in for (ADR-066), which briefly pushed a
    /// C2 generation to forty-five seconds and a re-telling to seventy-seven —
    /// past the budget, so the learner was told the passage could not be
    /// rewritten. Building the glossary in parallel instead (ADR-067) brought
    /// the whole ladder back to about twenty seconds, C2 included.
    ///
    /// Sixty seconds against a client that waits ninety: roughly three times
    /// the measured worst case, which is the margin a shared instance under
    /// load needs and no more.
    /// </remarks>
    public int TimeoutSeconds { get; init; } = 60;
}

/// <summary>
/// Calls the Python AI service over HTTP.
/// </summary>
/// <remarks>
/// The only place in the backend that knows the AI service exists. It performs
/// no judgement: it forwards a request and returns what came back, so every
/// pass/fail rule stays in the domain (rule R2).
///
/// A failure here is never fatal to a session — the caller falls back and
/// records that it did, so analytics can show how many sessions ran degraded.
/// </remarks>
public sealed class HttpAiContentService(
    HttpClient http,
    IOptions<AiServiceOptions> options,
    ILogger<HttpAiContentService> logger) : IAiContentService
{
    private readonly AiServiceOptions _options = options.Value;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<GeneratedContent> GenerateContentAsync(
        ContentRequest request,
        CancellationToken ct = default)
    {
        var payload = new
        {
            level = request.Level.ToWire(),
            interests = request.Interests,
            words = request.Words.Select(w => new
            {
                text = w.Text,
                meaning = w.Meaning,
                definition = w.Definition,
                part_of_speech = w.PartOfSpeech,
                // Which form it is, and whether the passage may pluralise it
                // (ADR-047).
                form = w.Form,
                may_pluralise = w.MayPluralise,
            }),
            listening = request.Listening,
            comprehension_count = request.ComprehensionCount,
            // Shaped like the target words, so a form the learner knows comes
            // back in that form (ADR-047).
            reuse_words = request.ReuseWords.Select(w => new
            {
                text = w.Text,
                meaning = w.Meaning,
                definition = w.Definition,
                part_of_speech = w.PartOfSpeech,
                form = w.Form,
                may_pluralise = w.MayPluralise,
            }),
        };

        var response = await PostAsync<ContentDto>("/ai/content", payload, ct);
        return ToContent(response);
    }

    public async Task<GeneratedContent> RelevelContentAsync(
        RelevelRequest request,
        CancellationToken ct = default)
    {
        var payload = new
        {
            text = request.Text,
            from_level = request.FromLevel.ToWire(),
            to_level = request.ToLevel.ToWire(),
            words = request.Words.Select(w => new
            {
                text = w.Text,
                meaning = w.Meaning,
                definition = w.Definition,
                part_of_speech = w.PartOfSpeech,
                // Which form it is, and whether the passage may pluralise it
                // (ADR-047).
                form = w.Form,
                may_pluralise = w.MayPluralise,
            }),
            comprehension_count = request.ComprehensionCount,
        };

        var response = await PostAsync<ContentDto>(
            "/ai/content/relevel", payload, ct);

        return ToContent(response);
    }

    private static GeneratedContent ToContent(ContentDto response) =>
        new(
            Text: response.Text,
            Sentences: response.Sentences,
            Comprehension: response.Comprehension
                .Select(q => new GeneratedQuestion(q.Prompt, q.Correct, q.Distractors))
                .ToList(),
            Contexts: response.Contexts
                .Select(c => new GeneratedWordContext(c.Word, c.Before, c.Sentence, c.After))
                .ToList(),
            PromptVersion: response.PromptVersion,
            Model: response.Model,
            Tokens: response.Tokens,
            FromFallback: false,
            Glossary: (response.Glossary ?? [])
                .Select(g => new GlossaryEntry(g.Word, g.MeaningAr, g.PartOfSpeech))
                .ToList(),
            Title: string.IsNullOrWhiteSpace(response.Title) ? null : response.Title);

    public async Task<WritingObservation> EvaluateWritingAsync(
        WritingEvaluationRequest request,
        CancellationToken ct = default)
    {
        var payload = new
        {
            word = request.Word,
            meaning = request.Meaning,
            definition = request.Definition,
            level = request.Level.ToWire(),
            sentence = request.Sentence,
            feedback_language = request.FeedbackLanguage,
        };

        var response = await PostAsync<WritingDto>("/ai/writing", payload, ct);

        return new WritingObservation(
            response.UsedWord,
            response.MeaningCorrect,
            response.UsageCorrect,
            response.Understandable,
            response.GrammarNote,
            response.Feedback,
            response.Suggestion,
            FromFallback: false,
            PromptVersion: response.PromptVersion,
            Model: response.Model,
            Tokens: response.Tokens);
    }

    public async Task<MeaningCheck> CheckMeaningAsync(
        MeaningCheckRequest request,
        CancellationToken ct = default)
    {
        var payload = new
        {
            word = request.Word,
            definitions = request.Definitions,
            part_of_speech = request.PartOfSpeech,
            meaning = request.Meaning,
            feedback_language = request.FeedbackLanguage,
            // Turns on the second question — is this an English word at all
            // (ADR-075). Sent explicitly rather than inferred from an empty
            // definition list, which would also be true of a word this service
            // does hold and has no English gloss for.
            known_word = request.KnownWord,
        };

        var response =
            await PostAsync<MeaningCheckDto>("/ai/meaning/check", payload, ct);

        return new MeaningCheck(
            response.Matches,
            response.Corrected,
            response.Suggestions ?? [],
            response.Note,
            PromptVersion: response.PromptVersion,
            Model: response.Model,
            Tokens: response.Tokens,
            // Absent for a word the lexicon already knew: nothing asked, so
            // nothing answered, and "recognized" is the truth about it.
            WordRecognized: response.WordRecognized ?? true,
            CorrectedWord: response.CorrectedWord,
            DefinitionEn: response.DefinitionEn,
            PartOfSpeech: response.WordPartOfSpeech,
            Level: response.CefrLevel);
    }

    public async Task<SpeakingObservation> SpeakingTurnAsync(
        SpeakingTurnRequest request,
        CancellationToken ct = default)
    {
        var payload = new
        {
            learner_name = request.LearnerName,
            level = request.Level.ToWire(),
            remaining_words = request.RemainingWords,
            used_words = request.UsedWords,
            transcript = request.Transcript.Select(t => new
            {
                from_ai = t.FromAi,
                text = t.Text,
            }),
            interests = request.Interests ?? [],
            // What each remaining word is, so the question invites that form
            // (ADR-047), and which words the learner nearly reached (ADR-050).
            remaining_shapes = (request.RemainingShapes ?? []).Select(w => new
            {
                text = w.Text,
                meaning = w.Meaning,
                // Which sense the learner is practising, in English. Without
                // it the tutor sees the bare string "can" and has no way to
                // know whether it is the modal, the tin, or preserving fruit —
                // so it asks a question the word cannot answer (ADR-069).
                definition = w.Definition,
                part_of_speech = w.PartOfSpeech,
                form = w.Form,
                may_pluralise = w.MayPluralise,
            }),
            unused_words = request.UnusedWords ?? [],
            form_reminders = (request.FormReminders ?? []).Select(r => new
            {
                word = r.Word,
                form = r.Form,
                said = r.Said,
            }),
        };

        var response = await PostAsync<SpeakingDto>("/ai/speaking/turn", payload, ct);

        return new SpeakingObservation(
            response.Reply, response.WordsOnlyNamed, FromFallback: false,
            PromptVersion: response.PromptVersion,
            Model: response.Model,
            Tokens: response.Tokens);
    }

    public async Task<SpeakingEvaluation> EvaluateSpeakingAsync(
        SpeakingEvaluationRequest request,
        CancellationToken ct = default)
    {
        var payload = new
        {
            learner_name = request.LearnerName,
            level = request.Level.ToWire(),
            words = request.Words.Select(w => new
            {
                text = w.Text,
                meaning = w.Meaning,
                definition = w.Definition,
            }),
            transcript = request.Transcript.Select(t => new
            {
                from_ai = t.FromAi,
                text = t.Text,
            }),
            feedback_language = request.FeedbackLanguage,
        };

        var response = await PostAsync<SpeakingEvalDto>(
            "/ai/speaking/evaluate", payload, ct);

        return new SpeakingEvaluation(
            response.Words.Select(w => new SpeakingWordObservation(
                w.Word, w.Used, w.MeaningCorrect, w.Understandable,
                w.GrammarAcceptable, w.MajorGrammarProblem,
                w.Evidence, w.Feedback, w.Better)).ToList(),
            response.Summary,
            FromFallback: false,
            PromptVersion: response.PromptVersion,
            Model: response.Model,
            Tokens: response.Tokens);
    }

    public async Task<PlacementEvaluation> EvaluatePlacementAsync(
        PlacementEvaluationRequest request,
        CancellationToken ct = default)
    {
        var payload = new
        {
            skill = request.Skill.ToWire(),
            answers = request.Answers.Select(a => new
            {
                item_id = a.ItemId,
                level = a.Level.ToWire(),
                prompt = a.Prompt,
                answer = a.Answer,
            }),
        };

        var response = await PostAsync<PlacementEvalDto>(
            "/ai/placement/evaluate", payload, ct);

        return new PlacementEvaluation(
            response.Answers.Select(a => new PlacementAnswerRating(
                a.ItemId,
                ParseLevel(a.EstimatedLevel),
                Math.Clamp(a.Score, 0, 1),
                a.Evidence)).ToList(),
            ParseLevel(response.OverallLevel),
            response.Summary,
            FromFallback: false,
            PromptVersion: response.PromptVersion,
            Model: response.Model,
            Tokens: response.Tokens);
    }

    /// <summary>
    /// A band the model named, or null when it named something unrecognisable.
    /// </summary>
    /// <remarks>
    /// Null rather than a guess: the score is what the estimator actually
    /// consumes, and inventing a band here would put a made-up label into the
    /// evidence the Owner audits.
    /// </remarks>
    private static CefrLevel? ParseLevel(string? raw) =>
        Enum.TryParse<CefrLevel>((raw ?? string.Empty).Trim(), ignoreCase: true,
            out var level)
            ? level
            : null;

    private async Task<T> PostAsync<T>(string path, object payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(payload),
        };

        // The token authenticates this backend to the AI service. It is a
        // header, never a query parameter — query strings reach proxy logs.
        if (!string.IsNullOrEmpty(_options.Token))
            request.Headers.Add("X-Service-Token", _options.Token);

        using var response = await http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            // Logged without the request payload: it carries the learner's own
            // writing (docs/07-SECURITY.md §9).
            logger.LogWarning(
                "AI service {Path} returned {Status}: {Body}",
                path, (int)response.StatusCode, Truncate(body, 300));

            throw new AiServiceException(
                $"AI service returned {(int)response.StatusCode}");
        }

        var result = await response.Content.ReadFromJsonAsync<T>(Json, ct);
        return result ?? throw new AiServiceException("AI service returned no body");
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    // Snake_case on the wire, matching the Python service's contract.
    private sealed record ContentDto(
        string Text,
        List<string> Sentences,
        List<QuestionDto> Comprehension,
        List<ContextDto> Contexts,
        string PromptVersion,
        string Model,
        int Tokens,
        List<GlossaryDto>? Glossary = null,
        string? Title = null);

    private sealed record QuestionDto(
        string Prompt, string Correct, List<string> Distractors);

    private sealed record ContextDto(
        string Word, string? Before, string Sentence, string? After);

    private sealed record WritingDto(
        bool UsedWord,
        bool MeaningCorrect,
        bool UsageCorrect,
        bool Understandable,
        string GrammarNote,
        string Feedback,
        string? Suggestion,
        string PromptVersion,
        string Model,
        int Tokens);

    /// <param name="WordPartOfSpeech">
    /// Named apart from the wire field it binds to (<c>word_part_of_speech</c>)
    /// only because <c>PartOfSpeech</c> would read, at the call site, like the
    /// part of speech that was *sent* — which is the commonest sense's, and is a
    /// different thing from the one the model is reporting back (ADR-075).
    /// </param>
    private sealed record MeaningCheckDto(
        bool Matches,
        string? Corrected,
        List<string>? Suggestions,
        string Note,
        string PromptVersion,
        string Model,
        int Tokens,
        // Nullable: a service that has not been deployed with ADR-075 yet omits
        // these, and the word it was asked about was one the lexicon knew.
        bool? WordRecognized = null,
        string? CorrectedWord = null,
        string? DefinitionEn = null,
        string? WordPartOfSpeech = null,
        string? CefrLevel = null);

    private sealed record SpeakingDto(
        string Reply,
        List<string> WordsOnlyNamed,
        string PromptVersion,
        string Model,
        int Tokens);

    private sealed record SpeakingEvalDto(
        List<SpeakingEvalWordDto> Words,
        string Summary,
        string PromptVersion,
        string Model,
        int Tokens);

    private sealed record GlossaryDto(
        string Word,
        string MeaningAr,
        string PartOfSpeech);

    private sealed record PlacementEvalDto(
        List<PlacementEvalAnswerDto> Answers,
        string OverallLevel,
        string Summary,
        string PromptVersion,
        string Model,
        int Tokens);

    private sealed record PlacementEvalAnswerDto(
        string ItemId,
        string EstimatedLevel,
        double Score,
        string Evidence);

    private sealed record SpeakingEvalWordDto(
        string Word,
        bool Used,
        bool MeaningCorrect,
        bool Understandable,
        bool GrammarAcceptable,
        bool MajorGrammarProblem,
        string Evidence,
        string Feedback,
        string Better = "");
}

public sealed class AiServiceException(string message) : Exception(message);
