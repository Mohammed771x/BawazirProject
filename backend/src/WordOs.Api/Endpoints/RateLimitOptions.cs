namespace WordOs.Api.Endpoints;

/// <summary>
/// The rate-limit budgets, as configuration rather than constants (rule R3).
/// </summary>
/// <remarks>
/// The defaults here are the <b>production</b> values, deliberately: an
/// environment that wants a looser budget has to opt in and say so, so nothing
/// is weakened by an omitted setting. `appsettings.Development.json` raises the
/// authentication budget because a local run registers throwaway learners by
/// the dozen — that is the only place it is relaxed, and it never ships.
///
/// Every limit stays enforced in every environment; only the numbers move.
/// </remarks>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    /// <summary>
    /// Registration and login attempts, partitioned by IP — the
    /// credential-stuffing surface.
    /// </summary>
    public int AuthenticationPermits { get; init; } = 10;

    public int AuthenticationWindowMinutes { get; init; } = 15;

    /// <summary>Word lookup fires on every keystroke.</summary>
    public int LookupPermitsPerMinute { get; init; } = 120;

    /// <summary>Anything that spends Gemini tokens.</summary>
    public int ExpensivePermitsPerMinute { get; init; } = 30;

    /// <summary>The backstop across every endpoint, per user.</summary>
    public int GlobalPermitsPerMinute { get; init; } = 300;
}
