using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using WordOs.Domain.Common;
using WordOs.Domain.Users;
using Xunit.Abstractions;

namespace WordOs.Api.Tests.Qa;

/// <summary>
/// QA prompt 04: authorization matrix, token tampering, mass assignment and
/// the Analyst's contact masking — over the real pipeline and PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SecurityQaTests(PostgresFixture db, ITestOutputHelper output) : QaApiBase(db)
{
    // ── S01: learner B against learner A's resources ─────────────────────────

    [SkippableFact]
    public async Task S01_another_learners_ids_are_never_reachable()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var b = await RegisterAsync();
        var wordId = await AddWordAsync(a, "harbour", "ميناء");
        var start = await a.Client.PostAsync("/api/sessions/reading/start", null);
        var session = await JsonAsync(start);
        var sessionId = session.GetProperty("id").GetGuid();
        var itemId = session.GetProperty("progress").GetProperty("nextItemId").GetGuid();
        var placement = await JsonAsync(await a.Client.PostAsync("/api/placement/start", null));
        var placementId = placement.TryGetProperty("sessionId", out var p) ? p.GetGuid()
            : placement.GetProperty("id").GetGuid();

        var attempts = new (string Name, Func<Task<HttpResponseMessage>> Send)[]
        {
            ("GET word", () => b.Client.GetAsync($"/api/words/{wordId}")),
            ("DELETE word", () => b.Client.DeleteAsync($"/api/words/{wordId}")),
            ("PATCH meaning", () => b.Client.PatchAsJsonAsync($"/api/words/{wordId}/meaning", new { meaning = "مرفأ" })),
            ("GET session", () => b.Client.GetAsync($"/api/sessions/{sessionId}")),
            ("answer", () => b.Client.PostAsJsonAsync($"/api/sessions/{sessionId}/answer", new { itemId, answer = "x" })),
            ("level", () => b.Client.PostAsJsonAsync($"/api/sessions/{sessionId}/level", new { level = "A1" })),
            ("complete", () => b.Client.PostAsync($"/api/sessions/{sessionId}/complete", null)),
            ("abandon", () => b.Client.PostAsync($"/api/sessions/{sessionId}/abandon", null)),
            ("placement answer", () => b.Client.PostAsJsonAsync($"/api/placement/{placementId}/answer", new { itemId = Guid.NewGuid(), answer = "x" })),
            ("placement complete", () => b.Client.PostAsync($"/api/placement/{placementId}/complete", null)),
        };

        foreach (var (name, send) in attempts)
        {
            var response = await send();
            output.WriteLine($"B -> {name}: {(int)response.StatusCode}");
            // Abandon is idempotent by design: an id that is not the caller's is
            // a no-op 204, exactly like an id already gone. The DB check below
            // is what proves it touched nothing.
            if (name != "abandon")
                Assert.False(response.IsSuccessStatusCode, $"{name} succeeded for another learner");
            Assert.True((int)response.StatusCode < 500, $"{name} returned {(int)response.StatusCode}");
        }

        // And nothing of A's changed.
        await using var context = Db.CreateContext();
        var word = await context.Words.IgnoreQueryFilters().FirstAsync(w => w.Id == wordId);
        Assert.Equal("ميناء", word.Meaning);
        Assert.Equal(WordState.Learning, word.State);
        var item = await context.SessionItems.FirstAsync(i => i.Id == itemId);
        Assert.Equal(0, item.Attempts);
        Assert.False((await context.SkillSessions.FirstAsync(s => s.Id == sessionId)).IsComplete);
    }

    // ── S02: role matrix ─────────────────────────────────────────────────────

    private static readonly string[] AdminIntelGets =
    [
        "/api/admin/intel/overview", "/api/admin/intel/users", "/api/admin/intel/feedback",
        "/api/admin/intel/decisions", "/api/admin/intel/changes",
        "/api/admin/intel/system", "/api/admin/intel/ai/conversations",
    ];

    // The audit trail is Owner-only inside the admin area ("who looked at whom
    // is itself sensitive", AdminIntelEndpoints.AuditTrailAsync).
    private static readonly string[] OwnerOnlyGets =
    [
        "/api/admin/overview", "/api/admin/users", "/api/admin/feedback", "/api/admin/intel/audit",
    ];

    [SkippableFact]
    public async Task S02_role_matrix_anonymous_user_analyst_owner()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var anonymous = NewClient();
        var user = await RegisterAsync();
        var analyst = await RegisterWithRoleAsync(nameof(UserRole.Analyst));
        var owner = await RegisterWithRoleAsync(nameof(UserRole.Owner));

        foreach (var path in AdminIntelGets.Concat(OwnerOnlyGets).Append("/api/hub").Append("/api/words"))
        {
            var r = await anonymous.GetAsync(path);
            output.WriteLine($"anon {path}: {(int)r.StatusCode}");
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        foreach (var path in AdminIntelGets.Concat(OwnerOnlyGets))
        {
            var r = await user.Client.GetAsync(path);
            output.WriteLine($"user {path}: {(int)r.StatusCode}");
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }

        foreach (var path in AdminIntelGets)
        {
            var r = await analyst.Client.GetAsync(path);
            output.WriteLine($"analyst {path}: {(int)r.StatusCode}");
            Assert.True(r.IsSuccessStatusCode, $"analyst refused {path}");
        }

        foreach (var path in OwnerOnlyGets)
        {
            var r = await analyst.Client.GetAsync(path);
            output.WriteLine($"analyst(owner-only) {path}: {(int)r.StatusCode}");
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }

        foreach (var path in AdminIntelGets.Concat(OwnerOnlyGets))
        {
            var r = await owner.Client.GetAsync(path);
            output.WriteLine($"owner {path}: {(int)r.StatusCode}");
            Assert.True(r.IsSuccessStatusCode, $"owner refused {path}");
        }

        // The schedule skip is Owner-only even for the analyst.
        var skip = await analyst.Client.PostAsJsonAsync(
            $"/api/admin/users/{user.Id}/advance-schedule", new { days = 2 });
        output.WriteLine($"analyst advance-schedule: {(int)skip.StatusCode}");
        Assert.Equal(HttpStatusCode.Forbidden, skip.StatusCode);
    }

    // ── S03: the analyst never sees contact details ──────────────────────────

    [SkippableFact]
    public async Task S03_analyst_responses_never_carry_a_learners_email_or_phone()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var learner = await RegisterAsync();
        await AddWordAsync(learner, "harbour", "ميناء");
        var feedback = await learner.Client.PostAsJsonAsync("/api/feedback",
            new { body = "the listening clip kept playing", category = "LISTENING" });
        output.WriteLine($"feedback: {(int)feedback.StatusCode}");
        var analyst = await RegisterWithRoleAsync(nameof(UserRole.Analyst));
        var owner = await RegisterWithRoleAsync(nameof(UserRole.Owner));

        var paths = new[]
        {
            "/api/admin/intel/users", $"/api/admin/intel/users/{learner.Id}",
            $"/api/admin/intel/users/{learner.Id}/timeline", "/api/admin/intel/feedback",
            "/api/admin/intel/users/attention", "/api/admin/intel/overview",
        };

        foreach (var path in paths)
        {
            var asAnalyst = await analyst.Client.GetAsync(path);
            var body = await asAnalyst.Content.ReadAsStringAsync();
            output.WriteLine($"analyst {path}: {(int)asAnalyst.StatusCode}, {body.Length} bytes");
            Assert.True(asAnalyst.IsSuccessStatusCode);
            Assert.DoesNotContain(learner.Email, body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("770000001", body);
        }

        // Control: the owner does see them on the learner page, so the probe is
        // looking in the right place.
        var ownerView = await (await owner.Client.GetAsync($"/api/admin/intel/users/{learner.Id}"))
            .Content.ReadAsStringAsync();
        Assert.Contains(learner.Email, ownerView, StringComparison.OrdinalIgnoreCase);
    }

    // ── S04: token tampering ─────────────────────────────────────────────────

    private static string Sign(string key, string issuer, string audience, DateTime notBefore,
        DateTime expires, params Claim[] claims)
    {
        var token = new JwtSecurityToken(issuer, audience, claims, notBefore, expires,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string B64(string s) => Base64UrlEncoder.Encode(s);

    [SkippableFact]
    public async Task S04_forged_expired_and_foreign_tokens_are_refused()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var learner = await RegisterAsync();
        var sub = new Claim(JwtRegisteredClaimNames.Sub, learner.Id.ToString());
        var ownerRole = new Claim(ClaimTypes.Role, nameof(UserRole.Owner));
        var now = DateTime.UtcNow;
        var otherKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));

        var parts = learner.Token.Split('.');
        var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(
            Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(parts[1])))!;
        payload[ClaimTypes.Role] = "Owner";
        var roleEditedSameSignature = $"{parts[0]}.{B64(JsonSerializer.Serialize(payload))}.{parts[2]}";
        var algNone = $"{B64("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{B64(JsonSerializer.Serialize(payload))}.";

        var cases = new (string Name, string Token, string Path)[]
        {
            ("other environment key + Owner role", Sign(otherKey, "wordos-test", "wordos-test", now, now.AddMinutes(10), sub, ownerRole), "/api/admin/intel/overview"),
            ("role edited, original signature", roleEditedSameSignature, "/api/admin/intel/overview"),
            ("alg none", algNone, "/api/me"),
            ("expired 2 minutes ago (skew is 30s)", Sign(ApiFactory.TestSigningKey, "wordos-test", "wordos-test", now.AddMinutes(-20), now.AddMinutes(-2), sub), "/api/me"),
            ("wrong issuer", Sign(ApiFactory.TestSigningKey, "someone-else", "wordos-test", now, now.AddMinutes(10), sub), "/api/me"),
            ("wrong audience", Sign(ApiFactory.TestSigningKey, "wordos-test", "someone-else", now, now.AddMinutes(10), sub), "/api/me"),
            ("garbage", "not.a.token", "/api/me"),
        };

        foreach (var (name, token, path) in cases)
        {
            var client = NewClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var r = await client.GetAsync(path);
            output.WriteLine($"{name}: {(int)r.StatusCode}");
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        // Control: the real token still works.
        Assert.True((await learner.Client.GetAsync("/api/me")).IsSuccessStatusCode);
    }

    // ── S05: mass assignment ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task S05_a_role_or_level_in_the_body_is_ignored()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var client = NewClient();
        var email = $"mass-{Guid.NewGuid():N}@test.dev";
        var r = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email, password = "correct-horse-battery", displayName = "M",
            phoneCountryCode = "967", phoneNumber = "770000001",
            role = "Owner", onboardingStage = "Complete", id = Guid.NewGuid(),
        });
        output.WriteLine($"register with role: {(int)r.StatusCode}");
        r.EnsureSuccessStatusCode();
        await using (var context = Db.CreateContext())
        {
            var user = await context.Users.FirstAsync(u => u.Email == email);
            Assert.Equal(UserRole.User, user.Role);
        }

        var body = await JsonAsync(r);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/intel/overview")).StatusCode);

        // A forged band and definition beside a real sense are discarded.
        var senseId = await SeedSenseAsync("harbour", "ميناء", CefrLevel.B1);
        var add = await client.PostAsJsonAsync("/api/words", new
        {
            senseId, cefrLevel = "C2", definitionEn = "forged", state = "ACTIVE", userId = Guid.NewGuid(),
        });
        add.EnsureSuccessStatusCode();
        await using (var context = Db.CreateContext())
        {
            var word = await context.Words.FirstAsync(w => w.SenseId == senseId);
            Assert.Equal(CefrLevel.B1, word.CefrLevel);
            Assert.NotEqual("forged", word.DefinitionEn);
            Assert.Equal(WordState.Learning, word.State);
            Assert.Equal(body.GetProperty("user").GetProperty("id").GetGuid(), word.UserId);
        }
    }

    // ── S06: no key leaks through a resumed session ──────────────────────────

    [SkippableFact]
    public async Task S06_a_resumed_session_carries_no_answer_key_for_unanswered_items()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        await AddWordAsync(a, "harbour", "ميناء");
        var start = await JsonAsync(await a.Client.PostAsync("/api/sessions/reading/start", null));
        var resumed = await a.Client.GetStringAsync($"/api/sessions/{start.GetProperty("id").GetGuid()}");
        var doc = JsonDocument.Parse(resumed).RootElement;

        foreach (var item in doc.GetProperty("items").EnumerateArray())
        {
            foreach (var key in new[] { "correctAnswer", "answerKey", "isCorrect", "correct" })
            {
                if (item.TryGetProperty(key, out var v))
                    Assert.True(v.ValueKind is JsonValueKind.Null or JsonValueKind.False,
                        $"unanswered item exposes {key}={v}");
            }
        }
        output.WriteLine($"resumed payload checked: {doc.GetProperty("items").GetArrayLength()} items");
    }

    // ── S07: admin AI conversations are per author ───────────────────────────

    [SkippableFact]
    public async Task S07_one_admins_ai_conversation_is_not_another_admins()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var owner = await RegisterWithRoleAsync(nameof(UserRole.Owner));
        var analyst = await RegisterWithRoleAsync(nameof(UserRole.Analyst));

        // Created through the API so the row is shaped exactly as production writes it.
        Root!.Agent.Script.Enqueue(StubAdminAgentService.Answer("Nothing to report.", "QA"));
        var send = await owner.Client.PostAsJsonAsync("/api/admin/intel/ai/messages",
            new { text = "how many learners?", conversationId = (Guid?)null });
        output.WriteLine($"owner send: {(int)send.StatusCode}");
        send.EnsureSuccessStatusCode();
        var conversationId = (await JsonAsync(send)).GetProperty("conversation").GetProperty("id").GetGuid();

        foreach (var (name, r) in new[]
        {
            ("GET", await analyst.Client.GetAsync($"/api/admin/intel/ai/conversations/{conversationId}")),
            ("DELETE", await analyst.Client.DeleteAsync($"/api/admin/intel/ai/conversations/{conversationId}")),
            ("PATCH", await analyst.Client.PatchAsJsonAsync($"/api/admin/intel/ai/conversations/{conversationId}", new { title = "x" })),
        })
        {
            output.WriteLine($"analyst {name} owner's conversation: {(int)r.StatusCode}");
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }
        var list = await analyst.Client.GetStringAsync("/api/admin/intel/ai/conversations");
        Assert.DoesNotContain(conversationId.ToString(), list);
    }
}
