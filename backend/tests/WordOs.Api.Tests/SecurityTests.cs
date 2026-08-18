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
/// Authorization and input-trust tests against the running API.
/// </summary>
/// <remarks>
/// Every one of these is a claim from <c>docs/07-SECURITY.md</c> turned into an
/// executable check. A security property that is only written down is a
/// property nobody knows is still true.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class SecurityTests(PostgresFixture db) : IAsyncLifetime
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

    private static string Unique(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}@test.dev";

    /// <summary>Registers a learner and returns their access token.</summary>
    private async Task<(string Token, Guid UserId)> RegisterAsync(
        string? email = null)
    {
        var response = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email = email ?? Unique("learner"),
            password = "correct-horse-battery",
            displayName = "Learner",
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("token").GetString()!,
                body.GetProperty("user").GetProperty("id").GetGuid());
    }

    /// <summary>Promotes a user to Owner directly in the database.</summary>
    /// <remarks>
    /// Deliberately done here, not through the API — because the API offers no
    /// way to do it, which is itself one of the properties under test.
    /// </remarks>
    private async Task<string> RegisterOwnerAsync()
    {
        var email = Unique("owner");
        await RegisterAsync(email);

        await using (var context = db.CreateContext())
        {
            await context.Database.ExecuteSqlAsync(
                $"""UPDATE users SET "Role" = 'Owner' WHERE "Email" = {email}""");
        }

        // Re-login so the token carries the Owner role.
        var login = await Client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = "correct-horse-battery",
        });
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("token").GetString()!;
    }

    private void Authenticate(string token) =>
        Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

    // ── Authentication ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Protected_endpoints_refuse_an_anonymous_request()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        foreach (var path in new[]
                 {
                     "/api/me", "/api/words?state=LEARNING",
                     "/api/words/lookup?q=bo", "/api/words/define?w=research",
                     "/api/admin/overview", "/api/admin/users",
                     $"/api/admin/users/{Guid.NewGuid()}/words",
                     $"/api/admin/users/{Guid.NewGuid()}/placement",
                     $"/api/admin/words/{Guid.NewGuid()}",
                 })
        {
            var response = await Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [SkippableFact]
    public async Task A_forged_token_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        // Signed with a different key — the signature check must reject it.
        Authenticate(
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9." +
            "eyJzdWIiOiIwMDAwMDAwMC0wMDAwLTAwMDAtMDAwMC0wMDAwMDAwMDAwMDEiLCJyb2xlIjoiT3duZXIifQ." +
            "not-a-valid-signature");

        var response = await Client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    public async Task Login_does_not_reveal_whether_an_email_exists()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var email = Unique("known");
        await RegisterAsync(email);

        var wrongPassword = await Client.PostAsJsonAsync("/api/auth/login",
            new { email, password = "wrong-password-entirely" });
        var unknownEmail = await Client.PostAsJsonAsync("/api/auth/login",
            new { email = Unique("nobody"), password = "wrong-password-entirely" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);

        // Identical bodies, so the response cannot be used to enumerate accounts.
        Assert.Equal(
            await wrongPassword.Content.ReadAsStringAsync(),
            await unknownEmail.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task A_password_is_never_stored_or_returned_in_clear()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        const string password = "correct-horse-battery";
        var email = Unique("hash");
        var response = await Client.PostAsJsonAsync("/api/auth/register",
            new { email, password, displayName = "Learner" });

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(password, body);

        await using var context = db.CreateContext();
        var stored = await context.Users.SingleAsync(u => u.Email == email);
        Assert.DoesNotContain(password, stored.PasswordHash);
        Assert.NotEqual(password, stored.PasswordHash);
    }

    [SkippableFact]
    public async Task Logout_revokes_the_refresh_token()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var email = Unique("logout");
        var register = await Client.PostAsJsonAsync("/api/auth/register",
            new { email, password = "correct-horse-battery", displayName = "L" });
        var body = await register.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;
        var refresh = body.GetProperty("refreshToken").GetString()!;

        Authenticate(token);
        var logout = await Client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        Client.DefaultRequestHeaders.Authorization = null;
        var reuse = await Client.PostAsJsonAsync("/api/auth/refresh",
            new { refreshToken = refresh });

        // Signing out ends the session — it does not merely drop the client's
        // copy of the token.
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    [SkippableFact]
    public async Task Reusing_a_refresh_token_revokes_the_whole_family()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var register = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email = Unique("rotate"),
            password = "correct-horse-battery",
            displayName = "L",
        });
        var body = await register.Content.ReadFromJsonAsync<JsonElement>();
        var first = body.GetProperty("refreshToken").GetString()!;

        var rotated = await Client.PostAsJsonAsync("/api/auth/refresh",
            new { refreshToken = first });
        rotated.EnsureSuccessStatusCode();
        var rotatedBody = await rotated.Content.ReadFromJsonAsync<JsonElement>();
        var second = rotatedBody.GetProperty("refreshToken").GetString()!;

        // Presenting the spent token is evidence it leaked.
        var replay = await Client.PostAsJsonAsync("/api/auth/refresh",
            new { refreshToken = first });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // …so the replacement is revoked too, rather than left live.
        var afterBreach = await Client.PostAsJsonAsync("/api/auth/refresh",
            new { refreshToken = second });
        Assert.Equal(HttpStatusCode.Unauthorized, afterBreach.StatusCode);
    }

    // ── Authorization: /admin/* ──────────────────────────────────────────────

    [SkippableFact]
    public async Task A_normal_learner_is_refused_every_admin_endpoint()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var (token, _) = await RegisterAsync();
        Authenticate(token);

        foreach (var path in new[]
                 {
                     "/api/admin/overview",
                     "/api/admin/users",
                     $"/api/admin/users/{Guid.NewGuid()}",
                     // The Part 3 views. Every one of them is a route a
                     // learner could guess, so every one is checked here
                     // rather than trusted to the group policy alone.
                     $"/api/admin/users/{Guid.NewGuid()}/words",
                     $"/api/admin/users/{Guid.NewGuid()}/placement",
                     $"/api/admin/words/{Guid.NewGuid()}",
                 })
        {
            var response = await Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [SkippableFact]
    public async Task The_owner_is_allowed()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        Authenticate(await RegisterOwnerAsync());

        var overview = await Client.GetAsync("/api/admin/overview");
        var users = await Client.GetAsync("/api/admin/users");

        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
    }

    [SkippableFact]
    public async Task Registration_can_never_produce_an_owner()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        // Role is not a field the API accepts; sending one must be ignored.
        // Sent as raw JSON so both casings can be attempted in one body.
        var payload = $$"""
            {
              "email": "{{Unique("escalate")}}",
              "password": "correct-horse-battery",
              "displayName": "Sneaky",
              "role": "Owner",
              "Role": "OWNER",
              "isOwner": true
            }
            """;

        var response = await Client.PostAsync("/api/auth/register",
            new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("USER", body.GetProperty("user").GetProperty("role").GetString());

        Authenticate(body.GetProperty("token").GetString()!);
        var admin = await Client.GetAsync("/api/admin/overview");
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
    }

    // ── Object-level authorization (IDOR) ────────────────────────────────────

    [SkippableFact]
    public async Task A_learner_sees_only_their_own_words()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var senseId = $"idor-{Guid.NewGuid():N}";
        await using (var context = db.CreateContext())
        {
            context.LexiconEntries.Add(LexiconEntry.Create(
                senseId, "solitude", "solitude", "n", "being alone", "عزلة",
                CefrLevel.B2, 1, "en=oewn;ar=awn", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        var (tokenA, _) = await RegisterAsync();
        Authenticate(tokenA);
        var added = await Client.PostAsJsonAsync("/api/words", new { senseId });
        added.EnsureSuccessStatusCode();

        var mine = await Client.GetFromJsonAsync<JsonElement>("/api/words");
        Assert.Equal(1, mine.GetProperty("total").GetInt32());

        // A second learner must not see it, even though they know the sense id.
        var (tokenB, _) = await RegisterAsync();
        Authenticate(tokenB);
        var theirs = await Client.GetFromJsonAsync<JsonElement>("/api/words");
        Assert.Equal(0, theirs.GetProperty("total").GetInt32());
    }

    [SkippableFact]
    public async Task Two_learners_may_each_add_the_same_sense()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var senseId = $"shared-{Guid.NewGuid():N}";
        await using (var context = db.CreateContext())
        {
            context.LexiconEntries.Add(LexiconEntry.Create(
                senseId, "candid", "candid", "a", "frank", "صريح",
                CefrLevel.B2, 1, "en=oewn;ar=awn", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        foreach (var _ in Enumerable.Range(0, 2))
        {
            var (token, _) = await RegisterAsync();
            Authenticate(token);
            var response = await Client.PostAsJsonAsync("/api/words", new { senseId });
            response.EnsureSuccessStatusCode();
        }
    }

    // ── Never trusting the request body ─────────────────────────────────────

    [SkippableFact]
    public async Task A_forged_level_or_meaning_in_the_request_is_discarded()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var senseId = $"forge-{Guid.NewGuid():N}";
        await using (var context = db.CreateContext())
        {
            context.LexiconEntries.Add(LexiconEntry.Create(
                senseId, "diligent", "diligent", "a", "showing care", "مجتهد",
                CefrLevel.B2, 1, "en=oewn;ar=awn", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        var (token, _) = await RegisterAsync();
        Authenticate(token);

        var response = await Client.PostAsJsonAsync("/api/words", new
        {
            senseId,
            text = "whatever-the-client-felt-like",
            meaning = "معنى مخترع",
            cefrLevel = "C2",
            definitionEn = "made up",
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The stored row is the lexicon's, not the request's.
        Assert.Equal("diligent", body.GetProperty("text").GetString());
        Assert.Equal("مجتهد", body.GetProperty("meaning").GetString());
        Assert.Equal("B2", body.GetProperty("cefrLevel").GetString());
    }

    [SkippableFact]
    public async Task An_unknown_sense_id_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var (token, _) = await RegisterAsync();
        Authenticate(token);

        var response = await Client.PostAsJsonAsync("/api/words",
            new { senseId = "not-a-real-sense-id" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("WORD_NOT_FOUND", body);
    }

    [SkippableFact]
    public async Task The_same_sense_cannot_be_added_twice_by_one_learner()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var senseId = $"dup-{Guid.NewGuid():N}";
        await using (var context = db.CreateContext())
        {
            context.LexiconEntries.Add(LexiconEntry.Create(
                senseId, "resilient", "resilient", "a", "able to recover",
                "مَرِن", CefrLevel.B2, 1, "en=oewn;ar=awn", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        var (token, _) = await RegisterAsync();
        Authenticate(token);

        (await Client.PostAsJsonAsync("/api/words", new { senseId }))
            .EnsureSuccessStatusCode();
        var second = await Client.PostAsJsonAsync("/api/words", new { senseId });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("WORD_ALREADY_ADDED",
            await second.Content.ReadAsStringAsync());
    }

    // ── Input validation ────────────────────────────────────────────────────

    [SkippableTheory]
    [InlineData("", "correct-horse-battery")]
    [InlineData("not-an-email", "correct-horse-battery")]
    [InlineData("valid@test.dev", "short")]
    public async Task Invalid_registration_input_is_rejected(
        string email, string password)
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var response = await Client.PostAsJsonAsync("/api/auth/register",
            new { email, password, displayName = "L" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task An_oversized_search_term_is_refused_rather_than_queried()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var (token, _) = await RegisterAsync();
        Authenticate(token);

        var response = await Client.GetAsync(
            "/api/words/lookup?q=" + new string('a', 500));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task A_SQL_injection_attempt_in_lookup_is_inert()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var (token, _) = await RegisterAsync();
        Authenticate(token);

        // Parameterised throughout, so this is just a string that matches
        // nothing — not a statement.
        var response = await Client.GetAsync(
            "/api/words/lookup?q=" + Uri.EscapeDataString("'; DROP TABLE words;--"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var results = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, results.GetArrayLength());

        // The table is still there.
        await using var context = db.CreateContext();
        Assert.True(await context.Words.CountAsync() >= 0);
    }

    [SkippableFact]
    public async Task A_short_query_returns_nothing_rather_than_the_whole_lexicon()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var (token, _) = await RegisterAsync();
        Authenticate(token);

        var senseId = $"sec-{Guid.NewGuid():N}";
        await using (var context = db.CreateContext())
        {
            context.LexiconEntries.Add(LexiconEntry.Create(
                senseId, $"b{Guid.NewGuid():N}"[..10], "b", "n",
                "a long word", "كلمة", CefrLevel.B1, 1,
                "en=wordos-test;ar=wordos-test", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        var response = await Client.GetFromJsonAsync<JsonElement>(
            "/api/words/lookup?q=b");

        // One letter matches a one-letter word and nothing else. "a" and "I"
        // are words and must be addable, but a single letter must never return
        // everything that starts with it — that is a dictionary dump.
        Assert.DoesNotContain(
            response.EnumerateArray(),
            r => r.GetProperty("senseId").GetString() == senseId);
    }

    // ── Hostile input ───────────────────────────────────────────────────────

    [SkippableTheory]
    // The number is typed in by hand in the Owner's dashboard, so it arrives as
    // whatever was typed. Large ones used to overflow the date arithmetic and
    // answer 500, which emptied the dashboard the Owner was looking at.
    [InlineData("999999999")]
    [InlineData("2147483647")]
    [InlineData("-5")]
    [InlineData("0")]
    public async Task Any_reporting_window_a_keyboard_can_produce_is_answered(
        string days)
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var token = await RegisterOwnerAsync();
        Authenticate(token);

        foreach (var path in new[] { "/api/admin/overview", "/api/admin/users" })
        {
            var response = await Client.GetAsync($"{path}?days={days}");

            Assert.True(response.IsSuccessStatusCode,
                $"{path}?days={days} answered {(int)response.StatusCode}");
        }
    }

    [SkippableTheory]
    // A value that cannot be bound is the caller's mistake. ASP.NET raises it
    // as an exception after the endpoint filters, so without handling it every
    // mistyped query answered 500 and was logged as a server fault.
    [InlineData("/api/admin/overview?days=abc")]
    [InlineData("/api/admin/overview?days=3.5")]
    [InlineData("/api/words?page=x&pageSize=20")]
    public async Task An_unreadable_query_value_is_refused_not_a_server_error(
        string path)
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var token = await RegisterOwnerAsync();
        Authenticate(token);

        var response = await Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Outside Development the framework answers the binding failure itself,
        // with an empty body; inside it, it throws and the handler shapes the
        // response. Either way the caller gets a refusal — what matters is that
        // neither path is a 500.
        var body = await response.Content.ReadAsStringAsync();
        if (body.Length > 0)
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            Assert.Equal("INVALID_PARAMETER",
                json.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [SkippableTheory]
    // Control characters arrive from a paste or a broken client. PostgreSQL
    // refuses a NUL byte inside a text value outright, so one pasted character
    // was a 500 — never an injection risk, since the query is parameterised,
    // but a crash all the same.
    [InlineData("ab\u0000cd")]
    [InlineData("re\u0007search")]
    [InlineData("\u0001\u0002")]
    public async Task Control_characters_in_a_search_term_are_stripped(
        string term)
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var (token, _) = await RegisterAsync();
        Authenticate(token);

        foreach (var path in new[]
                 {
                     "/api/words/lookup?q=" + Uri.EscapeDataString(term),
                     "/api/words/define?w=" + Uri.EscapeDataString(term),
                     "/api/words?page=0&pageSize=20&q=" + Uri.EscapeDataString(term),
                 })
        {
            var response = await Client.GetAsync(path);

            Assert.True(
                response.IsSuccessStatusCode ||
                response.StatusCode == HttpStatusCode.BadRequest,
                $"{path} answered {(int)response.StatusCode}");
        }
    }

    // ── Error responses ─────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Errors_carry_a_code_and_never_a_stack_trace()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var (token, _) = await RegisterAsync();
        Authenticate(token);

        var response = await Client.PostAsJsonAsync("/api/words",
            new { senseId = "nope" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"code\"", body);
        foreach (var leak in new[]
                 {
                     "StackTrace", "Npgsql", "at WordOs.", "ConnectionString",
                     "Password=", "SELECT ",
                 })
        {
            Assert.DoesNotContain(leak, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── Rate limiting ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task One_learner_cannot_spend_another_learners_ai_budget()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        // The AI budget is per user, but the limiter only sees a user if it
        // runs after authentication. When it does not, every request partitions
        // by IP instead and a whole school behind one NAT shares one learner's
        // allowance — which is what this pins down.
        var (firstToken, _) = await RegisterAsync();
        Authenticate(firstToken);

        // Spend well past the per-minute AI budget. Every one of these fails on
        // its merits (no words are due), which is fine — a 429 would not be.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 40; i++)
        {
            var response = await Client.PostAsync("/api/sessions/reading/start", null);
            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);

        // A different learner, same machine, same IP: their budget is untouched.
        var (secondToken, _) = await RegisterAsync();
        Authenticate(secondToken);

        var fresh = await Client.PostAsync("/api/sessions/reading/start", null);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, fresh.StatusCode);
    }
}
