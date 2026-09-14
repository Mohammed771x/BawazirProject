namespace WordOs.Api.Endpoints;

/// <summary>
/// What one instance of this service is allowed to consume (ADR-051).
/// </summary>
/// <remarks>
/// Rate limits say what one <i>learner</i> may ask for; these say what the
/// process itself will take on. The difference matters: a thousand learners
/// each behaving perfectly can still exhaust a database, a CPU or a memory
/// budget, and the failure then lands on everybody at once.
///
/// Every value is configuration rather than a constant, because the right
/// number depends on the machine and on how many instances share the database
/// (rule R3). The defaults below are sized for a small production instance and
/// a stock PostgreSQL — <c>max_connections = 100</c>.
/// </remarks>
public sealed class CapacityOptions
{
    public const string SectionName = "Capacity";

    /// <summary>
    /// Database connections this instance may hold.
    /// </summary>
    /// <remarks>
    /// Deliberately far below PostgreSQL's limit: several instances plus
    /// migrations plus a human with psql all draw on the same allowance, and
    /// the one that arrives last gets an error, not a queue. Forty leaves room
    /// for two instances and a person.
    /// </remarks>
    public int DatabaseConnections { get; init; } = 40;

    /// <summary>How long a request waits for a free connection.</summary>
    public int DatabaseConnectionWaitSeconds { get; init; } = 10;

    /// <summary>How long any single statement may run.</summary>
    public int DatabaseCommandTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// How long a successful readiness check stands before the database is
    /// asked again.
    /// </summary>
    /// <remarks>
    /// This is a hosting bill, not a tuning knob for its own sake. A serverless
    /// PostgreSQL — Neon, and every provider like it — charges for the time the
    /// compute is <i>awake</i>, not for the queries run, and suspends itself
    /// after a few minutes idle. <c>/health/ready</c> opens a real connection,
    /// so anything probing it on a shorter interval than that suspend window
    /// keeps the database awake around the clock and bills a fully idle
    /// service at the full-time rate.
    ///
    /// Measured on the deployed instance: two independent probes — the host's
    /// own health check and an uptime monitor, both pointed here every five
    /// minutes against a five-minute suspend — held the database awake for
    /// 24 hours a day and spent 80 of the month's 100 compute-hours in
    /// thirteen days, with no learner traffic involved at all.
    ///
    /// An hour is the number that makes the probe cost a rounding error: one
    /// wake per hour against a five-minute suspend is roughly 8% of the month
    /// rather than 100% of it. What it gives up is honest and small — the
    /// answer may be up to an hour old, so the response says how old.
    ///
    /// Only *successes* are held. A failure is re-checked on the next request,
    /// because a database that is down is not accruing compute time and
    /// recovery should be visible immediately.
    ///
    /// Zero restores the old behaviour: ask the database every single time.
    /// Correct for a server you own, where uptime costs nothing.
    /// </remarks>
    public int ReadinessDatabaseCheckSeconds { get; init; } = 3600;

    /// <summary>Transient-failure retries — a blip, a failover, a restart.</summary>
    public int DatabaseRetries { get; init; } = 3;

    public int DatabaseRetryDelaySeconds { get; init; } = 5;

    /// <summary>
    /// Password hashes that may run at the same time.
    /// </summary>
    /// <remarks>
    /// Argon2id is memory-hard on purpose: each verification takes 19 MiB and a
    /// core for its duration. That is what makes a stolen database expensive to
    /// crack — and what makes an unbounded login queue a way to exhaust the
    /// server's memory. Measured before this cap: 50 concurrent logins already
    /// cost 1 GiB and pushed the 95th percentile past a second.
    ///
    /// Sign-in is not a hot path — a learner does it once a day — so queueing
    /// briefly is the right trade against falling over.
    ///
    /// Zero means "one per core", which is what this work actually wants: it is
    /// CPU-bound, so more threads than cores buys nothing and each one costs
    /// another 19 MiB. A fixed number is worse in both directions — measured at
    /// a flat 8 on a 10-core machine, sign-in throughput dropped from 100 to 54
    /// per second for no safety gained.
    /// </remarks>
    public int ConcurrentPasswordHashes { get; init; }

    /// <summary>The cap actually applied: the configured value, or one per core.</summary>
    public int EffectivePasswordHashes =>
        ConcurrentPasswordHashes > 0
            ? ConcurrentPasswordHashes
            : Math.Max(4, Environment.ProcessorCount);

    /// <summary>How long a sign-in waits its turn before giving up.</summary>
    public int PasswordHashWaitSeconds { get; init; } = 20;

    /// <summary>
    /// Requests in flight against the AI service at once.
    /// </summary>
    /// <remarks>
    /// A Gemini call takes seconds, so this is where a crowd piles up first.
    /// Without a ceiling, every waiting request holds a database connection and
    /// a slice of memory while it waits, and a slow model becomes an outage in
    /// parts of the app that never call it.
    ///
    /// Past the ceiling a learner is told to try again in a moment, which is a
    /// far better answer than a request that hangs for ninety seconds and then
    /// fails anyway.
    /// </remarks>
    public int ConcurrentAiCalls { get; init; } = 24;

    /// <summary>How long a request waits for an AI slot before backing off.</summary>
    public int AiCallWaitSeconds { get; init; } = 5;

    /// <summary>
    /// Concurrent connections Kestrel will hold open. Zero leaves it unbounded.
    /// </summary>
    public int MaxConcurrentConnections { get; init; } = 2000;

    /// <summary>Largest request body accepted, in bytes. A transcript is small.</summary>
    public long MaxRequestBodyBytes { get; init; } = 256 * 1024;
}
