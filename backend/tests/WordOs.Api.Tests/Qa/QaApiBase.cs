using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Lexicon;

namespace WordOs.Api.Tests.Qa;

/// <summary>
/// Shared plumbing for QA campaign 20261111-0121-d365f09.
/// </summary>
/// <remarks>
/// Every class gets its own <see cref="ApiFactory"/> over the throwaway
/// PostgreSQL database the fixture migrates. <see cref="Settings"/> lets a class
/// override configuration (used by the rate-limit tests to run the real limiter
/// with small, isolated budgets).
/// </remarks>
public abstract class QaApiBase(PostgresFixture db) : IAsyncLifetime
{
    protected readonly PostgresFixture Db = db;
    protected Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>? Factory;
    protected ApiFactory? Root;

    /// <summary>Extra host settings layered over <see cref="ApiFactory"/>'s.</summary>
    protected virtual IReadOnlyDictionary<string, string> Settings { get; } =
        new Dictionary<string, string>();

    public virtual Task InitializeAsync()
    {
        if (Db.IsAvailable)
        {
            Root = new ApiFactory(Db.ConnectionString);
            Factory = Settings.Count == 0
                ? Root
                : Root.WithWebHostBuilder(b =>
                {
                    foreach (var (k, v) in Settings) b.UseSetting(k, v);
                });
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null && !ReferenceEquals(Factory, Root))
            await Factory.DisposeAsync();
        if (Root is not null) await Root.DisposeAsync();
    }

    protected FakeClock Clock => Root!.Clock;
    protected StubAiContentService Ai => Root!.Ai;

    protected HttpClient NewClient() => Factory!.CreateClient();

    protected sealed record Learner(HttpClient Client, Guid Id, string Email,
        string Token, string RefreshToken);

    protected async Task<Learner> RegisterAsync(string? email = null, HttpClient? client = null)
    {
        client ??= NewClient();
        email ??= $"qa-{Guid.NewGuid():N}@test.dev";
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = "correct-horse-battery",
            displayName = "QA Learner",
            phoneCountryCode = "967",
            phoneNumber = "770000001",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new Learner(client, body.GetProperty("user").GetProperty("id").GetGuid(),
            email, token, body.GetProperty("refreshToken").GetString()!);
    }

    /// <summary>Registers, promotes in SQL (the only way, ADR-061), signs in again.</summary>
    protected async Task<Learner> RegisterWithRoleAsync(string role)
    {
        var learner = await RegisterAsync();
        await using (var context = Db.CreateContext())
        {
            await context.Database.ExecuteSqlAsync(
                $"""UPDATE users SET "Role" = {role} WHERE "Id" = {learner.Id}""");
        }
        var login = await learner.Client.PostAsJsonAsync("/api/auth/login",
            new { email = learner.Email, password = "correct-horse-battery" });
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;
        learner.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return learner with { Token = token, RefreshToken = body.GetProperty("refreshToken").GetString()! };
    }

    protected async Task<string> SeedSenseAsync(string text, string meaning,
        CefrLevel level = CefrLevel.B1)
    {
        var senseId = $"qa-{Guid.NewGuid():N}";
        await using var context = Db.CreateContext();
        context.LexiconEntries.Add(LexiconEntry.Create(
            senseId, text, text, "n", $"the meaning of {text}", meaning,
            level, 1, "en=oewn;ar=awn", DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();
        return senseId;
    }

    protected async Task<Guid> AddWordAsync(Learner learner, string text, string meaning)
    {
        var senseId = await SeedSenseAsync(text, meaning);
        var response = await learner.Client.PostAsJsonAsync("/api/words", new { senseId });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    protected static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>Fires <paramref name="count"/> requests released together.</summary>
    protected static async Task<HttpResponseMessage[]> BurstAsync(
        int count, Func<int, Task<HttpResponseMessage>> send)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            await gate.Task;
            return await send(i);
        }).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    protected static string Histogram(IEnumerable<HttpResponseMessage> responses) =>
        string.Join(",", responses.GroupBy(r => (int)r.StatusCode).OrderBy(g => g.Key)
            .Select(g => $"{g.Key}x{g.Count()}"));
}
