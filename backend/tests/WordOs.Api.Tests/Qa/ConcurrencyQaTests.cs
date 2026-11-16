using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Words;
using Xunit.Abstractions;

namespace WordOs.Api.Tests.Qa;

/// <summary>
/// QA prompt 03: the same resource hit by 10 requests released together.
/// </summary>
/// <remarks>
/// Expected effect is stated per test. Each test logs a status histogram so the
/// report can quote it. A 409 for the losers is a correct answer; a 500, a
/// duplicate row or a double-applied effect is not.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class ConcurrencyQaTests(PostgresFixture db, ITestOutputHelper output) : QaApiBase(db)
{
    private const int N = 10;

    private void Log(string name, IEnumerable<HttpResponseMessage> r) =>
        output.WriteLine($"{name}: {Histogram(r)}");

    [SkippableFact]
    public async Task C01_same_email_registered_ten_times_creates_one_account()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var email = $"race-{Guid.NewGuid():N}@test.dev";

        var responses = await BurstAsync(N, _ => NewClient().PostAsJsonAsync("/api/auth/register", new
        {
            email, password = "correct-horse-battery", displayName = "R",
            phoneCountryCode = "967", phoneNumber = "770000001",
        }));
        Log("register", responses);

        Assert.Single(responses, r => r.IsSuccessStatusCode);
        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        await using var context = Db.CreateContext();
        Assert.Equal(1, await context.Users.CountAsync(u => u.Email == email));
    }

    [SkippableFact]
    public async Task C02_same_sense_added_ten_times_creates_one_word()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var senseId = await SeedSenseAsync("harbour", "ميناء");

        var responses = await BurstAsync(N, _ => a.Client.PostAsJsonAsync("/api/words", new { senseId }));
        Log("add-word", responses);

        Assert.Single(responses, r => r.IsSuccessStatusCode);
        Assert.All(responses.Where(r => !r.IsSuccessStatusCode),
            r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        await using var context = Db.CreateContext();
        Assert.Equal(1, await context.Words.CountAsync(w => w.UserId == a.Id && w.SenseId == senseId));
        var wordId = (await context.Words.FirstAsync(w => w.UserId == a.Id)).Id;
        Assert.Equal(1, await context.WordEvents.CountAsync(e => e.WordId == wordId && e.Type == WordEventType.Added));
    }

    private async Task<(Guid SessionId, JsonElement Session)> StartReadingAsync(Learner l)
    {
        var start = await l.Client.PostAsync("/api/sessions/reading/start", null);
        start.EnsureSuccessStatusCode();
        var body = await JsonAsync(start);
        return (body.GetProperty("id").GetGuid(), body);
    }

    private async Task<string> CorrectAnswerAsync(Guid itemId)
    {
        await using var context = Db.CreateContext();
        return (await context.SessionItems.FirstAsync(i => i.Id == itemId)).CorrectAnswer;
    }

    private async Task AnswerAllCorrectlyAsync(Learner l, Guid sessionId)
    {
        while (true)
        {
            var s = await l.Client.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}");
            var next = s.GetProperty("progress").GetProperty("nextItemId");
            if (next.ValueKind == JsonValueKind.Null) return;
            var id = next.GetGuid();
            var r = await l.Client.PostAsJsonAsync($"/api/sessions/{sessionId}/answer",
                new { itemId = id, answer = await CorrectAnswerAsync(id) });
            r.EnsureSuccessStatusCode();
        }
    }

    [SkippableFact]
    public async Task C03_same_answer_sent_ten_times_is_recorded_once()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        await AddWordAsync(a, "harbour", "ميناء");
        var (sessionId, session) = await StartReadingAsync(a);
        var itemId = session.GetProperty("progress").GetProperty("nextItemId").GetGuid();
        var answer = await CorrectAnswerAsync(itemId);

        var responses = await BurstAsync(N, _ => a.Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/answer", new { itemId, answer }));
        Log("answer", responses);

        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        await using var context = Db.CreateContext();
        var item = await context.SessionItems.FirstAsync(i => i.Id == itemId);
        output.WriteLine($"item attempts after burst = {item.Attempts}, firstAttemptCorrect = {item.FirstAttemptCorrect}");
        Assert.Equal(1, item.Attempts);
        Assert.True(item.FirstAttemptCorrect);
    }

    [SkippableFact]
    public async Task C04_complete_sent_ten_times_applies_the_outcome_once()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var wordId = await AddWordAsync(a, "harbour", "ميناء");
        var (sessionId, _) = await StartReadingAsync(a);
        await AnswerAllCorrectlyAsync(a, sessionId);

        var responses = await BurstAsync(N, _ => a.Client.PostAsync($"/api/sessions/{sessionId}/complete", null));
        Log("complete", responses);

        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        await using var context = Db.CreateContext();
        var word = await context.Words.Include(w => w.Skills).FirstAsync(w => w.Id == wordId);
        var reading = word.Skills.First(s => s.Skill == SkillType.Reading);
        var passedEvents = await context.WordEvents.CountAsync(e =>
            e.WordId == wordId && e.Type == WordEventType.SkillPassed);
        var level = await context.SkillLevels.FirstAsync(l => l.UserId == a.Id && l.Skill == SkillType.Reading);
        var completedActivities = await context.ActivityEvents.CountAsync(e =>
            e.UserId == a.Id && e.EntityId == sessionId);
        output.WriteLine($"reading attempts={reading.Attempts} passedEvents={passedEvents} evalSessions={level.EvaluationSessions} current={word.CurrentSkill} activityRowsForSession={completedActivities}");

        Assert.Equal(SkillStatus.Passed, reading.Status);
        Assert.Equal(1, reading.Attempts);
        Assert.Equal(1, passedEvents);
        Assert.Equal(1, level.EvaluationSessions);
        Assert.Equal(SkillType.Listening, word.CurrentSkill);
    }

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task C05_complete_racing_abandon_leaves_one_consistent_outcome(int round)
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var wordId = await AddWordAsync(a, $"harbour{round}", "ميناء");
        var (sessionId, _) = await StartReadingAsync(a);
        await AnswerAllCorrectlyAsync(a, sessionId);

        var responses = await BurstAsync(2, i => i == 0
            ? a.Client.PostAsync($"/api/sessions/{sessionId}/complete", null)
            : a.Client.PostAsync($"/api/sessions/{sessionId}/abandon", null));
        output.WriteLine($"round {round}: complete={(int)responses[0].StatusCode} abandon={(int)responses[1].StatusCode} body={await responses[0].Content.ReadAsStringAsync()}");

        await using var context = Db.CreateContext();
        var word = await context.Words.Include(w => w.Skills).FirstAsync(w => w.Id == wordId);
        var reading = word.Skills.First(s => s.Skill == SkillType.Reading);
        var sessionRow = await context.SkillSessions.FirstOrDefaultAsync(s => s.Id == sessionId);

        if (responses[0].IsSuccessStatusCode)
        {
            Assert.Equal(SkillStatus.Passed, reading.Status);
            Assert.NotNull(sessionRow);
            Assert.True(sessionRow!.IsComplete);
        }
        else
        {
            // Abandon won: the word is exactly as it was, still due.
            Assert.Equal(SkillStatus.Available, reading.Status);
            Assert.Equal(0, reading.Attempts);
        }

        output.WriteLine($"round {round}: state consistent (reading={reading.Status}, attempts={reading.Attempts}, sessionRow={(sessionRow is null ? "deleted" : "kept")})");
        // Checked last so the state above is reported even when this fails.
        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
    }

    [SkippableFact]
    public async Task C04b_complete_sent_twice_in_sequence_applies_once()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var wordId = await AddWordAsync(a, "harbour", "ميناء");
        var (sessionId, _) = await StartReadingAsync(a);
        await AnswerAllCorrectlyAsync(a, sessionId);

        var first = await a.Client.PostAsync($"/api/sessions/{sessionId}/complete", null);
        var second = await a.Client.PostAsync($"/api/sessions/{sessionId}/complete", null);
        output.WriteLine($"first={(int)first.StatusCode} second={(int)second.StatusCode} {await second.Content.ReadAsStringAsync()}");

        await using var context = Db.CreateContext();
        Assert.Equal(1, await context.WordEvents.CountAsync(e =>
            e.WordId == wordId && e.Type == WordEventType.SkillPassed));
    }

    [SkippableFact]
    public async Task C06_one_refresh_token_presented_ten_times_at_once()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();

        var responses = await BurstAsync(N, _ => NewClient().PostAsJsonAsync(
            "/api/auth/refresh", new { refreshToken = a.RefreshToken }));
        Log("refresh", responses);

        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        Assert.Contains(responses, r => r.IsSuccessStatusCode);

        await using var context = Db.CreateContext();
        var tokens = await context.RefreshTokens.Where(t => t.UserId == a.Id).ToListAsync();
        var families = tokens.Select(t => t.FamilyId).Distinct().Count();
        var unusedActive = tokens.Count(t => t.RevokedAt == null && t.UsedAt == null && t.ExpiresAt > DateTimeOffset.UtcNow);
        output.WriteLine($"tokens={tokens.Count} families={families} unused-active={unusedActive} revoked={tokens.Count(t => t.RevokedAt != null)}");

        // One family per sign-in; the burst must not fork a second one.
        Assert.Equal(1, families);
        // The learner still has a usable session afterwards (ADR-093)...
        Assert.True(unusedActive >= 1);
        // ...and rotation stays single-use: one presented token, one live
        // successor (the replay grace revokes the previous successor before it
        // reissues, AuthEndpoints.RefreshAsync).
        Assert.Equal(1, unusedActive);
    }

    [SkippableFact]
    public async Task C07_weekly_review_started_ten_times_leaves_one_open_review()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        await AddWordAsync(a, "harbour", "ميناء");
        await AddWordAsync(a, "lantern", "فانوس");
        Clock.SkipDays(8);

        var responses = await BurstAsync(N, _ => a.Client.PostAsync("/api/weekly-review/start", null));
        Log("weekly-start", responses);

        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        await using var context = Db.CreateContext();
        var open = await context.WeeklyReviews.CountAsync(r => r.UserId == a.Id && !r.IsComplete);
        output.WriteLine($"open weekly reviews after burst = {open}");
        Assert.Equal(1, open);
    }

    [SkippableFact]
    public async Task C08_deleting_a_word_while_its_session_completes_never_resurrects_it()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var wordId = await AddWordAsync(a, "harbour", "ميناء");
        var (sessionId, _) = await StartReadingAsync(a);
        await AnswerAllCorrectlyAsync(a, sessionId);

        var responses = await BurstAsync(2, i => i == 0
            ? a.Client.PostAsync($"/api/sessions/{sessionId}/complete", null)
            : a.Client.DeleteAsync($"/api/words/{wordId}"));
        output.WriteLine($"complete={(int)responses[0].StatusCode} delete={(int)responses[1].StatusCode}");

        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        await using var context = Db.CreateContext();
        var word = await context.Words.IgnoreQueryFilters().FirstAsync(w => w.Id == wordId);
        if (responses[1].IsSuccessStatusCode) Assert.Equal(WordState.Deleted, word.State);
    }

    [SkippableFact]
    public async Task C09_two_devices_setting_the_daily_target_converge_on_one_value()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var second = NewClient();
        second.DefaultRequestHeaders.Authorization = a.Client.DefaultRequestHeaders.Authorization;

        var responses = await BurstAsync(N, i => (i % 2 == 0 ? a.Client : second).PatchAsJsonAsync(
            "/api/settings/daily-target", new { skill = "READING", target = 5 + i }));
        Log("daily-target", responses);

        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
        await using var context = Db.CreateContext();
        var rows = await context.SkillLevels.Where(l => l.UserId == a.Id && l.Skill == SkillType.Reading).ToListAsync();
        Assert.Single(rows);
        Assert.InRange(rows[0].DailyTargetWords, 5, 14);
    }

    [SkippableFact]
    public async Task C10_a_retried_add_after_a_lost_reply_does_not_duplicate()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var senseId = await SeedSenseAsync("harbour", "ميناء");

        // The first reply is "lost": the client never reads it and retries.
        var first = await a.Client.PostAsJsonAsync("/api/words", new { senseId });
        var retry = await a.Client.PostAsJsonAsync("/api/words", new { senseId });
        output.WriteLine($"first={(int)first.StatusCode} retry={(int)retry.StatusCode} body={await retry.Content.ReadAsStringAsync()}");

        Assert.True(first.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        Assert.Contains("WORD_ALREADY_ADDED", await retry.Content.ReadAsStringAsync());
        await using var context = Db.CreateContext();
        Assert.Equal(1, await context.Words.CountAsync(w => w.UserId == a.Id));
    }
}
