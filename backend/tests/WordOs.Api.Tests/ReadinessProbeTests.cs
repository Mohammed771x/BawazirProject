using WordOs.Api.Endpoints;

namespace WordOs.Api.Tests;

/// <summary>
/// What the readiness probe is allowed to cost (ADR-077).
/// </summary>
/// <remarks>
/// These look like tests about a health endpoint. They are tests about a
/// hosting bill: every real check opens a database connection, and on a
/// serverless PostgreSQL a connection is a refusal to let the database sleep.
/// Two probes at five-minute intervals spent 80 of a 100 compute-hour month in
/// thirteen days with no learner traffic at all, so "how often does this run"
/// is a correctness property here, not a micro-optimisation.
/// </remarks>
public sealed class ReadinessProbeTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_burst_of_probes_touches_the_database_once()
    {
        var clock = new FakeClock(Start);
        var probe = new ReadinessProbe(
            new CapacityOptions { ReadinessDatabaseCheckSeconds = 3600 }, clock);

        var checks = 0;
        Task<bool> Check(CancellationToken _)
        {
            Interlocked.Increment(ref checks);
            return Task.FromResult(true);
        }

        // An hour of an uptime monitor at five-minute intervals, plus the
        // host's own health check on the same path — the exact arrangement
        // that caused this.
        for (var i = 0; i < 24; i++)
        {
            var result = await probe.CheckAsync(Check, CancellationToken.None);
            Assert.True(result.DatabaseReachable);
            clock.Advance(TimeSpan.FromMinutes(2.5));
        }

        Assert.Equal(1, checks);
    }

    [Fact]
    public async Task The_database_is_asked_again_once_the_window_has_passed()
    {
        var clock = new FakeClock(Start);
        var probe = new ReadinessProbe(
            new CapacityOptions { ReadinessDatabaseCheckSeconds = 3600 }, clock);

        var checks = 0;
        Task<bool> Check(CancellationToken _)
        {
            checks++;
            return Task.FromResult(true);
        }

        await probe.CheckAsync(Check, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(3601));
        await probe.CheckAsync(Check, CancellationToken.None);

        // Throttled, not disabled: the claim "the database is reachable" is
        // still something this service verifies.
        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task A_cached_answer_says_how_old_it_is()
    {
        var clock = new FakeClock(Start);
        var probe = new ReadinessProbe(
            new CapacityOptions { ReadinessDatabaseCheckSeconds = 3600 }, clock);

        await probe.CheckAsync(_ => Task.FromResult(true), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(20));

        var result = await probe.CheckAsync(
            _ => throw new InvalidOperationException("must not be asked"),
            CancellationToken.None);

        // Staleness is disclosed rather than hidden — the whole trade is that
        // this answer may be up to a window old, so whoever reads it is told.
        Assert.True(result.DatabaseReachable);
        Assert.Equal(1200, result.CheckedSecondsAgo);
    }

    [Fact]
    public async Task A_failure_is_never_cached()
    {
        var clock = new FakeClock(Start);
        var probe = new ReadinessProbe(
            new CapacityOptions { ReadinessDatabaseCheckSeconds = 3600 }, clock);

        var reachable = false;
        Task<bool> Check(CancellationToken _) => Task.FromResult(reachable);

        Assert.False((await probe.CheckAsync(Check, CancellationToken.None)).DatabaseReachable);

        // No time passes, and yet the next probe asks again: a database that is
        // down costs nothing to poll, and coming back must be visible at once.
        reachable = true;
        var second = await probe.CheckAsync(Check, CancellationToken.None);

        Assert.True(second.DatabaseReachable);
        Assert.Equal(0, second.CheckedSecondsAgo);
    }

    [Fact]
    public async Task Zero_restores_asking_every_time()
    {
        var clock = new FakeClock(Start);
        var probe = new ReadinessProbe(
            new CapacityOptions { ReadinessDatabaseCheckSeconds = 0 }, clock);

        var checks = 0;
        Task<bool> Check(CancellationToken _)
        {
            checks++;
            return Task.FromResult(true);
        }

        for (var i = 0; i < 5; i++)
            await probe.CheckAsync(Check, CancellationToken.None);

        // The right setting for a server you own, where uptime is already paid
        // for and a fresh answer costs nothing (rule R3 — it is configuration).
        Assert.Equal(5, checks);
    }
}
