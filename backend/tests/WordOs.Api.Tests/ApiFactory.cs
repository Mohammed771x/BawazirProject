using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WordOs.Application.Abstractions;
using WordOs.Infrastructure.Ai;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Tests;

/// <summary>
/// Boots the real API against the throwaway test database.
/// </summary>
/// <remarks>
/// The whole middleware pipeline runs — authentication, authorization, rate
/// limiting — because that pipeline <i>is</i> the security boundary. Testing
/// handlers in isolation would prove nothing about whether an unauthenticated
/// request actually gets refused.
/// </remarks>
public sealed class ApiFactory(string connectionString)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // UseSetting, not ConfigureAppConfiguration: Program.cs reads the
        // connection string while building the host, which happens before
        // ConfigureAppConfiguration callbacks are applied. Host settings are
        // in place earlier.
        builder.UseSetting("ConnectionStrings:WordOs", connectionString);
        // A test-only key, generated per run and meaningless outside this
        // process. Real keys never live in source.
        builder.UseSetting("Jwt:SigningKey", TestSigningKey);
        builder.UseSetting("Jwt:Issuer", "wordos-test");
        builder.UseSetting("Jwt:Audience", "wordos-test");

        // The authentication budget is 10 requests per 15 minutes in
        // production, per IP — and every request in this suite comes from the
        // same one. A single password-reset test spends eight of them on
        // purpose (register, request a code, then exhaust the attempt cap), so
        // the limiter would fail the test before the rule under test could.
        //
        // Raised, never removed: the limiter still runs in the pipeline, and
        // that it actually refuses is pinned by the AI-budget test, which
        // spends a policy this setting does not touch.
        builder.UseSetting("RateLimits:AuthenticationPermits", "1000");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<WordOsDbContext>>();
            services.AddDbContext<WordOsDbContext>(options =>
                options.UseNpgsql(connectionString));

            // Argon2id is deliberately slow — correct in production, but it
            // would make this suite crawl. The algorithm itself is covered by
            // its own unit tests.
            services.RemoveAll<IPasswordHasher>();
            services.AddSingleton<IPasswordHasher, FastTestPasswordHasher>();

            // Two substitutions that make session behaviour testable at all:
            //
            //  • the clock, because the pipeline is built on two-day gaps and
            //    a suite cannot wait for them;
            //  • the AI service, because a test asserting the requeue rule must
            //    not depend on what a language model produced this morning.
            //
            // The real Gemini path is covered separately, end to end, against a
            // running AI service.
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            // Only the outermost layer is swapped: the stub is still wrapped in
            // the real resilience decorator, so a test that makes the stub fail
            // exercises the production fallback rather than a test-only one.
            // The reset code exists only in the email, by design, so a test
            // can reach it in exactly one place: here.
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Email);

            services.RemoveAll<IAiContentService>();
            services.AddSingleton(Ai);
            services.AddScoped<IAiContentService>(provider =>
                new ResilientAiContentService(
                    Ai,
                    provider.GetRequiredService<
                        ILogger<ResilientAiContentService>>()));
        });
    }

    /// <summary>
    /// Advanced by tests to cross the spaced gaps.
    /// </summary>
    /// <remarks>
    /// Anchored to the real clock rather than a fixed date: JWTs are stamped
    /// from this provider but validated by the JWT middleware against the
    /// system clock, so a backdated start issues tokens that are already
    /// expired. Advancing forward is safe — validation never rewinds.
    /// </remarks>
    public FakeClock Clock { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>The deterministic stand-in for Gemini.</summary>
    public StubAiContentService Ai { get; } = new();

    /// <summary>Every email the API tried to send, in order.</summary>
    public CapturingEmailSender Email { get; } = new();

    public static readonly string TestSigningKey =
        Convert.ToBase64String(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
}

/// <summary>
/// A fast stand-in for Argon2id. Salted SHA-256 — adequate to prove the
/// <i>plumbing</i> (a wrong password is refused, the hash is not the password),
/// and never used outside tests.
/// </summary>
public sealed class FastTestPasswordHasher : IPasswordHasher
{
    public string Hash(string password) =>
        "test$" + Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("salt" + password)));

    public bool Verify(string password, string hash) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(Hash(password)),
            System.Text.Encoding.UTF8.GetBytes(hash));
}

/// <summary>
/// Holds sent messages instead of sending them.
/// </summary>
/// <remarks>
/// <see cref="Fails"/> stands in for a provider outage — the case the forgot
/// endpoint must answer identically to every other, or the difference becomes
/// the account-enumeration oracle the whole design avoids (ADR-078).
/// </remarks>
public sealed class CapturingEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = [];

    public IReadOnlyList<EmailMessage> Sent
    {
        get { lock (_sent) return _sent.ToList(); }
    }

    public EmailMessage? Last => Sent.LastOrDefault();

    /// <summary>When true, every send is refused, as a down provider would.</summary>
    public bool Fails { get; set; }

    public void Clear() { lock (_sent) _sent.Clear(); }

    /// <summary>
    /// The six digits out of the last message, found the way a learner does —
    /// by reading it.
    /// </summary>
    public string? LastCode
    {
        get
        {
            var body = Last?.TextBody;
            if (body is null) return null;
            var match = System.Text.RegularExpressions.Regex.Match(
                body, @"\b\d{6}\b");
            return match.Success ? match.Value : null;
        }
    }

    public Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (Fails) return Task.FromResult(false);
        lock (_sent) _sent.Add(message);
        return Task.FromResult(true);
    }
}

internal static class ServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services
            .Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var descriptor in descriptors) services.Remove(descriptor);
    }
}
