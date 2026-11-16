using System.Globalization;
using System.Threading.RateLimiting;

namespace WordOs.Api.Endpoints;

/// <summary>
/// Budgets keyed by something only the handler knows — the email a sign-in is
/// for, the learner whose meaning is about to be put to the model (ADR-137).
/// </summary>
/// <remarks>
/// <para>The endpoint policies in <c>Program.cs</c> partition before the body is
/// read, so they can key on an address or an authenticated id and nothing else.
/// Two rules needed more than that:</para>
/// <list type="bullet">
/// <item><b>Sign-in per account.</b> The per-address budget used to be the only
/// one, at 10 per 15 minutes — which a classroom behind one school address
/// spends in its first minute. The address budget is now wide enough for a
/// class, and guessing one account's password is held to the old pace here
/// instead, wherever the guesses come from.</item>
/// <item><b>Meaning checks per learner.</b> Adding a word with a meaning the
/// learner wrote, or editing a meaning, asks the model; those routes are not
/// under the Expensive policy because the ordinary dictionary add on the same
/// route costs nothing. The budget is spent only on the branch that calls the
/// model.</item>
/// </list>
/// <para>Idle partitions are disposed by the limiter itself, so keys an
/// attacker invents do not accumulate; and every key here is reached only
/// after the address budget has let the request through.</para>
/// </remarks>
public sealed class AttemptLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _accounts;
    private readonly PartitionedRateLimiter<string> _meaningChecks;

    public AttemptLimiter(RateLimitOptions options)
    {
        _accounts = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, options.AccountAttemptPermits),
                Window = TimeSpan.FromMinutes(Math.Max(1, options.AccountAttemptWindowMinutes)),
            }));

        _meaningChecks = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, options.MeaningCheckPermitsPerMinute),
                Window = TimeSpan.FromMinutes(1),
            }));
    }

    /// <summary>One attempt against an account, under <paramref name="purpose"/>.</summary>
    public IResult? CheckAccount(string purpose, string email, HttpResponse response) =>
        Rejection(_accounts.AttemptAcquire($"{purpose}:{email.Trim().ToLowerInvariant()}"), response);

    /// <summary>One model call on this learner's meaning-check budget.</summary>
    public IResult? CheckMeaning(Guid userId, HttpResponse response) =>
        Rejection(_meaningChecks.AttemptAcquire(userId.ToString("N")), response);

    private static IResult? Rejection(RateLimitLease lease, HttpResponse response)
    {
        using (lease)
        {
            if (lease.IsAcquired) return null;

            if (lease.TryGetMetadata(MetadataName.RetryAfter, out var wait))
                response.Headers.RetryAfter = Math.Ceiling(wait.TotalSeconds).ToString(CultureInfo.InvariantCulture);

            return Results.Json(
                new { error = new { code = "RATE_LIMITED", message = "Too many attempts. Please wait and try again." } },
                statusCode: StatusCodes.Status429TooManyRequests);
        }
    }

    public void Dispose()
    {
        _accounts.Dispose();
        _meaningChecks.Dispose();
    }
}
