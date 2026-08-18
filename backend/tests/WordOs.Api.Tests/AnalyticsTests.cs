using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Lexicon;
using WordOs.Domain.Users;

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

    // ── The activity log (Part 3 §34–§35) ────────────────────────────────────

    [SkippableFact]
    public async Task Everything_a_learner_does_leaves_a_row_behind()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync("activity");
        await AddWordAsync("research", "بحث علمي");
        await RunReadingAsync();

        await using var context = db.CreateContext();
        var events = await context.ActivityEvents
            .Where(e => e.UserId == userId)
            .OrderBy(e => e.Id)
            .ToListAsync();

        // The trail every dashboard figure is derived from. Without it, "did
        // they use the app on Tuesday?" can only be guessed at from whatever
        // side-effects happened to be durable.
        Assert.Contains(events, e => e.Type == ActivityType.Registered);
        Assert.Contains(events, e => e.Type == ActivityType.WordAdded);
        Assert.Contains(events, e =>
            e.Type == ActivityType.SessionStarted && e.Skill == SkillType.Reading);
        Assert.Contains(events, e =>
            e.Type == ActivityType.SessionCompleted && e.Skill == SkillType.Reading);
    }

    [SkippableFact]
    public async Task A_practice_session_is_logged_as_practice()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync("activity-practice");

        var start = await Client.PostAsync(
            "/api/sessions/reading/start?practice=true", null);
        start.EnsureSuccessStatusCode();

        await using var context = db.CreateContext();
        var events = await context.ActivityEvents
            .Where(e => e.UserId == userId)
            .ToListAsync();

        // Distinguished at the point of recording, so a month of practice can
        // never be mistaken for a month of progress.
        Assert.Contains(events, e => e.Type == ActivityType.PracticeStarted);
        Assert.DoesNotContain(events, e => e.Type == ActivityType.SessionStarted);
    }

    [SkippableFact]
    public async Task The_log_is_append_only_and_survives_a_second_visit()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync("activity-append");

        await using (var first = db.CreateContext())
        {
            var count = await first.ActivityEvents
                .CountAsync(e => e.UserId == userId);
            Assert.True(count > 0);
        }

        await AddWordAsync("evidence", "دليل");

        await using var after = db.CreateContext();
        var events = await after.ActivityEvents
            .Where(e => e.UserId == userId)
            .OrderBy(e => e.Id)
            .ToListAsync();

        // Ids strictly increase and nothing is rewritten: a figure computed
        // today from the log is the same figure computed next month.
        Assert.Equal(events.Select(e => e.Id).Order(), events.Select(e => e.Id));
        Assert.Contains(events, e => e.Type == ActivityType.Registered);
    }

    // ── Admin list: search, window, paging (Part 3 §37) ───────────────────────

    [SkippableFact]
    public async Task The_learner_list_can_be_searched_and_paged()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync("findme-alpha");
        await SignInAsync("findme-beta");
        await SignInAsOwnerAsync("list-owner");

        var searched = await Client.GetFromJsonAsync<JsonElement>(
            "/api/admin/users?q=findme-alpha");

        Assert.Equal(1, searched.GetProperty("total").GetInt32());

        var paged = await Client.GetFromJsonAsync<JsonElement>(
            "/api/admin/users?pageSize=1");

        Assert.Single(paged.GetProperty("items").EnumerateArray());
        Assert.True(paged.GetProperty("total").GetInt32() > 1,
            "total counts every match, not the page");
        Assert.True(paged.GetProperty("hasMore").GetBoolean());
    }

    [SkippableFact]
    public async Task The_time_window_is_answered_from_the_activity_log()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var quietId = await SignInAsync("quiet");
        await SignInAsOwnerAsync("window-owner");

        // Backdate everything this learner ever did. A "last login" column
        // would still place them inside today's window on their next request;
        // the log says plainly that nothing happened recently.
        await using (var context = db.CreateContext())
        {
            await context.Database.ExecuteSqlAsync(
                $"""UPDATE activity_events SET "CreatedAt" = now() - interval '30 days' WHERE "UserId" = {quietId}""");
        }

        var today = await Client.GetFromJsonAsync<JsonElement>(
            "/api/admin/users?days=1");
        var everyone = await Client.GetFromJsonAsync<JsonElement>(
            "/api/admin/users");

        var todayIds = today.GetProperty("items").EnumerateArray()
            .Select(u => u.GetProperty("id").GetGuid()).ToList();
        var allIds = everyone.GetProperty("items").EnumerateArray()
            .Select(u => u.GetProperty("id").GetGuid()).ToList();

        Assert.DoesNotContain(quietId, todayIds);
        Assert.Contains(quietId, allIds);
    }

    [SkippableFact]
    public async Task The_overview_window_scopes_what_it_should_and_not_what_it_should_not()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync("scoped");
        await AddWordAsync("research", "بحث علمي");
        await SignInAsOwnerAsync("scope-owner");

        var allTime = await Client.GetFromJsonAsync<JsonElement>(
            "/api/admin/overview");
        var today = await Client.GetFromJsonAsync<JsonElement>(
            "/api/admin/overview?days=1");

        // Words added is a windowed count...
        Assert.True(allTime.GetProperty("wordsAddedTotal").GetInt32() >= 1);

        // ...but pipeline completion is not. A word needs five skills and four
        // two-day gaps to reach Active, so "of the words added today, how many
        // completed?" is structurally zero and would read as a collapse.
        Assert.Equal(
            allTime.GetProperty("pipelineCompletionRate").GetDouble(),
            today.GetProperty("pipelineCompletionRate").GetDouble(),
            3);
    }

    // ── The Owner's view of a learner's vocabulary (Part 3) ──────────────────

    [SkippableFact]
    public async Task A_learners_words_can_be_listed_by_pipeline_state()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var learnerId = await SignInAsync("wordview");
        await AddWordAsync("research", "بحث علمي");
        await AddWordAsync("evidence", "دليل");
        await SignInAsOwnerAsync("wordview-owner");

        var all = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/admin/users/{learnerId}/words");
        Assert.Equal(2, all.GetProperty("total").GetInt32());

        // The states the learner's own screen deliberately hides are exactly
        // what this view is for (Part 2 §42 vs Part 3).
        var learning = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/admin/users/{learnerId}/words?state=LEARNING");
        Assert.Equal(2, learning.GetProperty("total").GetInt32());

        var archived = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/admin/users/{learnerId}/words?state=ARCHIVED");
        Assert.Equal(0, archived.GetProperty("total").GetInt32());

        var searched = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/admin/users/{learnerId}/words?q=resea");
        Assert.Equal(1, searched.GetProperty("total").GetInt32());
    }

    [SkippableFact]
    public async Task A_words_journey_shows_the_failures_and_not_just_the_ending()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync("journey");
        var wordId = await AddWordAsync("research", "بحث علمي");

        // Fail Reading. The word's own row afterwards says only "on Reading,
        // due in two days" — the fact that it was attempted and failed lives
        // solely in the event log.
        await RunReadingAsync(pass: false);

        await SignInAsOwnerAsync("journey-owner");
        var journey = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/admin/words/{wordId}");

        var events = journey.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("type").GetString())
            .ToList();

        Assert.Contains("ADDED", events);
        Assert.Contains("SKILL_STARTED", events);
        Assert.Contains("SKILL_FAILED", events);

        // Meanwhile the word itself looks untouched — which is exactly why the
        // journey is read from the log rather than from the row.
        Assert.Equal("LEARNING",
            journey.GetProperty("word").GetProperty("state").GetString());

        // And it says whose word it is, from the row rather than the request.
        Assert.False(string.IsNullOrWhiteSpace(
            journey.GetProperty("learner").GetProperty("email").GetString()));
    }

    [SkippableFact]
    public async Task A_learner_cannot_read_the_admin_word_views()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var learnerId = await SignInAsync("nosy");
        var wordId = await AddWordAsync("research", "بحث علمي");

        // Their own words, through the Owner's route: still forbidden. The
        // route is the privilege, not the data behind it.
        foreach (var path in new[]
                 {
                     $"/api/admin/users/{learnerId}/words",
                     $"/api/admin/words/{wordId}",
                 })
        {
            var response = await Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    // ── Placement evidence (Part 3) ──────────────────────────────────────────

    [SkippableFact]
    public async Task The_answers_behind_a_placement_level_can_be_read_back()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var learnerId = await SignInAsync("evidence");

        // Walk the adaptive test to the end, answering everything.
        var start = await Client.PostAsync("/api/placement/start", null);
        start.EnsureSuccessStatusCode();
        var step = await start.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = step.GetProperty("sessionId").GetGuid();

        var guard = 0;
        while (!step.GetProperty("isComplete").GetBoolean() && guard++ < 80)
        {
            var item = step.GetProperty("item");
            var options = item.GetProperty("options").EnumerateArray().ToList();

            var answer = options.Count > 0
                ? options[0].GetString()!
                : "I think the answer depends on the situation and the people.";

            var response = await Client.PostAsJsonAsync(
                $"/api/placement/{sessionId}/answer",
                new { itemId = item.GetProperty("id").GetString(), answer });
            response.EnsureSuccessStatusCode();
            step = await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        (await Client.PostAsync($"/api/placement/{sessionId}/complete", null))
            .EnsureSuccessStatusCode();

        await SignInAsOwnerAsync("evidence-owner");
        var evidence = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/admin/users/{learnerId}/placement");

        Assert.True(evidence.GetProperty("completed").GetBoolean());
        Assert.Equal(2, evidence.GetProperty("testVersion").GetInt32());

        var answers = evidence.GetProperty("answers").EnumerateArray().ToList();
        Assert.NotEmpty(answers);

        // Each answer carries what it measured, at what level.
        Assert.All(answers, a =>
        {
            Assert.False(string.IsNullOrWhiteSpace(
                a.GetProperty("domain").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(
                a.GetProperty("level").GetString()));
        });

        // And the free-text and spoken ones carry the learner's own words,
        // which is the whole point: a level can be believed, evidence can be
        // audited. Multiple-choice items deliberately store no raw answer —
        // the score already says which option was picked.
        Assert.Contains(answers, a =>
            !string.IsNullOrWhiteSpace(a.GetProperty("rawAnswer").GetString()));

        // Grammar and spelling appear even though neither is a visible skill:
        // they are evidence for the ones that are.
        var domains = answers
            .Select(a => a.GetProperty("domain").GetString())
            .Distinct()
            .ToList();
        Assert.Contains("Grammar", domains);

        // And the two halves of "have they moved?" are both present.
        var levels = evidence.GetProperty("progress").GetProperty("levels")
            .EnumerateArray().ToList();
        Assert.Equal(4, levels.Count);
        Assert.All(levels, l =>
        {
            Assert.NotEqual(JsonValueKind.Undefined,
                l.GetProperty("initialLevel").ValueKind);
            Assert.NotEqual(JsonValueKind.Undefined,
                l.GetProperty("currentLevel").ValueKind);
        });
    }

    [SkippableFact]
    public async Task Placement_evidence_answers_cleanly_when_there_is_none()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var learnerId = await SignInAsync("no-placement");
        await SignInAsOwnerAsync("no-placement-owner");

        // A learner who never finished onboarding is an ordinary case, not an
        // error — the dashboard should say "nothing here", not fall over.
        var evidence = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/admin/users/{learnerId}/placement");

        Assert.False(evidence.GetProperty("completed").GetBoolean());
        Assert.Empty(evidence.GetProperty("answers").EnumerateArray());
    }

    [SkippableFact]
    public async Task Placement_evidence_is_owner_only()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var learnerId = await SignInAsync("private-evidence");

        // Their own placement answers, through the Owner's route: forbidden.
        var response = await Client.GetAsync(
            $"/api/admin/users/{learnerId}/placement");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
