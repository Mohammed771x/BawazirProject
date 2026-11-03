using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WordOs.Application.Abstractions;

namespace WordOs.Api.Tests;

/// <summary>
/// WordOS AI (ADR-133): the backend runs the tools the model names, only
/// those, keeps the conversation, never hands the model a learner's identity,
/// and says so when the model is not there.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AdminAgentTests(PostgresFixture db) : IAsyncLifetime
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
    private StubAdminAgentService Agent => _factory!.Agent;

    private async Task<string> TokenAsync(string prefix, string role = "User")
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@test.dev";
        (await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email, password = "correct-horse-battery", displayName = prefix,
            phoneCountryCode = "967", phoneNumber = "770000009",
        })).EnsureSuccessStatusCode();
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

    private async Task<JsonElement> SendAsync(string text, Guid? conversationId = null)
    {
        var response = await Client.PostAsJsonAsync("/api/admin/intel/ai/messages", new { text, conversationId });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [SkippableFact]
    public async Task A_data_question_runs_the_tool_the_model_named_and_shows_where_the_number_came_from()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        As(await TokenAsync("agent-owner", "Owner"));
        Agent.Script.Enqueue(StubAdminAgentService.Tools(("metrics", """{"keys":["active_learners"],"days":7}""")));
        Agent.Script.Enqueue(StubAdminAgentService.Answer("نشط 3 متعلمين خلال آخر 7 أيام.", "النشطون هذا الأسبوع"));

        var result = await SendAsync("كم عدد المستخدمين النشطين خلال آخر 7 أيام؟");
        var reply = result.GetProperty("reply");
        Assert.False(reply.GetProperty("failed").GetBoolean());
        var source = reply.GetProperty("sources")[0];
        Assert.Equal("metrics", source.GetProperty("tool").GetString());
        Assert.False(string.IsNullOrEmpty(source.GetProperty("meta").GetProperty("from").GetString()));
        Assert.Equal("النشطون هذا الأسبوع", result.GetProperty("conversation").GetProperty("title").GetString());

        // The second step read the tool's result, and nothing else was queried.
        var second = Agent.Requests[^1];
        Assert.Single(second.Steps);
        Assert.Contains("active_learners", second.Steps[0].Results[0]);
        Assert.Contains("metrics", second.Tools);
    }

    [SkippableFact]
    public async Task Discussing_an_idea_queries_nothing()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        As(await TokenAsync("agent-idea", "Owner"));
        Agent.Script.Enqueue(StubAdminAgentService.Answer("فكرة جيدة إذا قيست بمعدل التلميحات لكل كلمة."));

        var reply = (await SendAsync("أفكر في إضافة تلميحات جديدة إلى Spelling. ما رأيك؟")).GetProperty("reply");
        Assert.Equal(0, reply.GetProperty("sources").GetArrayLength());
        Assert.Single(Agent.Requests);
    }

    [SkippableFact]
    public async Task A_follow_up_carries_the_conversation_and_reuses_a_fresh_result()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        As(await TokenAsync("agent-thread", "Owner"));
        const string args = """{"skill":"reading","days":30,"level":"B1"}""";
        Agent.Script.Enqueue(StubAdminAgentService.Tools(("skill", args)));
        Agent.Script.Enqueue(StubAdminAgentService.Answer("Reading في B1 مستقر."));
        var first = await SendAsync("كيف Reading عند B1 آخر 30 يومًا؟");
        var id = first.GetProperty("conversation").GetProperty("id").GetGuid();

        Agent.Script.Enqueue(StubAdminAgentService.Tools(("skill", args), ("skill", """{"skill":"reading","days":30,"level":"B2"}""")));
        Agent.Script.Enqueue(StubAdminAgentService.Answer("B2 أبطأ من B1."));
        var reply = (await SendAsync("طيب، ماذا عن B2؟", id)).GetProperty("reply");

        var request = Agent.Requests[^2];
        Assert.Equal("كيف Reading عند B1 آخر 30 يومًا؟", request.Conversation[0].Text);
        Assert.Contains("skill(", request.Conversation[1].Data); // the data the last answer rested on
        var sources = reply.GetProperty("sources").EnumerateArray().ToList();
        Assert.True(sources[0].GetProperty("cached").GetBoolean());   // B1 again: reused
        Assert.False(sources[1].GetProperty("cached").GetBoolean());  // B2: queried
    }

    [SkippableFact]
    public async Task Learners_go_to_the_screen_and_never_to_the_model()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var learnerToken = await TokenAsync("agent-learner");
        As(learnerToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.GetAsync("/api/admin/intel/ai/conversations")).StatusCode);

        As(await TokenAsync("agent-people", "Owner"));
        Agent.Script.Enqueue(StubAdminAgentService.Tools(("users", """{"criterion":"joined:7","days":7}""")));
        Agent.Script.Enqueue(StubAdminAgentService.Answer("هؤلاء هم."));
        var reply = (await SendAsync("من هم المستخدمون الجدد؟")).GetProperty("reply");

        // Every name on the admin's screen is absent from what the model read.
        var users = reply.GetProperty("attachments").EnumerateArray().Single(a => a.GetProperty("kind").GetString() == "users");
        var names = users.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("name").GetString()!).ToList();
        Assert.NotEmpty(names);
        var sent = Agent.Requests[^1].Steps[0].Results[0];
        Assert.All(names, n => Assert.DoesNotContain($"\"{n}\"", sent));
        Assert.DoesNotContain("@test.dev", sent);
    }

    [SkippableFact]
    public async Task A_missing_model_an_unknown_tool_and_the_last_round_are_all_handled()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        As(await TokenAsync("agent-edge", "Owner"));

        // The model is down: the answer says so and shows no number.
        var down = (await SendAsync("ما وضع WordOS؟")).GetProperty("reply");
        Assert.True(down.GetProperty("failed").GetBoolean());

        // A tool that does not exist comes back as an error the model can read.
        Agent.Script.Enqueue(StubAdminAgentService.Tools(("sql", """{"query":"drop table users"}""")));
        Agent.Script.Enqueue(StubAdminAgentService.Answer("لا أملك أداة لذلك."));
        var unknown = (await SendAsync("نفذ SQL")).GetProperty("reply");
        Assert.False(string.IsNullOrEmpty(unknown.GetProperty("sources")[0].GetProperty("error").GetString()));
        Assert.Contains("There is no tool", Agent.Requests[^1].Steps[0].Results[0]);

        // A model that keeps asking for tools is told, on the last round, to answer.
        for (var i = 0; i < 4; i++)
            Agent.Script.Enqueue(StubAdminAgentService.Tools(("health", $$"""{"days":{{i + 1}}}""")));
        Agent.Script.Enqueue(StubAdminAgentService.Answer("هذا ما وجدته."));
        await SendAsync("حلل كل شيء");
        Assert.True(Agent.Requests[^1].MustAnswer);
        Assert.False(Agent.Requests[^2].MustAnswer);
    }

    [SkippableFact]
    public async Task An_answer_Gemini_wrote_because_Groq_could_not_says_so_at_the_top()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        As(await TokenAsync("agent-fallback", "Owner"));

        Agent.Script.Enqueue(StubAdminAgentService.Tools(("health", """{"days":7}""")) with { Fallback = "quota" });
        Agent.Script.Enqueue(StubAdminAgentService.Answer("١٢ متعلمًا نشطًا.") with { Fallback = "quota" });
        var reply = (await SendAsync("كم عدد النشطين؟")).GetProperty("reply");
        Assert.False(reply.GetProperty("failed").GetBoolean());
        var content = reply.GetProperty("content").GetString()!;
        Assert.StartsWith("**تنبيه:** انتهت حصة Groq المجانية", content);
        Assert.EndsWith("١٢ متعلمًا نشطًا.", content);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(content, "تنبيه"));

        Agent.Script.Enqueue(StubAdminAgentService.Answer("فكرة جيدة.") with { Fallback = "unavailable" });
        Assert.StartsWith("**تنبيه:** تعذر الوصول إلى Groq",
            (await SendAsync("ما رأيك؟")).GetProperty("reply").GetProperty("content").GetString());

        Agent.Script.Enqueue(StubAdminAgentService.Answer("بدون تنبيه."));
        Assert.Equal("بدون تنبيه.", (await SendAsync("وأمس؟")).GetProperty("reply").GetProperty("content").GetString());
    }

    [SkippableFact]
    public async Task A_used_up_free_quota_says_when_it_is_back_and_stops_the_loop()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        As(await TokenAsync("agent-limit", "Owner"));

        // Mid-question: the first round ran, the second hit the daily ceiling.
        Agent.Script.Enqueue(StubAdminAgentService.Tools(("health", """{"days":7}""")));
        Agent.Script.Enqueue(AgentStepResult.Limited(990));
        Agent.Script.Enqueue(StubAdminAgentService.Answer("لا ينبغي أن يُطلب هذا."));

        var reply = (await SendAsync("كم عدد النشطين؟")).GetProperty("reply");
        Assert.True(reply.GetProperty("failed").GetBoolean());
        Assert.Contains("الحد المجاني", reply.GetProperty("content").GetString());
        Assert.Contains("17 دقيقة", reply.GetProperty("content").GetString());
        Assert.Equal(2, Agent.Requests.Count);

        // A per-minute wait that ran out reads as about a minute.
        Agent.Script.Clear();
        Agent.Script.Enqueue(AgentStepResult.Limited(null));
        var soon = (await SendAsync("وأمس؟")).GetProperty("reply");
        Assert.Contains("دقيقة تقريبًا", soon.GetProperty("content").GetString());
    }

    [SkippableFact]
    public async Task Conversations_are_private_and_an_answer_can_become_an_investigation()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var owner = await TokenAsync("agent-mine", "Owner");
        As(owner);
        Agent.Script.Enqueue(StubAdminAgentService.Tools(("skill", """{"skill":"speaking"}""")));
        Agent.Script.Enqueue(StubAdminAgentService.Answer("الانسحاب من Speaking 30%."));
        var result = await SendAsync("لماذا ينسحبون من Speaking؟");
        var conversationId = result.GetProperty("conversation").GetProperty("id").GetGuid();
        var answerId = result.GetProperty("reply").GetProperty("id").GetGuid();

        var saved = await (await Client.PostAsync($"/api/admin/intel/ai/messages/{answerId}/investigation", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        var inquiry = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/intel/inquiries/{saved.GetProperty("id").GetGuid()}");
        Assert.Equal("لماذا ينسحبون من Speaking؟", inquiry.GetProperty("question").GetString());
        Assert.Equal("الانسحاب من Speaking 30%.", inquiry.GetProperty("summary").GetString());

        // Regenerating keeps a saved answer and adds a new one.
        Agent.Script.Enqueue(StubAdminAgentService.Answer("إجابة أخرى."));
        (await Client.PostAsync($"/api/admin/intel/ai/conversations/{conversationId}/regenerate", null)).EnsureSuccessStatusCode();
        var thread = await Client.GetFromJsonAsync<JsonElement>($"/api/admin/intel/ai/conversations/{conversationId}");
        Assert.Equal(3, thread.GetProperty("messages").GetArrayLength());

        // Rename, search, archive.
        (await Client.PatchAsJsonAsync($"/api/admin/intel/ai/conversations/{conversationId}", new { title = "Speaking" })).EnsureSuccessStatusCode();
        var found = await Client.GetFromJsonAsync<JsonElement>("/api/admin/intel/ai/conversations?q=ينسحبون");
        Assert.Contains(found.GetProperty("items").EnumerateArray(), c => c.GetProperty("id").GetGuid() == conversationId);

        // Another admin cannot open it.
        As(await TokenAsync("agent-other", "Analyst"));
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/api/admin/intel/ai/conversations/{conversationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.DeleteAsync($"/api/admin/intel/ai/conversations/{conversationId}")).StatusCode);

        As(owner);
        (await Client.DeleteAsync($"/api/admin/intel/ai/conversations/{conversationId}")).EnsureSuccessStatusCode();
        // The investigation outlives the chat it came from.
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/api/admin/intel/inquiries/{saved.GetProperty("id").GetGuid()}")).StatusCode);
    }

    [SkippableFact]
    public async Task A_past_decision_is_evaluated_through_the_conversation()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        As(await TokenAsync("agent-decision", "Owner"));
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var created = await Client.PostAsJsonAsync("/api/admin/intel/decisions", new
        {
            title = "Speaking first conversation shorter", area = "speaking", problem = "abandonment", action = "three turns",
            metrics = new[] { new { key = "speaking.abandonment_rate", direction = "down" } },
        });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await Client.PostAsJsonAsync($"/api/admin/intel/decisions/{id}/transition", new { action = "approve" })).EnsureSuccessStatusCode();
        (await Client.PostAsJsonAsync($"/api/admin/intel/decisions/{id}/transition", new { action = "implement", on = today.AddDays(-7) })).EnsureSuccessStatusCode();

        Agent.Script.Enqueue(StubAdminAgentService.Tools(("decisions", """{"query":"Speaking first"}""")));
        Agent.Script.Enqueue(StubAdminAgentService.Tools(("decisions", $$"""{"id":"{{id}}"}""")));
        Agent.Script.Enqueue(StubAdminAgentService.Answer("التقييم غير حاسم: العينة صغيرة."));
        await SendAsync("هل نجح قرار تحسين Speaking؟");

        var steps = Agent.Requests[^1].Steps;
        Assert.Contains(id.ToString(), steps[0].Results[0]);
        Assert.Contains("speaking.abandonment_rate", steps[1].Results[0]);
        Assert.Contains("\"evaluationKind\":\"live\"", steps[1].Results[0]);
    }

    [SkippableFact]
    public async Task An_empty_answer_is_asked_for_once_more_before_giving_up()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        As(await TokenAsync("agent-retry", "Owner"));
        Agent.Script.Enqueue(StubAdminAgentService.Answer(""));
        Agent.Script.Enqueue(StubAdminAgentService.Answer("الإجابة في المحاولة الثانية."));

        var reply = (await SendAsync("لماذا؟")).GetProperty("reply");
        Assert.Equal("الإجابة في المحاولة الثانية.", reply.GetProperty("content").GetString());
        Assert.True(Agent.Requests[^1].MustAnswer);
        Assert.False(Agent.Requests[^2].MustAnswer);
    }

    [Fact]
    public void Every_tool_is_described_and_the_cache_key_ignores_argument_order()
    {
        Assert.All(WordOs.Api.Admin.AgentTools.Catalogue, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        Assert.Equal(
            WordOs.Api.Admin.AgentTools.CacheKey("skill", """{"skill":"reading","days":30}"""),
            WordOs.Api.Admin.AgentTools.CacheKey("skill", """{"days":30,"Skill":"reading"}"""));
    }
}
