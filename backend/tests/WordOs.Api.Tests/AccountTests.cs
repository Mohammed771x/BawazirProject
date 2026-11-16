using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using WordOs.Api.Tests.Qa;
using WordOs.Domain.Common;

namespace WordOs.Api.Tests;

/// <summary>
/// Deleting an account removes every row that is the learner's, and nothing
/// that is anyone else's (ADR-140).
/// </summary>
[Collection(PostgresCollection.Name)]
public class AccountDeletionTests(PostgresFixture db) : QaApiBase(db)
{
    private const string Password = "correct-horse-battery";

    /// <summary>Gives a learner data in as many tables as the API can reach.</summary>
    private async Task FillAsync(Learner l)
    {
        (await l.Client.PutAsJsonAsync("/api/me/interests", new { interests = new[] { "Science", "Travel" } }))
            .EnsureSuccessStatusCode();
        await AddWordAsync(l, $"del{Guid.NewGuid():N}"[..9], "معنى أول");
        await AddWordAsync(l, $"del{Guid.NewGuid():N}"[..9], "معنى ثان");

        var session = await JsonAsync(await l.Client.PostAsJsonAsync("/api/sessions/reading/start", new { }));
        var id = session.GetProperty("id").GetGuid();
        (await l.Client.PostAsync($"/api/sessions/{id}/complete", null)).EnsureSuccessStatusCode();

        (await l.Client.PostAsJsonAsync("/api/feedback", new { body = "A note before I go.", category = "general" }))
            .EnsureSuccessStatusCode();
        (await l.Client.PostAsJsonAsync("/api/events", new
        {
            events = new[] { new { name = "screen_viewed", at = DateTimeOffset.UtcNow, screen = "hub" } },
        })).EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Every row in the database that names <paramref name="userId"/>, by table.
    /// Found from the schema rather than listed by hand, so a table added
    /// later is counted without anyone remembering to add it here.
    /// </summary>
    private async Task<Dictionary<string, long>> RowsOfAsync(Guid userId)
    {
        await using var c = new NpgsqlConnection(Db.ConnectionString);
        await c.OpenAsync();

        var columns = new List<(string Table, string Column)>();
        await using (var q = new NpgsqlCommand("""
            SELECT table_name, column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND data_type = 'uuid'
              AND column_name IN ('UserId', 'AuthorId', 'ActorId', 'TargetId')
            """, c))
        await using (var r = await q.ExecuteReaderAsync())
            while (await r.ReadAsync()) columns.Add((r.GetString(0), r.GetString(1)));

        // Children that reach the learner only through a parent row.
        var children = new (string Table, string Column, string Parent, string ParentColumn)[]
        {
            ("word_events", "WordId", "words", "UserId"),
            ("word_skill_states", "WordId", "words", "UserId"),
            ("word_exposures", "WordId", "words", "UserId"),
            ("weekly_review_items", "WordId", "words", "UserId"),
            ("session_items", "SessionId", "skill_sessions", "UserId"),
            ("placement_answers", "SessionId", "placement_sessions", "UserId"),
        };

        var counts = new Dictionary<string, long>();
        foreach (var (table, column) in columns)
        {
            await using var q = new NpgsqlCommand($"""SELECT count(*) FROM "{table}" WHERE "{column}" = @id""", c);
            q.Parameters.AddWithValue("id", userId);
            counts[$"{table}.{column}"] = (long)(await q.ExecuteScalarAsync())!;
        }
        await using (var q = new NpgsqlCommand("""SELECT count(*) FROM users WHERE "Id" = @id""", c))
        {
            q.Parameters.AddWithValue("id", userId);
            counts["users"] = (long)(await q.ExecuteScalarAsync())!;
        }
        return counts;
    }

    /// <summary>Child rows hanging off this learner's words and sessions, captured by id.</summary>
    private async Task<long> ChildRowsAsync(IReadOnlyCollection<Guid> wordIds, IReadOnlyCollection<Guid> sessionIds)
    {
        await using var context = Db.CreateContext();
        var words = wordIds.ToList();
        var sessions = sessionIds.ToList();
        var total = 0L;
        foreach (var sql in new[]
        {
            """SELECT count(*)::bigint AS "Value" FROM word_events WHERE "WordId" = ANY({0})""",
            """SELECT count(*)::bigint AS "Value" FROM word_skill_states WHERE "WordId" = ANY({0})""",
            """SELECT count(*)::bigint AS "Value" FROM word_exposures WHERE "WordId" = ANY({0})""",
        })
            total += await context.Database.SqlQueryRaw<long>(sql, words.ToArray()).SingleAsync();
        total += await context.Database.SqlQueryRaw<long>(
            """SELECT count(*)::bigint AS "Value" FROM session_items WHERE "SessionId" = ANY({0})""",
            sessions.ToArray()).SingleAsync();
        return total;
    }

    [SkippableFact]
    public async Task Deleting_removes_every_row_of_the_learner_and_none_of_anyone_else()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var leaving = await RegisterAsync();
        var staying = await RegisterAsync();
        await FillAsync(leaving);
        await FillAsync(staying);

        List<Guid> wordIds, sessionIds;
        await using (var context = Db.CreateContext())
        {
            wordIds = await context.Words.IgnoreQueryFilters().Where(w => w.UserId == leaving.Id).Select(w => w.Id).ToListAsync();
            sessionIds = await context.SkillSessions.Where(s => s.UserId == leaving.Id).Select(s => s.Id).ToListAsync();
        }

        var before = await RowsOfAsync(leaving.Id);
        var othersBefore = await RowsOfAsync(staying.Id);
        Assert.True(before.Values.Sum() > 10, "the learner should have data in many tables before deleting");
        Assert.True(await ChildRowsAsync(wordIds, sessionIds) > 0);

        var response = await leaving.Client.PostAsJsonAsync("/api/me/delete", new { password = Password });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = await RowsOfAsync(leaving.Id);
        Assert.All(after, kv => Assert.True(kv.Value == 0, $"{kv.Key} still has {kv.Value} row(s)"));
        Assert.Equal(0, await ChildRowsAsync(wordIds, sessionIds));
        Assert.Equal(othersBefore, await RowsOfAsync(staying.Id));
    }

    [SkippableFact]
    public async Task After_deleting_the_session_and_the_password_are_gone_and_the_address_is_free()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var learner = await RegisterAsync();

        (await learner.Client.PostAsJsonAsync("/api/me/delete", new { password = Password })).EnsureSuccessStatusCode();

        // The access token still in the app's memory is refused…
        Assert.Equal(HttpStatusCode.Unauthorized, (await learner.Client.GetAsync("/api/me")).StatusCode);
        // …the refresh token went with the account…
        var refresh = await NewClient().PostAsJsonAsync("/api/auth/refresh", new { refreshToken = learner.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        // …signing in finds nobody…
        var login = await NewClient().PostAsJsonAsync("/api/auth/login", new { email = learner.Email, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        // …and the address can start again from nothing.
        var again = await RegisterAsync(learner.Email);
        Assert.NotEqual(learner.Id, again.Id);
    }

    [SkippableFact]
    public async Task A_wrong_password_deletes_nothing_and_keeps_the_session()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var learner = await RegisterAsync();
        await AddWordAsync(learner, $"keep{Guid.NewGuid():N}"[..9], "باق");

        var response = await learner.Client.PostAsJsonAsync("/api/me/delete", new { password = "not-my-password" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("WRONG_PASSWORD", await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await learner.Client.GetAsync("/api/me")).StatusCode);
        await using var context = Db.CreateContext();
        Assert.Equal(1, await context.Words.CountAsync(w => w.UserId == learner.Id));
    }

    [SkippableFact]
    public async Task Guessing_the_password_to_delete_runs_into_the_account_budget()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var learner = await RegisterAsync();

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
            codes.Add((await learner.Client.PostAsJsonAsync("/api/me/delete", new { password = $"guess-{i}" })).StatusCode);

        Assert.Equal(10, codes.Count(c => c == HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[^1]);
        // Even the right password waits out the window now.
        var right = await learner.Client.PostAsJsonAsync("/api/me/delete", new { password = Password });
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await learner.Client.GetAsync("/api/me")).StatusCode);
    }

    [SkippableFact]
    public async Task Staff_accounts_cannot_delete_themselves_from_the_app()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        foreach (var role in new[] { "Owner", "Analyst" })
        {
            var staff = await RegisterWithRoleAsync(role);
            var response = await staff.Client.PostAsJsonAsync("/api/me/delete", new { password = Password });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("STAFF_ACCOUNT", await response.Content.ReadAsStringAsync());
            await using var context = Db.CreateContext();
            Assert.True(await context.Users.AnyAsync(u => u.Id == staff.Id));
        }
    }

    [SkippableFact]
    public async Task Deleting_needs_a_signed_in_learner_and_a_password()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var anonymous = await NewClient().PostAsJsonAsync("/api/me/delete", new { password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var learner = await RegisterAsync();
        var empty = await learner.Client.PostAsJsonAsync("/api/me/delete", new { password = "" });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await learner.Client.GetAsync("/api/me")).StatusCode);
    }
}

/// <summary>
/// A new account proves its email address before onboarding, when required
/// (ADR-141).
/// </summary>
[Collection(PostgresCollection.Name)]
public class EmailVerificationTests(PostgresFixture db) : QaApiBase(db)
{
    protected override IReadOnlyDictionary<string, string> Settings { get; } =
        new Dictionary<string, string> { ["WordOs:RequireEmailVerification"] = "true" };

    private static async Task<string> StageAsync(Learner l) =>
        (await JsonAsync(await l.Client.GetAsync("/api/me"))).GetProperty("onboardingStage").GetString()!;

    private async Task<(Learner Learner, string Code)> RegisterAndReadCodeAsync()
    {
        Root!.Email.Clear();
        var learner = await RegisterAsync();
        var mail = Root.Email.Sent.Single(m => m.ToAddress == learner.Email);
        Assert.Contains("verification", mail.Subject);
        return (learner, Root.Email.LastCode!);
    }

    [SkippableFact]
    public async Task Registering_sends_a_code_and_holds_onboarding_until_it_is_entered()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var (learner, code) = await RegisterAndReadCodeAsync();

        Assert.Matches(@"^\d{6}$", code);
        Assert.Equal("VERIFY_EMAIL", await StageAsync(learner));

        var verified = await learner.Client.PostAsJsonAsync("/api/auth/email/verify", new { code });
        verified.EnsureSuccessStatusCode();
        Assert.Equal("INTERESTS", (await JsonAsync(verified)).GetProperty("onboardingStage").GetString());

        await using var context = Db.CreateContext();
        var user = await context.Users.SingleAsync(u => u.Id == learner.Id);
        Assert.NotNull(user.EmailVerifiedAt);
        Assert.Null(user.EmailCodeHash);

        // A repeat of the same request (its reply lost) answers the same.
        var again = await learner.Client.PostAsJsonAsync("/api/auth/email/verify", new { code });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [SkippableFact]
    public async Task Onboarding_cannot_be_walked_round_the_code()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var (learner, _) = await RegisterAndReadCodeAsync();

        var interests = await learner.Client.PutAsJsonAsync("/api/me/interests", new { interests = new[] { "Science" } });
        Assert.Equal(HttpStatusCode.Conflict, interests.StatusCode);
        Assert.Contains("EMAIL_NOT_VERIFIED", await interests.Content.ReadAsStringAsync());

        var placement = await learner.Client.PostAsync("/api/placement/start", null);
        Assert.Equal(HttpStatusCode.Conflict, placement.StatusCode);
        Assert.Equal("VERIFY_EMAIL", await StageAsync(learner));
    }

    [SkippableFact]
    public async Task Five_wrong_codes_spend_it_and_a_new_one_works()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var (learner, code) = await RegisterAndReadCodeAsync();
        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < 5; i++)
        {
            var r = await learner.Client.PostAsJsonAsync("/api/auth/email/verify", new { code = wrong });
            Assert.Contains("INVALID_EMAIL_CODE", await r.Content.ReadAsStringAsync());
        }

        // The right code is now refused too: five guesses was the budget.
        var spent = await learner.Client.PostAsJsonAsync("/api/auth/email/verify", new { code });
        Assert.Contains("EMAIL_CODE_EXPIRED", await spent.Content.ReadAsStringAsync());

        Clock.Advance(TimeSpan.FromSeconds(61));
        Root!.Email.Clear();
        (await learner.Client.PostAsync("/api/auth/email/resend", null)).EnsureSuccessStatusCode();
        var fresh = Root.Email.LastCode!;

        var ok = await learner.Client.PostAsJsonAsync("/api/auth/email/verify", new { code = fresh });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [SkippableFact]
    public async Task A_code_expires_and_a_new_one_replaces_the_old()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var (learner, first) = await RegisterAndReadCodeAsync();

        Clock.Advance(TimeSpan.FromMinutes(31));
        var expired = await learner.Client.PostAsJsonAsync("/api/auth/email/verify", new { code = first });
        Assert.Contains("EMAIL_CODE_EXPIRED", await expired.Content.ReadAsStringAsync());

        Root!.Email.Clear();
        (await learner.Client.PostAsync("/api/auth/email/resend", null)).EnsureSuccessStatusCode();
        var second = Root.Email.LastCode!;

        if (second != first)
        {
            var old = await learner.Client.PostAsJsonAsync("/api/auth/email/verify", new { code = first });
            Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK,
            (await learner.Client.PostAsJsonAsync("/api/auth/email/verify", new { code = second })).StatusCode);
    }

    [SkippableFact]
    public async Task Sending_again_waits_a_minute_and_says_how_long()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var (learner, _) = await RegisterAndReadCodeAsync();
        Root!.Email.Clear();

        var tooSoon = await learner.Client.PostAsync("/api/auth/email/resend", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, tooSoon.StatusCode);
        Assert.True(tooSoon.Headers.RetryAfter?.Delta?.TotalSeconds is > 0 and <= 60);
        Assert.Contains("RATE_LIMITED", await tooSoon.Content.ReadAsStringAsync());
        Assert.Empty(Root.Email.Sent);

        Clock.Advance(TimeSpan.FromSeconds(61));
        var later = await learner.Client.PostAsync("/api/auth/email/resend", null);
        Assert.Equal(HttpStatusCode.Accepted, later.StatusCode);
        Assert.Single(Root.Email.Sent);
    }

    [SkippableFact]
    public async Task A_failed_send_keeps_the_registration_and_the_learner_can_ask_again()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        Root!.Email.Fails = true;
        try
        {
            var learner = await RegisterAsync();
            Assert.Equal("VERIFY_EMAIL", await StageAsync(learner));

            Root.Email.Fails = false;
            Clock.Advance(TimeSpan.FromSeconds(61));
            (await learner.Client.PostAsync("/api/auth/email/resend", null)).EnsureSuccessStatusCode();
            var code = Root.Email.LastCode!;
            Assert.Equal(HttpStatusCode.OK,
                (await learner.Client.PostAsJsonAsync("/api/auth/email/verify", new { code })).StatusCode);
        }
        finally
        {
            Root.Email.Fails = false;
        }
    }

    [SkippableFact]
    public async Task Signing_in_again_before_verifying_returns_to_the_code()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var (learner, _) = await RegisterAndReadCodeAsync();

        var login = await NewClient().PostAsJsonAsync("/api/auth/login",
            new { email = learner.Email, password = "correct-horse-battery" });
        login.EnsureSuccessStatusCode();
        Assert.Equal("VERIFY_EMAIL",
            (await JsonAsync(login)).GetProperty("user").GetProperty("onboardingStage").GetString());
    }

    [SkippableFact]
    public async Task A_verified_account_has_nothing_to_resend()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var (learner, code) = await RegisterAndReadCodeAsync();
        (await learner.Client.PostAsJsonAsync("/api/auth/email/verify", new { code })).EnsureSuccessStatusCode();

        var resend = await learner.Client.PostAsync("/api/auth/email/resend", null);
        Assert.Equal(HttpStatusCode.Conflict, resend.StatusCode);
        Assert.Contains("EMAIL_ALREADY_VERIFIED", await resend.Content.ReadAsStringAsync());
    }
}

/// <summary>The setting off — the default — changes nothing about registering (ADR-141).</summary>
[Collection(PostgresCollection.Name)]
public class EmailVerificationOffTests(PostgresFixture db) : QaApiBase(db)
{
    [SkippableFact]
    public async Task Without_the_setting_registration_goes_straight_to_interests_and_sends_nothing()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        Root!.Email.Clear();
        var learner = await RegisterAsync();

        var me = await JsonAsync(await learner.Client.GetAsync("/api/me"));
        Assert.Equal("INTERESTS", me.GetProperty("onboardingStage").GetString());
        Assert.Empty(Root.Email.Sent);
    }
}
