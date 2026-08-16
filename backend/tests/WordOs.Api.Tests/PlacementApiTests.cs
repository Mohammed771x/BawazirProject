using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Placement;

namespace WordOs.Api.Tests;

/// <summary>
/// The adaptive placement test driven over real HTTP against real PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PlacementApiTests(PostgresFixture db) : IAsyncLifetime
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
            email = $"place-{Guid.NewGuid():N}@test.dev",
            password = "correct-horse-battery",
            displayName = "Learner",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return body.GetProperty("user").GetProperty("id").GetGuid();
    }

    private static string AnswerFor(string itemId, bool correctly)
    {
        var item = PlacementItemBank.Find(itemId)!;

        if (item.IsFreeText)
        {
            return correctly
                ? "I usually plan my week carefully because it helps me focus, and "
                  + "when something unexpected happens I adjust the plan instead of "
                  + "abandoning it entirely, which keeps my progress steady."
                : "no";
        }

        return correctly
            ? item.CorrectAnswer!
            : item.Options.First(o => o != item.CorrectAnswer);
    }

    /// <summary>Plays a whole placement over HTTP and returns the result body.</summary>
    private async Task<(JsonElement Result, int Questions)> PlayAsync(
        Func<string, bool> answerCorrectly)
    {
        var start = await Client.PostAsync("/api/placement/start", null);
        start.EnsureSuccessStatusCode();
        var step = await start.Content.ReadFromJsonAsync<JsonElement>();

        var sessionId = step.GetProperty("sessionId").GetGuid();
        var asked = 0;

        while (!step.GetProperty("isComplete").GetBoolean())
        {
            Assert.True(++asked < 60, "the adaptive loop must terminate");

            var itemId = step.GetProperty("item").GetProperty("id").GetString()!;
            var response = await Client.PostAsJsonAsync(
                $"/api/placement/{sessionId}/answer",
                new { itemId, answer = AnswerFor(itemId, answerCorrectly(itemId)) });

            response.EnsureSuccessStatusCode();
            step = await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        var complete = await Client.PostAsync(
            $"/api/placement/{sessionId}/complete", null);
        complete.EnsureSuccessStatusCode();

        return (await complete.Content.ReadFromJsonAsync<JsonElement>(), asked);
    }

    // ── The protocol ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Starting_returns_the_first_question_without_its_answer()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var response = await Client.PostAsync("/api/placement/start", null);
        response.EnsureSuccessStatusCode();
        var step = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(step.GetProperty("isComplete").GetBoolean());
        var item = step.GetProperty("item");

        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("prompt").GetString()));

        // Neither the answer nor the difficulty band ever reaches the client.
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("correctAnswer", raw);
        Assert.DoesNotContain("\"level\"", raw);
        Assert.DoesNotContain("difficulty", raw, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Answering_a_question_that_is_not_the_current_one_is_rejected()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var start = await Client.PostAsync("/api/placement/start", null);
        var step = await start.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = step.GetProperty("sessionId").GetGuid();
        var current = step.GetProperty("item").GetProperty("id").GetString()!;

        var other = PlacementItemBank.All.First(i => i.Id != current).Id;
        var response = await Client.PostAsJsonAsync(
            $"/api/placement/{sessionId}/answer",
            new { itemId = other, answer = "anything" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("ITEM_NOT_CURRENT", await response.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task Replaying_the_same_answer_cannot_inflate_the_estimate()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var start = await Client.PostAsync("/api/placement/start", null);
        var step = await start.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = step.GetProperty("sessionId").GetGuid();
        var itemId = step.GetProperty("item").GetProperty("id").GetString()!;

        await Client.PostAsJsonAsync($"/api/placement/{sessionId}/answer",
            new { itemId, answer = AnswerFor(itemId, true) });

        // The same item again: it is no longer current, so it is refused.
        var replay = await Client.PostAsJsonAsync($"/api/placement/{sessionId}/answer",
            new { itemId, answer = AnswerFor(itemId, true) });

        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);

        await using var context = db.CreateContext();
        var answers = await context.PlacementAnswers
            .CountAsync(a => a.SessionId == sessionId && a.ItemId == itemId);
        Assert.Equal(1, answers);
    }

    [SkippableFact]
    public async Task Completing_an_unfinished_test_is_rejected()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var start = await Client.PostAsync("/api/placement/start", null);
        var step = await start.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = step.GetProperty("sessionId").GetGuid();

        var response = await Client.PostAsync(
            $"/api/placement/{sessionId}/complete", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("PLACEMENT_INCOMPLETE",
            await response.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task Another_learners_run_is_not_addressable()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        await SignInAsync();
        var start = await Client.PostAsync("/api/placement/start", null);
        var step = await start.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = step.GetProperty("sessionId").GetGuid();
        var itemId = step.GetProperty("item").GetProperty("id").GetString()!;

        await SignInAsync(); // a different learner, who knows the id

        var answer = await Client.PostAsJsonAsync(
            $"/api/placement/{sessionId}/answer",
            new { itemId, answer = AnswerFor(itemId, true) });
        var complete = await Client.PostAsync(
            $"/api/placement/{sessionId}/complete", null);

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, complete.StatusCode);
    }

    // ── A full run ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_full_run_asks_between_12_and_22_questions_and_places_the_learner()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();

        var (result, asked) = await PlayAsync(_ => true);

        Assert.InRange(asked, 12, 22);

        var levels = result.GetProperty("levels").EnumerateArray().ToList();
        Assert.Equal(5, levels.Count);

        // A strong run places at B1 or above on every levelled skill.
        foreach (var level in levels.Where(
                     l => l.GetProperty("skill").GetString() != "SPELLING"))
        {
            var wire = level.GetProperty("systemAssessedLevel").GetString();
            Assert.NotNull(wire);
            var parsed = CefrLevelExtensions.TryFromWire(wire)!.Value;
            Assert.True(parsed.Rank() >= CefrLevel.B1.Rank(),
                $"{level.GetProperty("skill")} placed at {wire}");
        }

        // Onboarding advances and the levels are persisted.
        await using var context = db.CreateContext();
        var user = await context.Users
            .Include(u => u.SkillLevels)
            .SingleAsync(u => u.Id == userId);

        Assert.Equal(OnboardingStage.Complete, user.OnboardingStage);
        Assert.All(user.SkillLevels.Where(l => l.CarriesCefrLevel),
            l => Assert.NotNull(l.SystemAssessedLevel));
    }

    [SkippableFact]
    public async Task A_weak_run_places_low()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var (result, _) = await PlayAsync(_ => false);

        foreach (var level in result.GetProperty("levels").EnumerateArray()
                     .Where(l => l.GetProperty("skill").GetString() != "SPELLING"))
        {
            var wire = level.GetProperty("systemAssessedLevel").GetString()!;
            var parsed = CefrLevelExtensions.TryFromWire(wire)!.Value;
            Assert.True(parsed.Rank() <= CefrLevel.A2.Rank(),
                $"{level.GetProperty("skill")} placed at {wire}");
        }
    }

    [SkippableFact]
    public async Task Spelling_is_measured_but_never_levelled()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();

        var (result, _) = await PlayAsync(_ => true);

        var spelling = result.GetProperty("levels").EnumerateArray()
            .Single(l => l.GetProperty("skill").GetString() == "SPELLING");

        Assert.Equal(JsonValueKind.Null,
            spelling.GetProperty("systemAssessedLevel").ValueKind);

        var diagnostic = result.GetProperty("spelling");
        Assert.True(diagnostic.GetProperty("itemsAnswered").GetInt32() > 0);
        Assert.Equal("FREE_TYPING", diagnostic.GetProperty("supportMode").GetString());

        // And it stays null in the database, not a sentinel A1.
        await using var context = db.CreateContext();
        var level = await context.SkillLevels.SingleAsync(
            l => l.UserId == userId && l.Skill == SkillType.Spelling);

        Assert.Null(level.SystemAssessedLevel);
        Assert.Equal(SpellingInputMode.FreeTyping, level.SpellingSupportMode);
    }

    [SkippableFact]
    public async Task A_weak_speller_starts_on_letter_tiles()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var (result, _) = await PlayAsync(_ => false);

        Assert.Equal("LETTER_TILES",
            result.GetProperty("spelling").GetProperty("supportMode").GetString());
    }

    [SkippableFact]
    public async Task An_erratic_learner_is_told_the_level_is_provisional()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var flip = false;
        var (result, _) = await PlayAsync(_ => flip = !flip);

        var summary = result.GetProperty("summary").GetString()!;
        Assert.Contains("provisional", summary);

        // A level is still assigned — we never refuse to place a learner.
        Assert.All(result.GetProperty("levels").EnumerateArray()
                .Where(l => l.GetProperty("skill").GetString() != "SPELLING")
                .ToList(),
            l => Assert.Equal(JsonValueKind.String,
                l.GetProperty("systemAssessedLevel").ValueKind));
    }

    [SkippableFact]
    public async Task Placement_records_the_level_history_as_PLACEMENT()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();

        await PlayAsync(_ => true);

        await using var context = db.CreateContext();
        var changes = await context.LevelChanges
            .Where(c => c.UserId == userId).ToListAsync();

        Assert.Equal(5, changes.Count);
        Assert.All(changes,
            c => Assert.Equal(LevelChangeType.Placement, c.ChangeType));
    }

    [SkippableFact]
    public async Task Starting_again_abandons_the_previous_unfinished_run()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();

        var first = await Client.PostAsync("/api/placement/start", null);
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("sessionId").GetGuid();

        var second = await Client.PostAsync("/api/placement/start", null);
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("sessionId").GetGuid();

        Assert.NotEqual(firstId, secondId);

        // Resuming a half-scored estimate would corrupt the level.
        await using var context = db.CreateContext();
        var open = await context.PlacementSessions
            .CountAsync(s => s.UserId == userId && !s.IsComplete);
        Assert.Equal(1, open);
    }

    [SkippableFact]
    public async Task Every_answer_is_scored_server_side_and_persisted()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var start = await Client.PostAsync("/api/placement/start", null);
        var step = await start.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = step.GetProperty("sessionId").GetGuid();
        var itemId = step.GetProperty("item").GetProperty("id").GetString()!;

        await Client.PostAsJsonAsync($"/api/placement/{sessionId}/answer",
            new { itemId, answer = AnswerFor(itemId, true) });

        await using var context = db.CreateContext();
        var answer = await context.PlacementAnswers
            .SingleAsync(a => a.SessionId == sessionId && a.ItemId == itemId);

        // Score and difficulty are derived from the item the server issued —
        // the client sends neither.
        Assert.Equal(1.0, answer.Score, 3);
        var expected = new PlacementConfig().Scale.DifficultyOf(
            PlacementItemBank.Find(itemId)!.Level);
        Assert.Equal(expected, answer.Difficulty, 6);
    }

    [SkippableFact]
    public async Task An_oversized_answer_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var start = await Client.PostAsync("/api/placement/start", null);
        var step = await start.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = step.GetProperty("sessionId").GetGuid();
        var itemId = step.GetProperty("item").GetProperty("id").GetString()!;

        var response = await Client.PostAsJsonAsync(
            $"/api/placement/{sessionId}/answer",
            new { itemId, answer = new string('a', 10_000) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
