using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WordOs.Application.Abstractions;

namespace WordOs.Infrastructure.Ai;

/// <summary>
/// Asks the AI service to speak a tutor reply or a piece of a Listening
/// passage in Gemini's voice (ADR-108).
/// </summary>
/// <remarks>
/// Behind the same <see cref="AiCallGate"/> as every other provider call. The
/// audio is passed through as the AI service encoded it; this layer neither
/// decodes nor keeps it.
/// </remarks>
public sealed class HttpSpeechSynthesizer(
    HttpClient http,
    IOptions<AiServiceOptions> options,
    AiCallGate gate,
    ILogger<HttpSpeechSynthesizer> logger) : ISpeechSynthesizer
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public Task<SynthesizedSpeech> SynthesizeAsync(string text, CancellationToken ct = default) =>
        gate.RunAsync(() => SendAsync(text, ct), ct);

    private async Task<SynthesizedSpeech> SendAsync(string text, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ai/synthesize")
        {
            Content = JsonContent.Create(new { text }),
        };

        var token = options.Value.Token;
        if (!string.IsNullOrEmpty(token))
            request.Headers.Add("X-Service-Token", token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (
            (ex is HttpRequestException or TaskCanceledException)
            && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Voice: AI service unreachable");
            throw new SpeechUnavailableException("AI service unreachable");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning(
                    "Voice: AI service returned {Status}: {Body}",
                    (int)response.StatusCode,
                    body.Length <= 300 ? body : body[..300]);

                if (response.StatusCode == HttpStatusCode.ServiceUnavailable
                    && body.Contains("AI_BUSY", StringComparison.Ordinal))
                    throw new AiCapacityException();

                throw new SpeechUnavailableException(
                    $"AI service returned {(int)response.StatusCode}");
            }

            var dto = await response.Content.ReadFromJsonAsync<SynthesisDto>(Json, ct);
            if (dto?.Audio is not { Length: > 0 } audio || dto.Words is null)
                throw new SpeechUnavailableException("AI service returned no audio");

            return new SynthesizedSpeech(
                audio,
                dto.MimeType ?? "audio/mpeg",
                dto.DurationMs,
                dto.Words
                    .Select(w => new SpokenWord(w.StartMs, w.EndMs, w.CharStart, w.CharEnd))
                    .ToList(),
                dto.Timing ?? "estimated");
        }
    }

    private sealed record SynthesisDto(
        string? Audio,
        string? MimeType,
        int DurationMs,
        List<WordDto>? Words,
        string? Timing,
        string? Model);

    private sealed record WordDto(int StartMs, int EndMs, int CharStart, int CharEnd);
}
