using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Lexicon;

namespace WordOs.Api.Tests;

/// <summary>
/// Owner analytics, computed from real rows.
/// </summary>
/// <remarks>
/// The figures the dashboard draws have to come from the database, not from a
/// plausible-looking constant — a chart that is quietly always zero is worse
/// than no chart, because it looks like an answer.
///
/// Every test here first *creates* the history it then expects to see counted.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class AnalyticsTests(PostgresFixture db) : IAsyncLifetime
{
    private ApiFactory? _factory;
    private HttpClient? _client;

    public Task InitializeAsync()
    {
        if (db.IsAvailable)
        {
            _factory = new ApiFactory(db.ConnectionString);
            _client = _factory.CreateClient();
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    private HttpClient Client => _client!;

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private async Task<Guid> SignInAsync(string prefix)
    {
        var response = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"{prefix}-{Guid.NewGuid():N}@test.dev",
            password = "correct-horse-battery",
            displayName = "Learner",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", body.GetProperty("token").GetString());

        return body.GetProperty("user").GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Registers an Owner and signs in as them.
    /// </summary>
    /// <remarks>
    /// The promotion is done in SQL because the API offers no route to it —
    /// which is itself a property the security suite tests. The re-login is
    /// what puts the role in the token.
    /// </remarks>
    private async Task SignInAsOwnerAsync(string prefix)
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@test.dev";
        var register = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = "correct-horse-battery",
            displayName = "Owner",
        });
        register.EnsureSuccessStatusCode();

        await using (var context = db.CreateContext())
        {
            await context.Database.ExecuteSqlAsync(
                $"""UPDATE users SET "Role" = 'Owner' WHERE "Email" = {email}""");
        }

        var login = await Client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = "correct-horse-battery",
        });
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();

        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", body.GetProperty("token").GetString());
    }

    private async Task<Guid> AddWordAsync(string text, string meaning)
    {
        var senseId = $"an-{Guid.NewGuid():N}";
        await using (var context = db.CreateContext())
        {
            context.LexiconEntries.Add(LexiconEntry.Create(
                senseId, text, text, "n", $"the meaning of {text}", meaning,
                CefrLevel.B1, 1, "en=oewn;ar=awn", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        var response = await Client.PostAsJsonAsync("/api/words", new { senseId });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();
    }

    /// <summary>Runs a Reading session, passing or failing the word.</summary>
    private async Task RunReadingAsync(bool pass = true)
    {
        var start = await Client.PostAsync("/api/sessions/reading/start", null);
        start.EnsureSuccessStatusCode();
        var session = await start.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();

        var next = session.GetProperty("progress").GetProperty("nextItemId");
        while (next.ValueKind != JsonValueKind.Null)
        {
            var itemId = next.GetGuid();
            string answer;
            await using (var context = db.CreateContext())
            {
                var item = await context.SessionItems.FirstAsync(i => i.Id == itemId);
                answer = pass ? item.CorrectAnswer : "definitely wrong";
            }

            var result = await Client.PostAsJsonAsync(
                $"/api/sessions/{sessionId}/answer", new { itemId, answer });
            result.EnsureSuccessStatusCode();
            next = (await result.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("progress").GetProperty("nextItemId");
        }

        (await Client.PostAsync($"/api/sessions/{sessionId}/complete", null))
            .EnsureSuccessStatusCode();
    }

    // ── Overview ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task The_overview_counts_real_sessions_passes_and_failures()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        // A learner with a real history: one word passed, one failed.
        await SignInAsync("analytics-learner");
        await AddWordAsync("research", "بحث علمي");
        await RunReadingAsync(pass: true);

        // No clock skip: a newly added word is due immediately, and advancing
        // the fake clock would issue the Owner a token the JWT middleware —
        // which uses the system clock — sees as not yet valid.
        await AddWordAsync("theory", "نظرية");
        await RunReadingAsync(pass: false);

        await SignInAsOwnerAsync("analytics-owner");

        var overview = await Client.GetFromJsonAsync<JsonElement>(
            "/api/admin/overview");

        var reading = overview.GetProperty("skillStats").EnumerateArray()
            .First(s => s.GetProperty("skill").GetString() == "READING");

        // Counted from the append-only event log, so a word that failed and was
        // retried is both a failure and, later, a pass.
        Assert.True(reading.GetProperty("sessionsCompleted").GetInt32() >= 2);
        Assert.True(reading.GetProperty("wordsPassed").GetInt32() >= 1);
        Assert.True(reading.GetProperty("wordsFailed").GetInt32() >= 1);
        Assert.True(reading.GetProperty("firstAttemptPasses").GetInt32() >= 1);

        // Session shape — zero here would mean the chart is decorative.
        Assert.True(overview.GetProperty("averageSessionsPerUser").GetDouble() > 0);
        Assert.True(overview.GetProperty("averageWordsPerUserPerDay").GetDouble() > 0);

        // A proportion, not a count. Asserting an exact value here would be
        // wrong: the overview is global, and a sibling test deliberately runs a
        // session on the fallback. That behaviour has its own test below.
        var fallbackRate = overview.GetProperty("aiFallbackRate").GetDouble();
        Assert.InRange(fallbackRate, 0, 1);
    }

    [SkippableFact]
    public async Task The_overview_reports_when_sessions_ran_without_the_ai()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        await SignInAsync("fallback-learner");
        await AddWordAsync("research", "بحث علمي");

        _factory!.Ai.Fail = true;
        try
        {
            await RunReadingAsync(pass: true);
        }
        finally
        {
            _factory!.Ai.Fail = false;
        }

        await SignInAsOwnerAsync("fallback-owner");

        var overview = await Client.GetFromJsonAsync<JsonElement>(
            "/api/admin/overview");

        // A rising fallback rate is what stops a silent quality collapse being
        // read as learners getting worse (`MVP Core.txt` §62).
        Assert.True(overview.GetProperty("aiFallbackRate").GetDouble() > 0,
            "a session that ran on the fallback must be visible in analytics");
    }

    // ── Drill-down ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task The_drill_down_reports_this_learners_own_history()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var learnerId = await SignInAsync("drill-learner");
        await AddWordAsync("research", "بحث علمي");
        await RunReadingAsync(pass: false);

        await SignInAsOwnerAsync("drill-owner");

        var detail = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/admin/users/{learnerId}");

        Assert.Equal(1, detail.GetProperty("wordsAddedToday").GetInt32());
        Assert.Equal(1, detail.GetProperty("wordsLearning").GetInt32());
        Assert.True(detail.GetProperty("signInCount").GetInt32() >= 1);

        // The mistake list is what the Owner drills into — a wrong answer is
        // recorded, never used to remove the word (§48).
        var mistakes = detail.GetProperty("mistakes").EnumerateArray().ToList();
        Assert.Single(mistakes);
        Assert.Equal("research", mistakes[0].GetProperty("text").GetString());
        Assert.Equal("READING", mistakes[0].GetProperty("skill").GetString());
        Assert.True(mistakes[0].GetProperty("attempts").GetInt32() >= 1);

        // Fourteen days, including the empty ones: a series that omits gaps
        // reads as uninterrupted study.
        var daily = detail.GetProperty("daily").EnumerateArray().ToList();
        Assert.Equal(14, daily.Count);
        Assert.Contains(daily, d => d.GetProperty("wordsAdded").GetInt32() == 1);

        // Spelling appears with a diagnostic but never a CEFR band (ADR-008).
        var spelling = detail.GetProperty("levels").EnumerateArray()
            .First(l => l.GetProperty("skill").GetString() == "SPELLING");
        Assert.Equal(JsonValueKind.Null,
            spelling.GetProperty("userSelectedLevel").ValueKind);
        Assert.Equal(JsonValueKind.Null,
            spelling.GetProperty("systemAssessedLevel").ValueKind);
        Assert.False(string.IsNullOrEmpty(
            detail.GetProperty("spelling").GetProperty("supportMode").GetString()));
    }

    // ── Authorization ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Analytics_stay_closed_to_a_learner()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var learnerId = await SignInAsync("nosy");

        // Hiding the nav item is not access control; the server refuses.
        foreach (var path in new[]
                 {
                     "/api/admin/overview",
                     "/api/admin/users",
                     $"/api/admin/users/{learnerId}",
                 })
        {
            var response = await Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }
}
