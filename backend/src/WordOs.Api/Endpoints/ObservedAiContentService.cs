using System.Diagnostics;
using WordOs.Application.Abstractions;
using WordOs.Domain.Analytics;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Endpoints;

/// <summary>
/// Times every call to the AI service and records whether it answered (ADR-125).
/// </summary>
/// <remarks>
/// Sits <i>inside</i> the resilient wrapper, against the raw HTTP client, so
/// what it measures is the model and not the fallback: a call that failed and
/// was rescued with canned content is a failure here, which is exactly what the
/// admin area's "AI problem" needs to be able to see.
///
/// Written through its own scope and its own save. The request's unit of work
/// may still fail after the model answered — and a slow model that then saw its
/// session error out is precisely the call worth having on record.
/// Recording never throws into the caller: a lost timing is a lost data point,
/// a thrown one would be a lost lesson.
/// </remarks>
public sealed class ObservedAiContentService(
    IAiContentService inner,
    IHttpContextAccessor http,
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<ObservedAiContentService> log) : IAiContentService
{
    public Task<GeneratedContent> GenerateContentAsync(ContentRequest request, CancellationToken ct = default) =>
        Observe("content", request.Listening ? "Listening" : "Reading", () => inner.GenerateContentAsync(request, ct));

    public Task<WritingObservation> EvaluateWritingAsync(WritingEvaluationRequest request, CancellationToken ct = default) =>
        Observe("writing", "Writing", () => inner.EvaluateWritingAsync(request, ct));

    public Task<MeaningCheck> CheckMeaningAsync(MeaningCheckRequest request, CancellationToken ct = default) =>
        Observe("meaning_check", null, () => inner.CheckMeaningAsync(request, ct));

    public Task<SpeakingObservation> SpeakingTurnAsync(SpeakingTurnRequest request, CancellationToken ct = default) =>
        Observe("speaking_turn", "Speaking", () => inner.SpeakingTurnAsync(request, ct));

    public Task<SpeakingEvaluation> EvaluateSpeakingAsync(SpeakingEvaluationRequest request, CancellationToken ct = default) =>
        Observe("speaking_eval", "Speaking", () => inner.EvaluateSpeakingAsync(request, ct));

    public Task<PlacementEvaluation> EvaluatePlacementAsync(PlacementEvaluationRequest request, CancellationToken ct = default) =>
        Observe("placement", request.Skill.ToString(), () => inner.EvaluatePlacementAsync(request, ct));

    public Task<GeneratedContent> RelevelContentAsync(RelevelRequest request, CancellationToken ct = default) =>
        Observe("relevel", null, () => inner.RelevelContentAsync(request, ct));

    private async Task<T> Observe<T>(string operation, string? skill, Func<Task<T>> call)
    {
        var started = Stopwatch.GetTimestamp();
        string? failure = null;

        try
        {
            return await call();
        }
        catch (Exception e)
        {
            failure = e.GetType().Name;
            throw;
        }
        finally
        {
            await RecordAsync(operation, skill, Stopwatch.GetElapsedTime(started), failure);
        }
    }

    private async Task RecordAsync(string operation, string? skill, TimeSpan elapsed, string? failure)
    {
        var userId = http.HttpContext?.User.UserId();
        if (userId is null) return;

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WordOsDbContext>();

            var now = clock.GetUtcNow();
            db.AnalyticsEvents.Add(AnalyticsEvent.Create(
                userId.Value, AnalyticsEventNames.AiCall, AnalyticsSource.Server, now, now,
                skill: Enum.TryParse<Domain.Common.SkillType>(skill, out var s) ? s : null,
                result: failure is null ? "ok" : "error",
                durationMs: (int)Math.Min(int.MaxValue, elapsed.TotalMilliseconds),
                propsJson: System.Text.Json.JsonSerializer.Serialize(new
                {
                    operation,
                    error = failure,
                })));

            // Not the request's token: a learner who closed the app mid-call
            // still produced a call worth timing.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "Could not record an AI call timing for {Operation}", operation);
        }
    }
}
