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

internal static class ServiceCollectionExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        var descriptors = services
            .Where(d => d.ServiceType == typeof(T)).ToList();
        foreach (var descriptor in descriptors) services.Remove(descriptor);
    }
}
