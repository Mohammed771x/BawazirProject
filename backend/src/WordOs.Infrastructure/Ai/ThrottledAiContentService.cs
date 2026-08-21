using WordOs.Application.Abstractions;

namespace WordOs.Infrastructure.Ai;

/// <summary>
/// Caps how many AI calls this instance has in flight at once (ADR-051).
/// </summary>
/// <remarks>
/// A Gemini call takes seconds, which makes it the one place a crowd
/// accumulates. Every request waiting on one holds a thread, a database
/// connection and its own memory for the whole wait, so an AI service having a
/// slow minute becomes an outage in parts of the app that never call it —
/// signing in, opening the word list, reading yesterday's results.
///
/// The ceiling turns that into something bounded: so many at a time, the rest
/// wait briefly, and a wait that gets too long is refused fast. A learner told
/// "busy, try again in a moment" has lost five seconds. A learner in a queue
/// with no ceiling waits ninety and then fails anyway, having held a piece of
/// the server the whole time.
///
/// This sits *outside* <see cref="ResilientAiContentService"/> on purpose.
/// The fallback exists for an AI that answered badly or not at all; this is
/// about an AI that would eventually answer fine if only there were room. They
/// are different failures and deserve different answers — one degrades the
/// session, the other asks the learner to come back in a moment with their
/// session untouched.
/// </remarks>
public sealed class ThrottledAiContentService(
    IAiContentService inner, AiCallGate gate) : IAiContentService
{
    private readonly IAiContentService _inner = inner;
    private readonly AiCallGate _gate = gate;

    public Task<GeneratedContent> GenerateContentAsync(
        ContentRequest request, CancellationToken ct = default) =>
        RunAsync(() => _inner.GenerateContentAsync(request, ct), ct);

    public Task<GeneratedContent> RelevelContentAsync(
        RelevelRequest request, CancellationToken ct = default) =>
        RunAsync(() => _inner.RelevelContentAsync(request, ct), ct);

    public Task<WritingObservation> EvaluateWritingAsync(
        WritingEvaluationRequest request, CancellationToken ct = default) =>
        RunAsync(() => _inner.EvaluateWritingAsync(request, ct), ct);

    public Task<SpeakingObservation> SpeakingTurnAsync(
        SpeakingTurnRequest request, CancellationToken ct = default) =>
        RunAsync(() => _inner.SpeakingTurnAsync(request, ct), ct);

    public Task<SpeakingEvaluation> EvaluateSpeakingAsync(
        SpeakingEvaluationRequest request, CancellationToken ct = default) =>
        RunAsync(() => _inner.EvaluateSpeakingAsync(request, ct), ct);

    public Task<PlacementEvaluation> EvaluatePlacementAsync(
        PlacementEvaluationRequest request, CancellationToken ct = default) =>
        RunAsync(() => _inner.EvaluatePlacementAsync(request, ct), ct);

    private async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct) =>
        await _gate.RunAsync(work, ct);
}

/// <summary>
/// The ceiling itself, shared by every request in the process.
/// </summary>
/// <remarks>
/// A singleton, deliberately. The service that uses it is scoped — one per
/// request, as it has always been — so the counter cannot live there: a limit
/// created per request limits nothing.
/// </remarks>
public sealed class AiCallGate : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _wait;

    public AiCallGate(int concurrency, int waitSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);

        _slots = new SemaphoreSlim(concurrency, concurrency);
        _wait = TimeSpan.FromSeconds(waitSeconds);
    }

    /// <summary>How many slots are free — for the readiness report.</summary>
    public int Available => _slots.CurrentCount;

    public IAiContentService Wrap(IAiContentService inner) =>
        new ThrottledAiContentService(inner, this);

    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (!await _slots.WaitAsync(_wait, ct))
            throw new AiCapacityException();

        try
        {
            return await work();
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose() => _slots.Dispose();
}

/// <summary>
/// Thrown when a request waited for an AI slot and did not get one in time.
/// </summary>
/// <remarks>
/// Distinct from an AI failure: nothing was asked of the model, nothing was
/// spent, and the learner's session is exactly as it was. The right answer is
/// 503 with a "try again shortly", not the deterministic fallback — serving
/// visibly weaker content because the queue was long would spend the fallback
/// on a problem that fixes itself in seconds.
/// </remarks>
public sealed class AiCapacityException : Exception
{
    public AiCapacityException()
        : base("The AI service is at capacity. Try again in a moment.")
    {
    }
}
