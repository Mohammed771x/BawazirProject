using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Lexicon;

namespace WordOs.Api.Tests;

/// <summary>
/// Interests, Settings and the Skills Hub, driven over real HTTP against real
/// PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OnboardingAndHubTests(PostgresFixture db) : IAsyncLifetime
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

    private async Task<Guid> SignInAsync()
    {
        var response = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"hub-{Guid.NewGuid():N}@test.dev",
            password = "correct-horse-battery",
            displayName = "Learner",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", body.GetProperty("token").GetString());

        return body.GetProperty("user").GetProperty("id").GetGuid();
    }

    /// <summary>Seeds a lexicon sense and adds it to the caller's pipeline.</summary>
    private async Task<Guid> AddWordAsync(
        string text = "research",
        CefrLevel level = CefrLevel.B1)
    {
        var senseId = $"hub-{Guid.NewGuid():N}";
        await using (var context = db.CreateContext())
        {
            context.LexiconEntries.Add(LexiconEntry.Create(
                senseId, text, text, "n", "careful study", "بحث علمي",
                level, 1, "en=oewn;ar=awn", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        var response = await Client.PostAsJsonAsync("/api/words", new { senseId });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    // ── Interests ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task The_interest_catalogue_is_returned_with_both_languages()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var catalogue = await Client.GetFromJsonAsync<JsonElement>(
            "/api/onboarding/interests");

        Assert.True(catalogue.GetArrayLength() > 5);
        var first = catalogue[0];
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("slug").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("labelAr").GetString()));
    }

    [SkippableFact]
    public async Task Saving_interests_advances_onboarding_and_marks_custom_ones()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();

        var response = await Client.PutAsJsonAsync("/api/me/interests", new
        {
            interests = new[] { "technology", "تصوير فوتوغرافي" },
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The server advances the stage — the client never sends it (rule R1).
        Assert.Equal("PLACEMENT", body.GetProperty("onboardingStage").GetString());

        await using var context = db.CreateContext();
        var stored = await context.UserInterests
            .Where(i => i.UserId == userId).ToListAsync();

        Assert.Equal(2, stored.Count);
        // The server classifies custom vs catalogue, not the client.
        Assert.Contains(stored, i => i.Interest == "technology" && !i.IsCustom);
        Assert.Contains(stored, i => i.Interest == "تصوير فوتوغرافي" && i.IsCustom);
    }

    [SkippableFact]
    public async Task Saving_interests_replaces_rather_than_appends()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();

        await Client.PutAsJsonAsync("/api/me/interests",
            new { interests = new[] { "technology", "science" } });
        await Client.PutAsJsonAsync("/api/me/interests",
            new { interests = new[] { "travel" } });

        await using var context = db.CreateContext();
        var stored = await context.UserInterests
            .Where(i => i.UserId == userId).ToListAsync();

        Assert.Single(stored);
        Assert.Equal("travel", stored[0].Interest);
    }

    [SkippableFact]
    public async Task An_empty_interest_list_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var empty = await Client.PutAsJsonAsync("/api/me/interests",
            new { interests = Array.Empty<string>() });
        var blank = await Client.PutAsJsonAsync("/api/me/interests",
            new { interests = new[] { "   ", "" } });

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }

    [SkippableFact]
    public async Task An_oversized_interest_list_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var response = await Client.PutAsJsonAsync("/api/me/interests", new
        {
            interests = Enumerable.Range(0, 200).Select(i => $"topic{i}").ToArray(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Settings_returns_five_skill_levels_with_Spelling_unlevelled()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var body = await Client.GetFromJsonAsync<JsonElement>("/api/settings");
        var levels = body.GetProperty("skillLevels");

        Assert.Equal(5, levels.GetArrayLength());

        var spelling = levels.EnumerateArray()
            .Single(l => l.GetProperty("skill").GetString() == "SPELLING");

        // Measured, but no CEFR band (ADR-008).
        Assert.Equal(JsonValueKind.Null,
            spelling.GetProperty("systemAssessedLevel").ValueKind);
        Assert.Equal(JsonValueKind.Null,
            spelling.GetProperty("userSelectedLevel").ValueKind);
    }

    [SkippableFact]
    public async Task Changing_a_level_moves_only_the_chosen_one_not_the_validated_one()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();

        // Give Reading a validated level so the separation is observable.
        await using (var seed = db.CreateContext())
        {
            await seed.Database.ExecuteSqlAsync(
                $"""
                 UPDATE user_skill_levels
                 SET "SystemAssessedLevel" = 'A2', "UserSelectedLevel" = 'A2'
                 WHERE "UserId" = {userId} AND "Skill" = 'Reading'
                 """);
        }

        var response = await Client.PatchAsJsonAsync("/api/settings/skill-level",
            new { skill = "READING", level = "C1" });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("C1", body.GetProperty("userSelectedLevel").GetString());
        // Rule R6: the validated level is untouched by a self-declared one.
        Assert.Equal("A2", body.GetProperty("systemAssessedLevel").GetString());
    }

    [SkippableFact]
    public async Task A_manual_level_change_is_recorded_as_such()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();

        await Client.PatchAsJsonAsync("/api/settings/skill-level",
            new { skill = "LISTENING", level = "B2" });

        await using var context = db.CreateContext();
        var change = await context.LevelChanges
            .SingleAsync(c => c.UserId == userId);

        Assert.Equal(LevelChangeType.UserManualChange, change.ChangeType);
        Assert.Equal(CefrLevel.B2, change.NewLevel);
    }

    [SkippableFact]
    public async Task Setting_a_CEFR_level_on_Spelling_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var response = await Client.PatchAsJsonAsync("/api/settings/skill-level",
            new { skill = "SPELLING", level = "B1" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("SKILL_NOT_LEVELLED",
            await response.Content.ReadAsStringAsync());
    }

    [SkippableTheory]
    [InlineData(1, 5)]    // below the floor → clamped up
    [InlineData(99, 15)]  // above the ceiling → clamped down
    [InlineData(8, 8)]    // inside the range → kept
    public async Task The_daily_target_is_clamped_by_the_server(
        int requested, int expected)
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var response = await Client.PatchAsJsonAsync("/api/settings/daily-target",
            new { skill = "READING", target = requested });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The client's number is a request, not a decision.
        Assert.Equal(expected, body.GetProperty("dailyTargetWords").GetInt32());
    }

    [SkippableFact]
    public async Task An_unknown_skill_or_level_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var badSkill = await Client.PatchAsJsonAsync("/api/settings/skill-level",
            new { skill = "TELEPATHY", level = "B1" });
        var badLevel = await Client.PatchAsJsonAsync("/api/settings/skill-level",
            new { skill = "READING", level = "Z9" });

        Assert.Equal(HttpStatusCode.BadRequest, badSkill.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badLevel.StatusCode);
    }

    [SkippableFact]
    public async Task A_learner_cannot_change_another_learners_settings()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var victimId = await SignInAsync();
        await SignInAsync(); // now authenticated as somebody else

        await Client.PatchAsJsonAsync("/api/settings/daily-target",
            new { skill = "READING", target = 15 });

        await using var context = db.CreateContext();
        var victim = await context.SkillLevels.SingleAsync(
            l => l.UserId == victimId && l.Skill == SkillType.Reading);

        // The change landed on the caller, never on the id in any request.
        Assert.NotEqual(15, victim.DailyTargetWords);
    }

    // ── Skills Hub ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task An_empty_hub_reports_every_skill_as_EMPTY()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var hub = await Client.GetFromJsonAsync<JsonElement>("/api/hub");
        var skills = hub.GetProperty("skills");

        Assert.Equal(5, skills.GetArrayLength());
        Assert.All(skills.EnumerateArray().ToList(), s =>
            Assert.Equal("EMPTY", s.GetProperty("availability").GetString()));
        Assert.Equal(0, hub.GetProperty("vocabulary").GetProperty("learning").GetInt32());
    }

    [SkippableFact]
    public async Task A_new_word_makes_only_the_first_skill_available()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync();

        var hub = await Client.GetFromJsonAsync<JsonElement>("/api/hub");
        var skills = hub.GetProperty("skills").EnumerateArray().ToList();

        var reading = skills.Single(s => s.GetProperty("skill").GetString() == "READING");
        Assert.Equal("AVAILABLE", reading.GetProperty("availability").GetString());
        Assert.Equal(1, reading.GetProperty("dueWordCount").GetInt32());

        // The other four are still waiting their turn.
        foreach (var other in skills.Where(
                     s => s.GetProperty("skill").GetString() != "READING"))
        {
            Assert.Equal("EMPTY", other.GetProperty("availability").GetString());
        }

        Assert.Equal(1, hub.GetProperty("vocabulary").GetProperty("learning").GetInt32());
        Assert.Equal(1, hub.GetProperty("dailyProgress")
            .GetProperty("wordsAddedToday").GetInt32());
    }

    [SkippableFact]
    public async Task The_session_word_count_is_capped_by_the_daily_target()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        for (var i = 0; i < 8; i++) await AddWordAsync($"word{i}");

        await Client.PatchAsJsonAsync("/api/settings/daily-target",
            new { skill = "READING", target = 5 });

        var hub = await Client.GetFromJsonAsync<JsonElement>("/api/hub");
        var reading = hub.GetProperty("skills").EnumerateArray()
            .Single(s => s.GetProperty("skill").GetString() == "READING");

        Assert.Equal(8, reading.GetProperty("dueWordCount").GetInt32());
        // Due is what exists; session is what the learner will actually be asked.
        Assert.Equal(5, reading.GetProperty("sessionWordCount").GetInt32());
    }

    [SkippableFact]
    public async Task Spelling_carries_no_level_on_its_hub_card()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var hub = await Client.GetFromJsonAsync<JsonElement>("/api/hub");
        var spelling = hub.GetProperty("skills").EnumerateArray()
            .Single(s => s.GetProperty("skill").GetString() == "SPELLING");

        Assert.Equal(JsonValueKind.Null, spelling.GetProperty("level").ValueKind);
    }

    [SkippableFact]
    public async Task The_hub_shows_only_the_callers_own_vocabulary()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        await SignInAsync();
        await AddWordAsync();
        await AddWordAsync("evidence");

        await SignInAsync(); // a different learner
        var hub = await Client.GetFromJsonAsync<JsonElement>("/api/hub");

        Assert.Equal(0, hub.GetProperty("vocabulary").GetProperty("learning").GetInt32());
    }
}
