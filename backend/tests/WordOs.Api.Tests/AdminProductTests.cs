using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WordOs.Api.Admin;
using WordOs.Domain.Analytics;

namespace WordOs.Api.Tests;

/// <summary>
/// Decide → Implement → Measure → Learn (ADR-132): who may approve, what is
/// kept, what an evaluation says about itself, and the investigator's threads.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AdminProductTests(PostgresFixture db) : IAsyncLifetime
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

    private async Task<string> TokenAsync(string prefix, string role = "User")
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@test.dev";
        var register = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email, password = "correct-horse-battery", displayName = prefix,
            phoneCountryCode = "967", phoneNumber = "770000009",
        });
        register.EnsureSuccessStatusCode();
        if (role != "User")
        {
            await using var context = db.CreateContext();
            await context.Database.ExecuteSqlAsync($"""UPDATE users SET "Role" = {role} WHERE "Email" = {email}""");
        }
        var login = await Client.PostAsJsonAsync("/api/auth/login", new { email, password = "correct-horse-battery" });
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private void As(string token) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static object Decision(string title, params object[] metrics) => new
    {
        title, area = "speaking",
        problem = "الانسحاب من Speaking مرتفع",
        action = "تقصير المحادثة الأولى",
        hypothesis = "المحادثة الأولى طويلة للمبتدئين",
        metrics,
        reviewOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14)),
    };

    [SkippableFact]
    public async Task A_proposal_is_not_a_decision_until_the_owner_approves_it()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var learner = await TokenAsync("decision-learner");
        var analyst = await TokenAsync("decision-analyst", "Analyst");
        var owner = await TokenAsync("decision-owner", "Owner");

        As(learner);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.GetAsync("/api/admin/intel/decisions")).StatusCode);

        As(analyst);
        var created = await Client.PostAsJsonAsync("/api/admin/intel/decisions",
            Decision("Shorter first conversation", new { key = "speaking.abandonment_rate", direction = "down", target = 0.2 }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // An analyst writes proposals; approving one is the Owner's.
        var approveByAnalyst = await Client.PostAsJsonAsync($"/api/admin/intel/decisions/{id}/transition", new { action = "approve" });
        Assert.Equal(HttpStatusCode.Forbidden, approveByAnalyst.StatusCode);

        As(owner);
        (await Client.PostAsJsonAsync($"/api/admin/intel/decisions/{id}/transition", new { action = "approve" })).EnsureSuccessStatusCode();

        // What an approved decision promises is no longer the analyst's to rewrite.
        As(analyst);
        var edit = await Client.PutAsJsonAsync($"/api/admin/intel/decisions/{id}", Decision("Rewritten"));
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);

        // Anyone may record that it shipped; the evaluation then has an "after".
        var shipped = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2));
        (await Client.PostAsJsonAsync($"/api/admin/intel/decisions/{id}/transition",
            new { action = "implement", on = shipped })).EnsureSuccessStatusCode();

        var detail = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/intel/decisions/{id}");
        Assert.Equal("IMPLEMENTED", detail.GetProperty("decision").GetProperty("status").GetString());
        var evaluation = detail.GetProperty("evaluation");
        Assert.Equal(shipped.ToString("yyyy-MM-dd"), evaluation.GetProperty("after").GetProperty("from").GetString());
        Assert.Equal("speaking.abandonment_rate", evaluation.GetProperty("targeted")[0].GetProperty("key").GetString());
        Assert.True(evaluation.GetProperty("guardrails").GetArrayLength() > 0);
        var cautions = evaluation.GetProperty("cautions").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains(cautions, c => c!.Contains("مجموعة مقارنة"));   // time, not cause
        Assert.Contains(cautions, c => c!.Contains("لم تكتمل"));        // review is two weeks out

        // Closing needs a verdict a person wrote, and freezes the measurement.
        As(owner);
        var noVerdict = await Client.PostAsJsonAsync($"/api/admin/intel/decisions/{id}/transition", new { action = "close" });
        Assert.Equal(HttpStatusCode.BadRequest, noVerdict.StatusCode);
        (await Client.PostAsJsonAsync($"/api/admin/intel/decisions/{id}/transition",
            new { action = "close", verdict = "inconclusive", note = "عينة صغيرة" })).EnsureSuccessStatusCode();

        var closed = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/intel/decisions/{id}");
        Assert.Equal("INCONCLUSIVE", closed.GetProperty("decision").GetProperty("verdict").GetString());
        Assert.Contains("عند إغلاق", closed.GetProperty("evaluationNote").GetString());
        var history = closed.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("action").GetString()).ToList();
        Assert.Contains("decision.proposed", history);
        Assert.Contains("decision.approve", history);
        Assert.Contains("decision.close", history);

        var reopened = await Client.PutAsJsonAsync($"/api/admin/intel/decisions/{id}", Decision("After the fact"));
        Assert.Equal(HttpStatusCode.Conflict, reopened.StatusCode);
    }

    [SkippableFact]
    public async Task A_metric_or_area_outside_the_dictionary_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        As(await TokenAsync("dictionary-owner", "Owner"));

        var unknownMetric = await Client.PostAsJsonAsync("/api/admin/intel/decisions",
            Decision("x metric", new { key = "happiness", direction = "up" }));
        Assert.Equal(HttpStatusCode.BadRequest, unknownMetric.StatusCode);

        var definitions = await Client.GetFromJsonAsync<JsonElement>("/api/admin/intel/definitions");
        var keys = definitions.GetProperty("metrics").EnumerateArray().Select(m => m.GetProperty("key").GetString()).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(definitions.GetProperty("metrics").EnumerateArray(),
            m => Assert.False(string.IsNullOrWhiteSpace(m.GetProperty("formula").GetString())));
        Assert.Contains(definitions.GetProperty("brief").EnumerateArray(),
            b => b.GetProperty("requested").GetString() == "notification_sent" && b.GetProperty("status").GetString() == "partial");
    }

    [SkippableFact]
    public async Task An_experiment_compares_against_its_group_and_overlaps_are_named()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        As(await TokenAsync("change-owner", "Owner"));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var experiment = await Client.PostAsJsonAsync("/api/admin/intel/changes", new
        {
            title = "Translation tap shows example", kind = "translation", isExperiment = true,
            description = "B1 learners see an example sentence", shippedOn = today.AddDays(-3),
            segment = "level:B1", comparisonSegment = "level:A2",
            metrics = new[] { new { key = "reading.first_attempt_accuracy", direction = "up" } },
            windowDays = 7,
        });
        Assert.Equal(HttpStatusCode.Created, experiment.StatusCode);
        var id = (await experiment.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        (await Client.PostAsJsonAsync("/api/admin/intel/changes", new
        {
            title = "Faster API", kind = "performance", isExperiment = false,
            description = "Caching", shippedOn = today.AddDays(-1),
        })).EnsureSuccessStatusCode();

        var detail = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/intel/changes/{id}");
        var evaluation = detail.GetProperty("evaluation");
        Assert.False(string.IsNullOrEmpty(evaluation.GetProperty("comparisonPopulation").GetString()));
        Assert.DoesNotContain(evaluation.GetProperty("cautions").EnumerateArray(), c => c.GetString()!.Contains("لا توجد مجموعة مقارنة"));
        Assert.Contains(evaluation.GetProperty("overlappingChanges").EnumerateArray(), o => o.GetProperty("title").GetString() == "Faster API");

        var bad = await Client.PostAsJsonAsync("/api/admin/intel/changes", new
        {
            title = "Nope", kind = "vibes", isExperiment = false, description = "x x x", shippedOn = today,
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [SkippableFact]
    public async Task A_follow_up_keeps_its_thread_and_a_discussion_reads_no_data()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        As(await TokenAsync("thread-owner", "Owner"));
        _factory!.Insight.Next = null;

        var first = await (await Client.PostAsJsonAsync("/api/admin/intel/inquiries", new
        {
            section = "reading", question = "كيف أداء Reading في B1؟", filter = new { days = 30, level = "B1" },
        })).Content.ReadFromJsonAsync<JsonElement>();
        var firstId = first.GetProperty("id").GetGuid();

        // No section, no filters: both come from the parent.
        var follow = await Client.PostAsJsonAsync("/api/admin/intel/inquiries", new { question = "والفترة السابقة؟", parentId = firstId });
        follow.EnsureSuccessStatusCode();
        var second = await follow.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("reading", second.GetProperty("section").GetString());
        Assert.Equal(firstId, second.GetProperty("parentId").GetGuid());
        Assert.Equal("B1", second.GetProperty("filter").GetProperty("level").GetString());
        var sent = JsonDocument.Parse(_factory.Insight.LastRequest!.EvidenceJson).RootElement;
        Assert.Equal("كيف أداء Reading في B1؟", sent.GetProperty("conversation")[0].GetProperty("question").GetString());

        var discussion = await (await Client.PostAsJsonAsync("/api/admin/intel/inquiries", new
        {
            section = "discussion", question = "ما الأفكار لتحسين تجربة الترجمة؟",
        })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("discussion", discussion.GetProperty("mode").GetString());
        Assert.Equal(0, discussion.GetProperty("evidence").GetArrayLength());

        // A follow-up date, and asking the same question again over the days since.
        (await Client.PatchAsJsonAsync($"/api/admin/intel/inquiries/{firstId}",
            new { on = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)) })).EnsureSuccessStatusCode();
        var rerun = await (await Client.PostAsync($"/api/admin/intel/inquiries/{firstId}/rerun", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(firstId, rerun.GetProperty("parentId").GetGuid());

        var list = await Client.GetFromJsonAsync<JsonElement>("/api/admin/intel/inquiries");
        var row = list.GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetGuid() == firstId);
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("followUpOn").ValueKind);
    }

    [SkippableFact]
    public async Task A_reminder_plan_is_recorded_once_and_the_journey_says_what_it_cannot_see()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var learner = await TokenAsync("reminder-learner");
        As(learner);
        (await Client.GetAsync("/api/notifications/daily")).EnsureSuccessStatusCode();
        (await Client.GetAsync("/api/notifications/daily")).EnsureSuccessStatusCode();

        await using (var context = db.CreateContext())
        {
            var me = await context.Users.SingleAsync(u => u.Email.StartsWith("reminder-learner") && u.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
            var plans = await context.AnalyticsEvents.CountAsync(e => e.UserId == me.Id && e.Name == AnalyticsEventNames.RemindersPlanned);
            Assert.Equal(1, plans);
        }

        As(await TokenAsync("reminder-owner", "Owner"));
        var page = await Client.GetFromJsonAsync<JsonElement>("/api/admin/intel/notifications?days=7");
        Assert.Equal(4, page.GetProperty("journey").GetArrayLength());
        Assert.Contains(page.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("ADR-076"));
    }

    [SkippableFact]
    public async Task A_decision_past_its_review_date_is_measured_once_and_waits_for_a_verdict()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        As(await TokenAsync("review-owner", "Owner"));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var created = await Client.PostAsJsonAsync("/api/admin/intel/decisions", new
        {
            title = "Reviewed by itself", area = "general", problem = "p p p", action = "a a a",
            metrics = new[] { new { key = "sessions", direction = "up" } },
            reviewOn = today.AddDays(-2),
        });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await Client.PostAsJsonAsync($"/api/admin/intel/decisions/{id}/transition", new { action = "approve" })).EnsureSuccessStatusCode();
        (await Client.PostAsJsonAsync($"/api/admin/intel/decisions/{id}/transition", new { action = "implement", on = today.AddDays(-10) })).EnsureSuccessStatusCode();

        // Opening the log is what notices the date has passed.
        var list = await Client.GetFromJsonAsync<JsonElement>("/api/admin/intel/decisions");
        var row = list.GetProperty("items").EnumerateArray().First(r => r.GetProperty("id").GetGuid() == id);
        Assert.Equal("measured", row.GetProperty("reviewState").GetString());

        // The log's cards filter by group: implemented counts as active, and as due.
        foreach (var group in new[] { "active", "due" })
        {
            var filtered = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/intel/decisions?status={group}");
            Assert.Contains(filtered.GetProperty("items").EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);
        }

        var detail = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/intel/decisions/{id}");
        Assert.Equal(today.AddDays(-2).ToString("yyyy-MM-dd"), detail.GetProperty("review").GetProperty("after").GetProperty("to").GetString());
        Assert.True(detail.GetProperty("review").GetProperty("afterComplete").GetBoolean());
        Assert.Equal("IMPLEMENTED", detail.GetProperty("decision").GetProperty("status").GetString()); // a measurement, not a verdict
        Assert.Single(detail.GetProperty("history").EnumerateArray(), h => h.GetProperty("action").GetString() == "decision.reviewed");

        // Moving the review date throws away a measurement of the old window.
        (await Client.PostAsJsonAsync($"/api/admin/intel/decisions/{id}/transition", new { action = "reschedule", reviewOn = today.AddDays(5) })).EnsureSuccessStatusCode();
        var moved = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/intel/decisions/{id}");
        Assert.Equal(JsonValueKind.Null, moved.GetProperty("review").ValueKind);
    }

    [SkippableFact]
    public async Task A_day_opens_with_its_lessons_words_and_events_and_a_skill_splits_by_content_version()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var learner = await TokenAsync("day-learner");
        As(learner);
        var me = (await Client.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("id").GetGuid();

        As(await TokenAsync("day-owner", "Owner"));
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        var day = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/intel/users/{me}/days/{today:yyyy-MM-dd}");
        Assert.Equal(today.ToString("yyyy-MM-dd"), day.GetProperty("date").GetString());
        Assert.True(day.GetProperty("summary").TryGetProperty("sessions", out _));
        Assert.True(day.GetProperty("events").GetArrayLength() > 0); // signing up is on the timeline

        var bad = await Client.GetAsync($"/api/admin/intel/users/{me}/days/yesterday");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var skill = await Client.GetFromJsonAsync<JsonElement>("/api/admin/intel/skills/reading?days=30");
        Assert.Equal(JsonValueKind.Array, skill.GetProperty("byVersion").ValueKind);
    }

    [Fact]
    public void A_baseline_never_shares_a_day_with_the_after_window()
    {
        var now = DateTimeOffset.UtcNow;
        var d = ProductDecision.Propose(Guid.NewGuid(), Guid.NewGuid(), "t", "speaking", "problem", "action", now);
        var shipped = new DateOnly(2026, 11, 3);
        d.Detail(null, null, null, null, null, null, new DateOnly(2026, 10, 5), shipped, [], null, now);
        d.Approve(Guid.NewGuid(), now);
        d.MarkImplemented(shipped, now);

        var (before, after) = IntelEvaluation.WindowsOf(d, 14)!.Value;
        Assert.Equal(new DateOnly(2026, 11, 2), before.To);
        Assert.Equal(shipped, after.From);
        // No review date: the after window is as long as the baseline.
        Assert.Equal(before.Days, after.Days);
    }

    [Fact]
    public void Every_guardrail_and_metric_key_resolves()
    {
        Assert.All(IntelMeasures.Guardrails, k => Assert.NotNull(IntelMeasures.Find(k)));
        Assert.Equal(IntelMeasures.Catalogue.Count, IntelMeasures.Catalogue.Select(m => m.Key).Distinct().Count());
    }
}
