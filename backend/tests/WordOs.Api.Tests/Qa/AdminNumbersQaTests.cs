using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using Xunit.Abstractions;

namespace WordOs.Api.Tests.Qa;

/// <summary>
/// QA prompt 11: a dataset small enough to count by hand, scoped by a unique
/// interest tag so other tests' learners in the same database do not leak into
/// the figures. Expected values are written as literals from the definitions in
/// IntelMeasures — not computed by calling production code.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AdminNumbersQaTests(PostgresFixture db, ITestOutputHelper output) : QaApiBase(db)
{
    private async Task TagAsync(Learner l, string tag)
    {
        var r = await l.Client.PutAsJsonAsync("/api/me/interests", new { interests = new[] { tag } });
        r.EnsureSuccessStatusCode();
    }

    private async Task<string> CorrectAsync(Guid itemId)
    {
        await using var c = Db.CreateContext();
        return (await c.SessionItems.FirstAsync(i => i.Id == itemId)).CorrectAnswer;
    }

    /// <summary>Plays a reading session; <paramref name="missWords"/> answers target words wrong once first.</summary>
    private async Task PlayReadingAsync(Learner l, bool missWords)
    {
        var start = await JsonAsync(await l.Client.PostAsync("/api/sessions/reading/start", null));
        var id = start.GetProperty("id").GetGuid();
        var missed = new HashSet<Guid>();
        while (true)
        {
            var s = await l.Client.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}");
            var next = s.GetProperty("progress").GetProperty("nextItemId");
            if (next.ValueKind == JsonValueKind.Null) break;
            var itemId = next.GetGuid();
            var item = s.GetProperty("items").EnumerateArray().First(i => i.GetProperty("id").GetGuid() == itemId);
            var correct = await CorrectAsync(itemId);
            var isWord = item.TryGetProperty("wordId", out var w) && w.ValueKind != JsonValueKind.Null;
            var answer = correct;
            if (missWords && isWord && missed.Add(itemId))
                answer = item.GetProperty("options").EnumerateArray().Select(o => o.GetString()!).First(o => o != correct);
            (await l.Client.PostAsJsonAsync($"/api/sessions/{id}/answer", new { itemId, answer })).EnsureSuccessStatusCode();
        }
        (await l.Client.PostAsync($"/api/sessions/{id}/complete", null)).EnsureSuccessStatusCode();
    }

    private async Task<JsonElement> MetricsAsync(Learner owner, string tag, params string[] keys)
    {
        var args = JsonSerializer.Serialize(new { keys, days = 30, interest = tag });
        Root!.Agent.Script.Enqueue(StubAdminAgentService.Tools(("metrics", args)));
        Root!.Agent.Script.Enqueue(StubAdminAgentService.Answer("ok", "qa"));
        var r = await owner.Client.PostAsJsonAsync("/api/admin/intel/ai/messages", new { text = "qa metrics", conversationId = (Guid?)null });
        r.EnsureSuccessStatusCode();
        var fed = Root!.Agent.Requests[^1].Steps[^1].Results[0];
        output.WriteLine($"metrics tool result: {fed}");
        return JsonDocument.Parse(fed).RootElement;
    }

    private static JsonElement Find(JsonElement root, string key)
    {
        // The tool's shape is not part of a public contract; search for the key.
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in root.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("key", out var k) && k.GetString() == key) return p.Value;
                if (p.Name == key) return p.Value;
                var inner = Find(p.Value, key);
                if (inner.ValueKind != JsonValueKind.Undefined) return inner;
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in root.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("key", out var k) && k.GetString() == key) return e;
                var inner = Find(e, key);
                if (inner.ValueKind != JsonValueKind.Undefined) return inner;
            }
        }
        return default;
    }

    [SkippableFact]
    public async Task A01_hand_counted_dataset_matches_the_dictionary_metrics()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var tag = $"qa-cohort-{Guid.NewGuid():N}"[..20];

        var l1 = await RegisterAsync(); await TagAsync(l1, tag);
        var l2 = await RegisterAsync(); await TagAsync(l2, tag);
        var l3 = await RegisterAsync(); await TagAsync(l3, tag);
        var owner = await RegisterWithRoleAsync("Owner"); await TagAsync(owner, tag);
        var analyst = await RegisterWithRoleAsync("Analyst"); await TagAsync(analyst, tag);

        // L1: three words, one deleted, a clean reading session.
        await AddWordAsync(l1, "harbour", "ميناء");
        await AddWordAsync(l1, "lantern", "فانوس");
        var gone = await AddWordAsync(l1, "anchor", "مرساة");
        (await l1.Client.DeleteAsync($"/api/words/{gone}")).EnsureSuccessStatusCode();
        await PlayReadingAsync(l1, missWords: false);

        // L2: one word, missed on the first attempt then answered right.
        await AddWordAsync(l2, "pebble", "حصاة");
        await PlayReadingAsync(l2, missWords: true);

        // Owner and Analyst add words and play: they must not count (ADR-052).
        await AddWordAsync(owner, "ownerword", "كلمة");
        await PlayReadingAsync(owner, missWords: false);
        await AddWordAsync(analyst, "analystword", "كلمة");

        var m = await MetricsAsync(owner, tag,
            "active_learners", "learning_learners", "sessions", "words_added",
            "words_entered_active", "review_first_pass", "ai_fallback_rate");

        double? V(string key)
        {
            var e = Find(m, key);
            Assert.True(e.ValueKind != JsonValueKind.Undefined, $"metric {key} missing from tool output");
            var v = e.TryGetProperty("value", out var x) ? x : e;
            return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        }

        // Hand counts (definitions quoted in the report):
        Assert.Equal(3, V("active_learners"));      // L1, L2, L3 (registration is activity); not Owner/Analyst
        Assert.Equal(2, V("learning_learners"));    // L1 and L2 started a skill session
        Assert.Equal(2, V("sessions"));             // two non-practice reading sessions
        Assert.Equal(4, V("words_added"));          // 3 (incl. deleted) + 1
        Assert.Equal(0, V("words_entered_active"));
        Assert.Null(V("review_first_pass"));        // zero denominator is "no data", not 0 %
        Assert.Equal(0, V("ai_fallback_rate"));

        // Reading in depth: 3 target words decided, 2 first-attempt correct.
        await using var context = Db.CreateContext();
        var ids = new[] { l1.Id, l2.Id };
        var items = await context.SessionItems
            .Join(context.SkillSessions, i => i.SessionId, s => s.Id, (i, s) => new { i, s })
            .Where(x => ids.Contains(x.s.UserId) && x.i.WordId != null)
            .Select(x => x.i).ToListAsync();
        output.WriteLine($"DB: target-word items={items.Count}, first-attempt correct={items.Count(i => i.FirstAttemptCorrect == true)}");
        Assert.Equal(3, items.Count);
        Assert.Equal(2, items.Count(i => i.FirstAttemptCorrect == true));
    }

    [SkippableFact]
    public async Task A02_decision_workflow_is_owner_gated_and_validates_metrics()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var owner = await RegisterWithRoleAsync("Owner");
        var analyst = await RegisterWithRoleAsync("Analyst");

        object Proposal(string metric) => new
        {
            title = "QA decision", area = "general", problem = "learners miss words",
            action = "change the hint ladder", metrics = new[] { new { key = metric, direction = "up" } },
        };

        var unknown = await analyst.Client.PostAsJsonAsync("/api/admin/intel/decisions", Proposal("not_a_metric"));
        output.WriteLine($"unknown metric: {(int)unknown.StatusCode} {await unknown.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var proposed = await analyst.Client.PostAsJsonAsync("/api/admin/intel/decisions", Proposal("sessions"));
        output.WriteLine($"analyst proposes: {(int)proposed.StatusCode}");
        proposed.EnsureSuccessStatusCode();
        var id = (await JsonAsync(proposed)).GetProperty("id").GetGuid();

        async Task<HttpResponseMessage> Act(Learner who, string action, object? extra = null) =>
            await who.Client.PostAsJsonAsync($"/api/admin/intel/decisions/{id}/transition",
                extra ?? new { action });

        var analystApprove = await Act(analyst, "approve");
        var closeBeforeApprove = await Act(owner, "close", new { action = "close", verdict = "Success" });
        var bogus = await Act(owner, "teleport");
        var ownerApprove = await Act(owner, "approve");
        var approveTwice = await Act(owner, "approve");
        output.WriteLine($"analyst approve={(int)analystApprove.StatusCode} close-before-approve={(int)closeBeforeApprove.StatusCode} " +
                         $"unknown-action={(int)bogus.StatusCode} owner approve={(int)ownerApprove.StatusCode} approve-twice={(int)approveTwice.StatusCode}");

        Assert.Equal(HttpStatusCode.Forbidden, analystApprove.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);
        Assert.True(ownerApprove.IsSuccessStatusCode);
        Assert.True((int)approveTwice.StatusCode is >= 400 and < 500, "approving twice should be refused");
        Assert.True((int)closeBeforeApprove.StatusCode < 500);

        var audit = await owner.Client.GetStringAsync("/api/admin/intel/audit");
        Assert.Contains(id.ToString(), audit);
    }

    [SkippableFact]
    public async Task A03_event_ingest_accepts_own_ids_only_and_bounds_input()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var b = await RegisterAsync();
        var bWord = await AddWordAsync(b, "pebble", "حصاة");
        var now = DateTimeOffset.UtcNow;

        var batch = new
        {
            events = new object[]
            {
                new { name = "hint_used", at = now, screen = "spelling" },
                new { name = "hint_used", at = now, wordId = bWord },               // someone else's word
                new { name = "not_on_the_list", at = now },
                new { name = "app_opened", at = now.AddYears(5) },                   // far future
                new { name = "app_opened", at = now, props = new { note = new string('x', 5000) } },
            },
        };
        var r = await a.Client.PostAsJsonAsync("/api/events", batch);
        var body = await r.Content.ReadAsStringAsync();
        output.WriteLine($"ingest: {(int)r.StatusCode} {body}");
        Assert.True((int)r.StatusCode < 500);

        await using var context = Db.CreateContext();
        var stored = await context.AnalyticsEvents.Where(e => e.UserId == a.Id).ToListAsync();
        output.WriteLine($"stored for A: {stored.Count} -> {string.Join(", ", stored.Select(e => $"{e.Name}@{e.OccurredAt:yyyy-MM-dd} word={e.WordId}"))}");
        Assert.DoesNotContain(stored, e => e.WordId == bWord);
        Assert.DoesNotContain(stored, e => e.Name == "not_on_the_list");
        Assert.DoesNotContain(stored, e => e.OccurredAt > now.AddDays(1));
    }
}
