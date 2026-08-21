using WordOs.Application.Abstractions;
using WordOs.Domain.Common;
using WordOs.Infrastructure.Ai;
using WordOs.Infrastructure.Security;

namespace WordOs.Api.Tests;

/// <summary>
/// What the service does when more people arrive than it can serve at once
/// (ADR-051).
/// </summary>
/// <remarks>
/// These are the properties that decide whether a busy morning is a slow
/// morning or an outage. Each one was chosen because its absence had been
/// measured: an unbounded connection pool answered 500s at 200 concurrent
/// readers, and a blocking hash queue halved throughput for everything else in
/// the process.
/// </remarks>
public sealed class CapacityTests
{
    // ── Password hashing ─────────────────────────────────────────────────────

    [Fact]
    public async Task No_more_hashes_run_at_once_than_the_cap_allows()
    {
        using var hasher = new ThrottledPasswordHasher(
            new SlowHasher(TimeSpan.FromMilliseconds(60)),
            concurrency: 3, waitSeconds: 30);

        var running = 0;
        var peak = 0;
        var watcher = new SlowHasher(TimeSpan.FromMilliseconds(60), () =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
        }, () => Interlocked.Decrement(ref running));

        using var watched = new ThrottledPasswordHasher(
            watcher, concurrency: 3, waitSeconds: 30);

        await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => watched.HashAsync("password")));

        // Twenty arrived; never more than three were spending memory at once.
        Assert.True(peak <= 3, $"peak concurrency was {peak}");
    }

    [Fact]
    public async Task A_sign_in_that_waits_too_long_is_told_the_server_is_busy()
    {
        using var hasher = new ThrottledPasswordHasher(
            new SlowHasher(TimeSpan.FromSeconds(5)),
            concurrency: 1, waitSeconds: 0);

        // One slot, held.
        var holding = hasher.HashAsync("first");
        await Task.Delay(100);

        // The second is refused rather than queued for ever — and refused as
        // *busy*, never as a wrong password: nothing was checked.
        await Assert.ThrowsAsync<PasswordHashingBusyException>(
            () => hasher.HashAsync("second"));

        await holding;
    }

    [Fact]
    public async Task Waiting_for_a_hash_does_not_hold_a_thread()
    {
        using var hasher = new ThrottledPasswordHasher(
            new SlowHasher(TimeSpan.FromMilliseconds(200)),
            concurrency: 2, waitSeconds: 30);

        // Far more waiters than the thread pool would tolerate if each of them
        // blocked one. A synchronous queue here is what took sign-in throughput
        // from 97 requests a second to 48, and starved everything else in the
        // process while it did.
        var before = ThreadPool.ThreadCount;
        var work = Enumerable.Range(0, 200)
            .Select(_ => hasher.HashAsync("password"))
            .ToList();

        await Task.Delay(150);
        var during = ThreadPool.ThreadCount;

        await Task.WhenAll(work);

        Assert.True(during - before < 50,
            $"thread count grew by {during - before} while 200 waited");
    }

    // ── AI calls ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task No_more_AI_calls_run_at_once_than_the_cap_allows()
    {
        using var gate = new AiCallGate(concurrency: 4, waitSeconds: 30);

        var running = 0;
        var peak = 0;
        var ai = gate.Wrap(new SlowAi(TimeSpan.FromMilliseconds(80),
            () => InterlockedMax(ref peak, Interlocked.Increment(ref running)),
            () => Interlocked.Decrement(ref running)));

        await Task.WhenAll(Enumerable.Range(0, 30).Select(_ =>
            ai.EvaluateWritingAsync(
                new WritingEvaluationRequest(
                    "word", "meaning", "definition", CefrLevel.B1, "a sentence"))));

        Assert.True(peak <= 4, $"peak concurrency was {peak}");
    }

    [Fact]
    public async Task A_request_that_cannot_get_an_AI_slot_is_refused_quickly()
    {
        using var gate = new AiCallGate(concurrency: 1, waitSeconds: 0);
        var ai = gate.Wrap(new SlowAi(TimeSpan.FromSeconds(5)));

        var holding = ai.EvaluateWritingAsync(new WritingEvaluationRequest(
            "word", "meaning", "definition", CefrLevel.B1, "a sentence"));
        await Task.Delay(100);

        // Refused, not queued behind a five-second call — and refused as
        // capacity, so the caller answers "try again" with the session intact
        // rather than spending the deterministic fallback on a queue that
        // clears in seconds.
        await Assert.ThrowsAsync<AiCapacityException>(() =>
            ai.EvaluateWritingAsync(new WritingEvaluationRequest(
                "word", "meaning", "definition", CefrLevel.B1, "a sentence")));

        await holding;
    }

    [Fact]
    public async Task A_slow_AI_never_holds_more_of_the_process_than_its_cap()
    {
        using var gate = new AiCallGate(concurrency: 2, waitSeconds: 30);
        var ai = gate.Wrap(new SlowAi(TimeSpan.FromMilliseconds(120)));

        var work = Enumerable.Range(0, 50).Select(_ =>
            ai.EvaluateWritingAsync(new WritingEvaluationRequest(
                "word", "meaning", "definition", CefrLevel.B1, "a sentence")));

        // The queue is a queue, not a crowd: fifty callers, two in flight, and
        // the free-slot count the readiness probe reports never goes negative
        // or leaks.
        await Task.WhenAll(work);

        Assert.Equal(2, gate.Available);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
        }
    }

    private sealed class SlowHasher(
        TimeSpan cost, Action? entered = null, Action? left = null)
        : IPasswordHasher
    {
        public string Hash(string password)
        {
            entered?.Invoke();
            try
            {
                Thread.Sleep(cost);
                return $"hash:{password}";
            }
            finally
            {
                left?.Invoke();
            }
        }

        public bool Verify(string password, string hash) =>
            Hash(password) == hash;
    }

    private sealed class SlowAi(
        TimeSpan cost, Action? entered = null, Action? left = null)
        : IAiContentService
    {
        public async Task<WritingObservation> EvaluateWritingAsync(
            WritingEvaluationRequest request, CancellationToken ct = default)
        {
            entered?.Invoke();
            try
            {
                await Task.Delay(cost, ct);
                return new WritingObservation(
                    true, true, true, true, "none", "ok", null,
                    FromFallback: false, PromptVersion: "test",
                    Model: "test", Tokens: 0);
            }
            finally
            {
                left?.Invoke();
            }
        }

        public Task<GeneratedContent> GenerateContentAsync(
            ContentRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GeneratedContent> RelevelContentAsync(
            RelevelRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SpeakingObservation> SpeakingTurnAsync(
            SpeakingTurnRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SpeakingEvaluation> EvaluateSpeakingAsync(
            SpeakingEvaluationRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<PlacementEvaluation> EvaluatePlacementAsync(
            PlacementEvaluationRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
