using Microsoft.AspNetCore.Http.Features;
using WordOs.Application.Abstractions;

namespace WordOs.Api.Endpoints;

/// <summary>
/// The learner's recorded Speaking turn, turned into text (ADR-107).
/// </summary>
/// <remarks>
/// The body is the raw recording, typed by its <c>Content-Type</c> — no
/// multipart, so nothing to parse but bytes. What comes back is a draft: the
/// app shows it to the learner to read and correct, and only what they send
/// afterwards through <c>/sessions/{id}/speaking/turn</c> counts (ADR-069).
/// That is why this endpoint needs no session and changes nothing.
/// </remarks>
public static class SpeechEndpoints
{
    public sealed record TranscriptionResponse(string Text);

    public sealed record SynthesisRequest(string? Text);

    // What both engines accept. Refused here, so a wrong type is a clear 415
    // rather than a provider's error two hops away.
    private static readonly HashSet<string> AudioTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/mp4", "audio/m4a", "audio/x-m4a", "audio/aac", "audio/wav",
        "audio/x-wav", "audio/ogg", "audio/webm", "audio/mpeg", "audio/flac",
    };

    public static IEndpointRouteBuilder MapSpeechEndpoints(this IEndpointRouteBuilder app)
    {
        // Rate-limited with everything else that costs an AI call.
        app.MapPost("/api/speech/transcribe", TranscribeAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Expensive)
            .WithTags("Speech");

        // The voice (ADR-108, ADR-110). Its own budget: a Listening session
        // asks for all its audio ahead, and that must not spend the tutor's.
        app.MapPost("/api/speech/synthesize", SynthesizeAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Voice)
            .WithTags("Speech");

        return app;
    }

    private static async Task<IResult> TranscribeAsync(
        HttpContext context,
        ISpeechTranscriber transcriber,
        CapacityOptions capacity,
        CancellationToken ct)
    {
        var limit = capacity.MaxAudioBytes;

        // The global body limit is sized for a transcript. A recording is
        // larger, and only this endpoint is allowed the difference.
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = limit;

        var mimeType = context.Request.ContentType?.Split(';')[0].Trim().ToLowerInvariant() ?? "";
        if (!AudioTypes.Contains(mimeType))
            return Results.Json(
                new { error = new { code = "UNSUPPORTED_AUDIO", message = "That recording format is not supported." } },
                statusCode: StatusCodes.Status415UnsupportedMediaType);

        if (context.Request.ContentLength > limit)
            return TooLarge();

        byte[] audio;
        try
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, ct);
            audio = buffer.ToArray();
        }
        catch (BadHttpRequestException ex)
            when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge();
        }

        if (audio.Length == 0)
            return Problems.BadRequest("EMPTY_AUDIO", "Nothing was recorded.");
        if (audio.Length > limit)
            return TooLarge();

        try
        {
            var text = await transcriber.TranscribeAsync(audio, mimeType, ct);
            return Results.Ok(new TranscriptionResponse(text));
        }
        catch (SpeechUnavailableException)
        {
            // The learner can still type — the app offers it on this code.
            return Problems.Unavailable(
                "SPEECH_UNAVAILABLE",
                "We couldn't listen just now. Try again, or type your answer.");
        }
    }

    private static async Task<IResult> SynthesizeAsync(
        SynthesisRequest request,
        ISpeechSynthesizer synthesizer,
        CapacityOptions capacity,
        CancellationToken ct)
    {
        // Sent on exactly as received: the word positions that come back are
        // offsets into this string, and the app looks them up in its own copy.
        var text = request.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
            return Problems.BadRequest("EMPTY_TEXT", "There is nothing to say.");
        if (text.Length > capacity.MaxSpeechChars)
            return Results.Json(
                new { error = new { code = "TEXT_TOO_LONG", message = "That text is too long to speak at once." } },
                statusCode: StatusCodes.Status413PayloadTooLarge);

        try
        {
            var speech = await synthesizer.SynthesizeAsync(text, ct);
            return Results.Ok(new
            {
                audio = speech.Audio,
                mimeType = speech.MimeType,
                durationMs = speech.DurationMs,
                words = speech.Words.Select(w => new
                {
                    start = w.StartMs,
                    end = w.EndMs,
                    charStart = w.CharStart,
                    charEnd = w.CharEnd,
                }),
                timing = speech.Timing,
            });
        }
        catch (SpeechUnavailableException)
        {
            // The app speaks with the phone's voice on this code.
            return Problems.Unavailable(
                "VOICE_UNAVAILABLE",
                "The voice is unavailable right now.");
        }
    }

    private static IResult TooLarge() =>
        Results.Json(
            new { error = new { code = "AUDIO_TOO_LARGE", message = "That recording is too long." } },
            statusCode: StatusCodes.Status413PayloadTooLarge);
}
