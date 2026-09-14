namespace WordOs.Api.Endpoints;

/// <summary>
/// The result of asking whether the database is reachable, and how recently
/// that was actually asked.
/// </summary>
/// <param name="DatabaseReachable">Whether the last real check succeeded.</param>
/// <param name="CheckedSecondsAgo">
/// Age of that answer. Zero means this request paid for it.
/// </param>
public readonly record struct ReadinessResult(bool DatabaseReachable, int CheckedSecondsAgo);

/// <summary>
/// Answers <c>/health/ready</c> without touching the database more than once
/// per <see cref="CapacityOptions.ReadinessDatabaseCheckSeconds"/> (ADR-077).
/// </summary>
/// <remarks>
/// The reason this exists is billing, and it is worth stating plainly: on a
/// serverless PostgreSQL the probe that proves the database is alive is also
/// the thing that forbids it to sleep. See the option's own remarks for the
/// measurement.
///
/// Singleton on purpose — a cache with one entry per request is not a cache.
/// </remarks>
public sealed class ReadinessProbe(CapacityOptions capacity, TimeProvider clock)
{
    // Serialises the real check so a burst of probes costs one round trip
    // rather than one each. Contention here is a handful of health requests,
    // never learner traffic.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTimeOffset _lastSuccessAt = DateTimeOffset.MinValue;

    /// <param name="probe">
    /// The real check. Passed in rather than held, because it belongs to a
    /// scoped DbContext and this object outlives every request.
    /// </param>
    public async Task<ReadinessResult> CheckAsync(
        Func<CancellationToken, Task<bool>> probe, CancellationToken ct)
    {
        if (TryCached(out var cached))
            return cached;

        await _gate.WaitAsync(ct);
        try
        {
            // Re-read under the lock: whoever waited here was very likely
            // waiting for the answer that just arrived.
            if (TryCached(out cached))
                return cached;

            var reachable = await probe(ct);
            if (reachable)
                _lastSuccessAt = clock.GetUtcNow();

            // A failure is deliberately not remembered: the next request
            // re-checks, so a database coming back is seen at once.
            return new ReadinessResult(reachable, CheckedSecondsAgo: 0);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryCached(out ReadinessResult result)
    {
        result = default;

        if (capacity.ReadinessDatabaseCheckSeconds <= 0)
            return false;

        var age = clock.GetUtcNow() - _lastSuccessAt;
        if (age >= TimeSpan.FromSeconds(capacity.ReadinessDatabaseCheckSeconds))
            return false;

        result = new ReadinessResult(true, (int)age.TotalSeconds);
        return true;
    }
}
