using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WordOs.Application.Abstractions;

namespace WordOs.Infrastructure.Ai;

/// <summary>
/// Sends a recorded turn to the AI service, which tries Gemini and then Groq
/// (ADR-107).
/// </summary>
/// <remarks>
/// Behind the same <see cref="AiCallGate"/> as every lesson and evaluation: a
/// provider call is a provider call, and a burst of recordings must not starve
/// the rest of the app of AI capacity — nor the reverse.
///
/// Which engine answered is logged, never returned: the owner needs to know
/// how often Gemini refused, and the learner does not.
///
/// The audio is held for the length of the request and never stored
/// (rule R4 is about state; a recording is not state).
/// </remarks>
public sealed class HttpSpeechTranscriber(
    HttpClient http,
    IOptions<AiServiceOptions> options,
    AiCallGate gate,
    ILogger<HttpSpeechTranscriber> logger) : ISpeechTranscriber
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public Task<string> TranscribeAsync(
        byte[] audio, string mimeType, CancellationToken ct = default) =>
        gate.RunAsync(() => SendAsync(audio, mimeType, ct), ct);

    private async Task<string> SendAsync(
        byte[] audio, string mimeType, CancellationToken ct)
    {
        using var content = new ByteArrayContent(audio);
        content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/ai/transcribe")
        {
            Content = content,
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
            logger.LogWarning(ex, "Speech: AI service unreachable");
            throw new SpeechUnavailableException("AI service unreachable");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                // Logged without the audio, which is the learner's own voice.
                logger.LogWarning(
                    "Speech: AI service returned {Status}: {Body}",
                    (int)response.StatusCode,
                    body.Length <= 300 ? body : body[..300]);

                // Capacity on the far side is the same answer as capacity on
                // this side: try again in a moment (ADR-051).
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable
                    && body.Contains("AI_BUSY", StringComparison.Ordinal))
                    throw new AiCapacityException();

                throw new SpeechUnavailableException(
                    $"AI service returned {(int)response.StatusCode}");
            }

            var result = await response.Content.ReadFromJsonAsync<TranscriptionDto>(Json, ct)
                ?? throw new SpeechUnavailableException("AI service returned no body");

            logger.LogInformation(
                "Speech: transcribed {Bytes} bytes with {Engine}{Fallback}",
                audio.Length,
                result.Engine,
                result.FallbackReason is null ? "" : $" (fallback: {result.FallbackReason})");

            return result.Text?.Trim() ?? string.Empty;
        }
    }

    private sealed record TranscriptionDto(
        string? Text, string? Engine, string? FallbackReason);
}
