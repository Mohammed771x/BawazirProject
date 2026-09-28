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

    /// <summary>
    /// The request header the hosting proxy writes the caller's real address
    /// into. Empty means "trust the socket".
    /// </summary>
    /// <remarks>
    /// On Render every request arrives from Render's own proxy, so the socket
    /// address is the same for every learner in the world. Partitioned by it,
    /// "10 sign-ins per 15 minutes per IP" was 10 per 15 minutes for the whole
    /// user base — and `/auth/refresh` spends that budget each time the app is
    /// opened after the 15-minute access token lapsed (ADR-106).
    ///
    /// Only a header the proxy <b>overwrites</b> may be named here. One the
    /// caller can set freely would hand an attacker a fresh budget per request.
    /// Which one a platform overwrites is a fact about the platform, which is
    /// why it is configuration and not code (rule R3).
    /// </remarks>
    public string ClientAddressHeader { get; init; } = "True-Client-IP";
}
