using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WordOs.Application.Abstractions;

namespace WordOs.Infrastructure.Ai;

/// <summary>
/// Asks the AI service to read an investigation's evidence (ADR-125).
/// </summary>
/// <remarks>
/// Never throws. An admin asking a question must get the data whether or not
/// the model is up — the interpretation is the optional part, and the rules
/// written in the backend stand in for it.
/// </remarks>
public sealed class HttpAdminInsightService(
    HttpClient http,
    IOptions<AiServiceOptions> options,
    ILogger<HttpAdminInsightService> logger) : IAdminInsightService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public async Task<InsightResponse?> InterpretAsync(InsightRequest request, CancellationToken ct = default)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/ai/admin/investigate")
            {
                Content = JsonContent.Create(new
                {
                    section = request.Section,
                    question = request.Question,
                    evidence = request.EvidenceJson,
                }),
            };
            if (!string.IsNullOrEmpty(options.Value.Token))
                message.Headers.Add("X-Service-Token", options.Value.Token);

            using var response = await http.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Admin insight returned {Status}", (int)response.StatusCode);
                return null;
            }

            var dto = await response.Content.ReadFromJsonAsync<Dto>(Json, ct);
            if (dto is null || string.IsNullOrWhiteSpace(dto.Summary)) return null;

            return new InsightResponse(
                dto.Summary.Trim(),
                dto.Interpretation ?? [],
                dto.Hypotheses ?? [],
                (dto.Investigate ?? []).Select(l => new InsightLead(l.Title, l.Why)).ToList(),
                dto.Charts ?? [],
                dto.Model ?? string.Empty,
                dto.PromptVersion ?? string.Empty);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException)
        {
            logger.LogWarning(e, "Admin insight unavailable; falling back to rules");
            return null;
        }
    }

    private sealed record LeadDto(string Title, string Why);

    private sealed record Dto(
        string Summary,
        List<string>? Interpretation,
        List<string>? Hypotheses,
        List<LeadDto>? Investigate,
        List<string>? Charts,
        string? Model,
        string? PromptVersion);
}
