using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WordOs.Application.Abstractions;

namespace WordOs.Infrastructure.Ai;

/// <summary>
/// One step of WordOS AI through the AI service (ADR-133).
/// </summary>
/// <remarks>
/// Never throws: an unreachable model becomes a message saying so, not a
/// failed page. The tools themselves run in the backend, not here.
/// </remarks>
public sealed class HttpAdminAgentService(
    HttpClient http,
    IOptions<AiServiceOptions> options,
    ILogger<HttpAdminAgentService> logger) : IAdminAgentService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<AgentStepResult?> StepAsync(AgentStepRequest request, CancellationToken ct = default)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/ai/admin/agent")
            {
                Content = JsonContent.Create(new
                {
                    message = request.Message,
                    conversation = request.Conversation.Select(t => new { role = t.Role, text = t.Text, data = t.Data }),
                    steps = request.Steps.Select(s => new
                    {
                        calls = s.Calls.Select(c => new { tool = c.Tool, args_json = c.ArgsJson, why = c.Why }),
                        results = s.Results,
                    }),
                    tools = request.Tools,
                    dictionary = request.Dictionary,
                    today = request.Today,
                    must_answer = request.MustAnswer,
                }),
            };
            if (!string.IsNullOrEmpty(options.Value.Token))
                message.Headers.Add("X-Service-Token", options.Value.Token);

            using var response = await http.SendAsync(message, ct);
            // The free model's quota is used up — not a failure to retry at
            // once, so the admin is told when it is back (ADR-134).
            if ((int)response.StatusCode == 429)
            {
                var limit = await response.Content.ReadFromJsonAsync<LimitDto>(Json, ct);
                logger.LogWarning("Admin agent rate-limited for {Seconds}s", limit?.Error?.RetryAfterSeconds);
                return AgentStepResult.Limited(limit?.Error?.RetryAfterSeconds);
            }
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Admin agent returned {Status}", (int)response.StatusCode);
                return null;
            }

            var dto = await response.Content.ReadFromJsonAsync<Dto>(Json, ct);
            if (dto is null) return null;
            return new AgentStepResult(
                dto.Action ?? "answer",
                (dto.Calls ?? []).Select(c => new AgentToolCall(c.Tool, c.ArgsJson ?? "{}", c.Why ?? "")).ToList(),
                dto.Answer ?? string.Empty,
                dto.Attachments ?? [],
                dto.Title ?? string.Empty,
                dto.Model ?? string.Empty,
                dto.PromptVersion ?? string.Empty,
                dto.Tokens,
                Fallback: dto.Fallback);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException)
        {
            logger.LogWarning(e, "Admin agent unavailable");
            return null;
        }
    }

    private sealed record LimitDto(LimitError? Error);

    private sealed record LimitError(string? Code, int? RetryAfterSeconds);

    private sealed record CallDto(string Tool, string? ArgsJson, string? Why);

    private sealed record Dto(
        string? Action, List<CallDto>? Calls, string? Answer, List<string>? Attachments,
        string? Title, string? Model, string? PromptVersion, int Tokens, string? Fallback = null);
}
