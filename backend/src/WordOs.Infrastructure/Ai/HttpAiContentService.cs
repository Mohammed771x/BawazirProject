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

    public int TimeoutSeconds { get; init; } = 90;
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
            }),
            listening = request.Listening,
            comprehension_count = request.ComprehensionCount,
            reuse_words = request.ReuseWords,
        };

        var response = await PostAsync<ContentDto>("/ai/content", payload, ct);

        return new GeneratedContent(
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
            FromFallback: false);
    }

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
        };

        var response = await PostAsync<SpeakingDto>("/ai/speaking/turn", payload, ct);

        return new SpeakingObservation(
            response.Reply, response.WordsUsedNaturally, FromFallback: false,
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
        };

        var response = await PostAsync<SpeakingEvalDto>(
            "/ai/speaking/evaluate", payload, ct);

        return new SpeakingEvaluation(
            response.Words.Select(w => new SpeakingWordObservation(
                w.Word, w.Used, w.MeaningCorrect, w.Understandable,
                w.GrammarAcceptable, w.MajorGrammarProblem,
                w.Evidence, w.Feedback)).ToList(),
            response.Summary,
            FromFallback: false,
            PromptVersion: response.PromptVersion,
            Model: response.Model,
            Tokens: response.Tokens);
    }

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
        int Tokens);

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

    private sealed record SpeakingDto(
        string Reply,
        List<string> WordsUsedNaturally,
        string PromptVersion,
        string Model,
        int Tokens);

    private sealed record SpeakingEvalDto(
        List<SpeakingEvalWordDto> Words,
        string Summary,
        string PromptVersion,
        string Model,
        int Tokens);

    private sealed record SpeakingEvalWordDto(
        string Word,
        bool Used,
        bool MeaningCorrect,
        bool Understandable,
        bool GrammarAcceptable,
        bool MajorGrammarProblem,
        string Evidence,
        string Feedback);
}

public sealed class AiServiceException(string message) : Exception(message);
