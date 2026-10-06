using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WordOs.Application.Abstractions;
using WordOs.Domain.Analytics;
using WordOs.Domain.Common;
using WordOs.Domain.Lexicon;

namespace WordOs.Api.Tests;

/// <summary>
/// The admin website's API (ADR-125): who may read it, what it records, and
/// that its figures come from rows the test itself created.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AdminIntelTests(PostgresFixture db) : IAsyncLifetime
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

    private async Task<(Guid Id, string Email)> RegisterAsync(string prefix, string role = "User")
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@test.dev";
        var register = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = "correct-horse-battery",
            displayName = prefix,
            phoneCountryCode = "967",
            phoneNumber = "770000009",
        });
        register.EnsureSuccessStatusCode();
        var id = (await register.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("user").GetProperty("id").GetGuid();

        if (role != "User")
        {
            await using var context = db.CreateContext();
            await context.Database.ExecuteSqlAsync(
                $"""UPDATE users SET "Role" = {role} WHERE "Email" = {email}""");
        }

        // Signing in again is what puts the role in the token.
        var login = await Client.PostAsJsonAsync("/api/auth/login", new { email, password = "correct-horse-battery" });
        login.EnsureSuccessStatusCode();
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());

        return (id, email);
    }

    private async Task<Guid> AddWordAsync(string text, string meaning)
    {
        var senseId = $"ai-{Guid.NewGuid():N}";
        await using (var context = db.CreateContext())
        {
            context.LexiconEntries.Add(LexiconEntry.Create(
                senseId, text, text, "n", $"the meaning of {text}", meaning,
                CefrLevel.B1, 1, "en=oewn;ar=awn", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        var response = await Client.PostAsJsonAsync("/api/words", new { senseId });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    // ── Who may read it ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_learner_is_refused_and_an_owner_is_answered()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        await RegisterAsync("intel-learner");
        foreach (var path in new[] { "overview", "learning", "behavior", "retention", "users", "feedback", "inquiries", "system" })
        {
            var refused = await Client.GetAsync($"/api/admin/intel/{path}");
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        await RegisterAsync("intel-owner", "Owner");
        foreach (var path in new[] { "meta", "overview?days=7", "learning", "behavior", "retention", "users", "users/attention", "feedback", "inquiries", "system", "audit" })
        {
            var answered = await Client.GetAsync($"/api/admin/intel/{path}");
            Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        }
    }

    [SkippableFact]
    public async Task An_analyst_reads_every_figure_but_no_learners_contact_details()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var (learnerId, learnerEmail) = await RegisterAsync("contact-learner");
        await RegisterAsync("intel-analyst", "Analyst");

        var users = await Client.GetFromJsonAsync<JsonElement>("/api/admin/intel/users?q=contact-learner");
        var row = users.GetProperty("rows").EnumerateArray().First(r => r.GetProperty("id").GetGuid() == learnerId);
        Assert.NotEqual(learnerEmail, row.GetProperty("email").GetString());
        Assert.Contains("•", row.GetProperty("email").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("phone").ValueKind);

        // Searching by an address they cannot see would confirm it a letter at a time.
        var byEmail = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/intel/users?q={learnerEmail}");
        Assert.Equal(0, byEmail.GetProperty("total").GetInt32());

        // Who looked at whom is the Owner's alone.
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.GetAsync("/api/admin/intel/audit")).StatusCode);
        // The learner-changing tool is not on the analytics surface at all.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client.PostAsJsonAsync($"/api/admin/users/{learnerId}/advance-schedule", new { days = 2 })).StatusCode);
    }

    [SkippableFact]
    public async Task Opening_a_learner_is_written_to_the_audit_trail()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var (learnerId, _) = await RegisterAsync("audited-learner");
        var (ownerId, _) = await RegisterAsync("auditing-owner", "Owner");

        var user = await Client.GetAsync($"/api/admin/intel/users/{learnerId}");
        Assert.Equal(HttpStatusCode.OK, user.StatusCode);

        await using var context = db.CreateContext();
        Assert.True(await context.AdminAuditEvents.AnyAsync(a =>
            a.ActorId == ownerId && a.Action == "user.viewed" && a.TargetId == learnerId.ToString()));
    }

    // ── What it records ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_phone_may_send_its_own_events_and_nothing_else()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var (learnerId, _) = await RegisterAsync("events-learner");

        var response = await Client.PostAsJsonAsync("/api/events", new
        {
            appVersion = "1.3.0",
            platform = "ios",
            events = new object[]
            {
                new { name = "translation_opened", skill = "READING", props = new { word = "garden", isTarget = false, nested = new { a = 1 } } },
                new { name = "audio_replayed", skill = "LISTENING", durationMs = 1200 },
                // Only the server may write these, and nobody may invent names.
                new { name = "answer_submitted", result = "pass" },
                new { name = "made_up_event" },
            },
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("accepted").GetInt32());
        Assert.Equal(2, body.GetProperty("rejected").GetInt32());

        await using var context = db.CreateContext();
        var stored = await context.AnalyticsEvents.Where(e => e.UserId == learnerId).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, e => Assert.Equal(AnalyticsSource.Client, e.Source));
        var translation = stored.Single(e => e.Name == "translation_opened");
        Assert.Equal(SkillType.Reading, translation.Skill);
        Assert.Equal("1.3.0", translation.AppVersion);
        // A flat map of scalars survives; anything nested is dropped.
        Assert.Contains("garden", translation.PropsJson);
        Assert.DoesNotContain("nested", translation.PropsJson);
    }

    [SkippableFact]
    public async Task Too_many_events_at_once_are_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        await RegisterAsync("batch-learner");
        var events = Enumerable.Range(0, 51).Select(_ => new { name = "app_opened" }).ToArray();
        var response = await Client.PostAsJsonAsync("/api/events", new { events });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Every_attempt_is_recorded_with_its_time_and_an_abandoned_session_leaves_a_trace()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var (learnerId, _) = await RegisterAsync("attempt-learner");
        await AddWordAsync("garden", "حديقة");

        var start = await Client.PostAsync("/api/sessions/reading/start", null);
        start.EnsureSuccessStatusCode();
        var session = await start.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetGuid();
        var itemId = session.GetProperty("progress").GetProperty("nextItemId").GetGuid();

        // A wrong answer, then — the item requeued — the session walked away from.
        var answer = await Client.PostAsJsonAsync($"/api/sessions/{sessionId}/answer",
            new { itemId, answer = "definitely wrong", elapsedMs = 8400 });
        answer.EnsureSuccessStatusCode();
        (await Client.PostAsync($"/api/sessions/{sessionId}/abandon", null)).EnsureSuccessStatusCode();

        await using var context = db.CreateContext();
        var events = await context.AnalyticsEvents.Where(e => e.UserId == learnerId).ToListAsync();

        var attempt = events.Single(e => e.Name == AnalyticsEventNames.AnswerSubmitted);
        Assert.Equal("fail", attempt.Result);
        Assert.Equal(1, attempt.Attempt);
        Assert.Equal(8400, attempt.DurationMs);
        Assert.Equal(sessionId, attempt.SessionId);
        Assert.Equal(AnalyticsSource.Server, attempt.Source);

        // The session row is gone; the fact that it was abandoned is not.
        Assert.False(await context.SkillSessions.AnyAsync(s => s.Id == sessionId));
        var abandoned = events.Single(e => e.Name == AnalyticsEventNames.SessionAbandoned);
        Assert.Equal(SkillType.Reading, abandoned.Skill);
        // jsonb stores its own spacing.
        Assert.Contains("\"answered\": 1", abandoned.PropsJson);
    }

    [SkippableFact]
    public async Task Feedback_carries_its_topic_and_takes_internal_notes()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        await RegisterAsync("topic-learner");
        (await Client.PostAsJsonAsync("/api/feedback", new { body = "the audio is too fast", category = "LISTENING" }))
            .EnsureSuccessStatusCode();
        // An old build's unknown label must not cost the report.
        (await Client.PostAsJsonAsync("/api/feedback", new { body = "something odd", category = "NOT_A_TOPIC" }))
            .EnsureSuccessStatusCode();

        await RegisterAsync("notes-owner", "Owner");
        var page = await Client.GetFromJsonAsync<JsonElement>("/api/admin/intel/feedback?category=LISTENING");
        var item = page.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("body").GetString() == "the audio is too fast");
        Assert.Equal("LISTENING", item.GetProperty("category").GetString());

        var id = item.GetProperty("id").GetGuid();
        (await Client.PostAsJsonAsync($"/api/admin/intel/feedback/{id}/notes", new { body = "Investigating Listening speed at B1." }))
            .EnsureSuccessStatusCode();
        (await Client.PatchAsJsonAsync($"/api/admin/intel/feedback/{id}", new { handled = true })).EnsureSuccessStatusCode();

        var after = await Client.GetFromJsonAsync<JsonElement>("/api/admin/intel/feedback?status=HANDLED");
        var handled = after.GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetGuid() == id);
        Assert.Equal("Investigating Listening speed at B1.",
            handled.GetProperty("notes")[0].GetProperty("body").GetString());
    }

    // ── What it computes ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_learners_failure_reason_is_counted_from_the_evaluation_it_recorded()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var (learnerId, _) = await RegisterAsync("reason-learner");
        var wordId = await AddWordAsync("garden", "حديقة");

        // A Writing failure, as the server records one: the evaluator said the
        // word was used but the meaning was wrong. Written directly because
        // reaching Writing honestly takes three spaced gaps.
        await using (var context = db.CreateContext())
        {
            var now = DateTimeOffset.UtcNow;
            context.AnalyticsEvents.Add(AnalyticsEvent.Create(learnerId, AnalyticsEventNames.WritingEvaluated,
                AnalyticsSource.Server, now, now, wordId: wordId, skill: SkillType.Writing, attempt: 1, result: "fail",
                propsJson: """{"usedWord":true,"meaningCorrect":false,"usageCorrect":false,"understandable":true}"""));
            await context.SaveChangesAsync();
        }

        await RegisterAsync("reason-owner", "Owner");
        var learning = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/intel/learning?days=7&userId={learnerId}");
        var writing = learning.GetProperty("failures").EnumerateArray().First(f => f.GetProperty("skill").GetString() == "WRITING");
        var reason = writing.GetProperty("reasons").EnumerateArray().Single();
        Assert.Equal("wrong_meaning", reason.GetProperty("key").GetString());
        Assert.Equal("learning", reason.GetProperty("category").GetString());
        Assert.Equal(1, reason.GetProperty("count").GetInt32());
    }

    [SkippableFact]
    public async Task User_360_and_the_timeline_show_what_the_learner_did()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var (learnerId, _) = await RegisterAsync("timeline-learner");
        await AddWordAsync("garden", "حديقة");
        (await Client.PostAsJsonAsync("/api/events", new { events = new[] { new { name = "app_opened" } } })).EnsureSuccessStatusCode();

        await RegisterAsync("timeline-owner", "Owner");
        var user = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/intel/users/{learnerId}");
        Assert.Equal(1, user.GetProperty("states").GetProperty("total").GetInt32());
        Assert.Equal("garden", user.GetProperty("words")[0].GetProperty("text").GetString());
        Assert.Contains("wa.me/967", user.GetProperty("header").GetProperty("whatsapp").GetString());

        var timeline = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/intel/users/{learnerId}/timeline");
        var kinds = timeline.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("kind").GetString()).ToList();
        Assert.Contains("signup", kinds);
        Assert.Contains("word_added", kinds);
        Assert.Contains("app_opened", kinds);
    }

    [SkippableFact]
    public async Task An_investigation_answers_without_the_model_and_is_saved_as_shown()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        await RegisterAsync("inquiry-owner", "Owner");
        _factory!.Insight.Next = null; // the model is down

        var response = await Client.PostAsJsonAsync("/api/admin/intel/inquiries", new
        {
            section = "speaking",
            question = "لماذا ينسحب المستخدمون من Speaking؟",
            filter = new { days = 30 },
        });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rules", result.GetProperty("interpretedBy").GetString());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("summary").GetString()));
        Assert.True(result.GetProperty("evidence").GetArrayLength() > 0);

        var id = result.GetProperty("id").GetGuid();
        var saved = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/intel/inquiries/{id}");
        Assert.Equal(result.GetProperty("summary").GetString(), saved.GetProperty("summary").GetString());

        // With the model up, its reading replaces the rules' — and it was sent
        // aggregates, not anyone's identity.
        _factory.Insight.Next = new InsightResponse("قراءة الـAI", ["تفسير"], ["فرضية"],
            [new InsightLead("افحص", "لأن")], [], "test-model", "admin-insight-v1");
        var withAi = await (await Client.PostAsJsonAsync("/api/admin/intel/inquiries", new
        {
            section = "ux",
            question = "أين يحتك المستخدمون؟",
        })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ai", withAi.GetProperty("interpretedBy").GetString());
        Assert.Equal("قراءة الـAI", withAi.GetProperty("summary").GetString());
        Assert.DoesNotContain("@test.dev", _factory.Insight.LastRequest!.EvidenceJson);

        // An unknown section is refused rather than guessed.
        var bad = await Client.PostAsJsonAsync("/api/admin/intel/inquiries", new { section = "nope", question = "why?" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
