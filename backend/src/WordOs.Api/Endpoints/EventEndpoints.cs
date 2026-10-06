using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using WordOs.Domain.Analytics;
using WordOs.Domain.Common;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Endpoints;

/// <summary>
/// What the phone saw and the server could not (ADR-125).
/// </summary>
/// <remarks>
/// Telemetry, not state. Nothing here is read back by a learner-facing
/// endpoint, nothing here moves a word, and a batch that fails to arrive costs
/// a chart a data point and nothing else — which is why the phone sends it
/// fire-and-forget and why rule R1 is untouched: the client reports what the
/// learner did, it does not decide anything about it.
/// </remarks>
public static class EventEndpoints
{
    /// <summary>Most events one request may carry.</summary>
    public const int MaxBatch = 50;

    public sealed record ClientEventDto(
        [property: Required, StringLength(48)] string Name,
        DateTimeOffset? At = null,
        [property: StringLength(64)] string? AppSessionId = null,
        Guid? SessionId = null,
        Guid? WordId = null,
        [property: StringLength(16)] string? Skill = null,
        int? Attempt = null,
        [property: StringLength(16)] string? Result = null,
        int? DurationMs = null,
        [property: StringLength(8)] string? Level = null,
        [property: StringLength(48)] string? Screen = null,
        JsonElement? Props = null);

    public sealed record EventBatchRequest(
        [property: Required] IReadOnlyList<ClientEventDto> Events,
        [property: StringLength(32)] string? AppVersion = null,
        [property: StringLength(16)] string? Platform = null);

    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/events", IngestAsync)
            .RequireAuthorization()
            .WithTags("Events");

        return app;
    }

    private static async Task<IResult> IngestAsync(
        EventBatchRequest request,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        if (request.Events is null || request.Events.Count == 0)
            return Results.Ok(new { accepted = 0, rejected = 0 });

        if (request.Events.Count > MaxBatch)
            return Problems.BadRequest(
                "BATCH_TOO_LARGE", $"Send at most {MaxBatch} events at a time.");

        var now = clock.GetUtcNow();
        var accepted = 0;

        foreach (var e in request.Events)
        {
            // Unknown names, and names only the server may write, are dropped
            // rather than refusing the batch: one stale event from an old build
            // must not cost the other forty-nine.
            if (!AnalyticsEventNames.ClientNames.Contains(e.Name)) continue;

            // A phone's clock can be anything. Within a day of the server it is
            // believed — that is batching and offline use; beyond that it is a
            // wrong clock, and the arrival time is the better guess.
            var at = e.At is { } claimed && (now - claimed).Duration() < TimeSpan.FromDays(1)
                ? claimed.ToUniversalTime()
                : now;

            db.AnalyticsEvents.Add(AnalyticsEvent.Create(
                userId.Value, e.Name, AnalyticsSource.Client, at, now,
                appSessionId: e.AppSessionId,
                sessionId: e.SessionId,
                wordId: e.WordId,
                skill: Enum.TryParse<SkillType>(e.Skill, ignoreCase: true, out var skill)
                       && Enum.IsDefined(skill) ? skill : null,
                attempt: e.Attempt,
                result: e.Result,
                durationMs: e.DurationMs,
                contentLevel: e.Level,
                screen: e.Screen,
                appVersion: request.AppVersion,
                platform: request.Platform,
                propsJson: PropsOf(e.Props)));
            accepted++;
        }

        await db.SaveChangesAsync(ct);

        return Results.Ok(new { accepted, rejected = request.Events.Count - accepted });
    }

    /// <summary>
    /// Keeps a props object only if it is a flat object of scalars.
    /// </summary>
    /// <remarks>
    /// Nested documents and arrays are how free text and whole payloads creep
    /// into a telemetry table; a flat map of flags and numbers is all any event
    /// here needs.
    /// </remarks>
    private static string? PropsOf(JsonElement? props)
    {
        if (props is not { ValueKind: JsonValueKind.Object } obj) return null;

        var flat = new Dictionary<string, object?>();
        foreach (var p in obj.EnumerateObject().Take(16))
        {
            if (p.Name.Length > 32) continue;
            flat[p.Name] = p.Value.ValueKind switch
            {
                JsonValueKind.String => p.Value.GetString() is { } s && s.Length <= 64 ? s : null,
                JsonValueKind.Number => p.Value.TryGetDouble(out var d) ? d : null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
        }

        var json = JsonSerializer.Serialize(flat.Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value));
        return json.Length <= AnalyticsEvent.MaxPropsLength ? json : null;
    }
}

/// <summary>
/// The server's half of the event log: attempts and AI judgements (ADR-125).
/// </summary>
/// <remarks>
/// Added to the same unit of work as the change it describes, so an answer and
/// the record of it are saved together or not at all.
/// </remarks>
public static class ServerEvents
{
    public static void Add(
        WordOsDbContext db,
        Guid userId,
        string name,
        DateTimeOffset now,
        Guid? sessionId = null,
        Guid? wordId = null,
        SkillType? skill = null,
        int? attempt = null,
        bool? passed = null,
        int? durationMs = null,
        CefrLevel? level = null,
        object? props = null)
    {
        db.AnalyticsEvents.Add(AnalyticsEvent.Create(
            userId, name, AnalyticsSource.Server, now, now,
            sessionId: sessionId,
            wordId: wordId,
            skill: skill,
            attempt: attempt,
            result: passed switch { true => "pass", false => "fail", null => null },
            durationMs: durationMs,
            contentLevel: level?.ToWire(),
            propsJson: props is null ? null : JsonSerializer.Serialize(props, Json)));
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
