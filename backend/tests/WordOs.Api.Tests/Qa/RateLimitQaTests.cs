using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Xunit.Abstractions;

namespace WordOs.Api.Tests.Qa;

/// <summary>
/// QA prompt 08 with small, isolated budgets so each policy's N / N+1 edge is
/// visible. The limiter, partitioning and header handling are the production
/// code; only the numbers are smaller.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RateLimitQaTests(PostgresFixture db, ITestOutputHelper output) : QaApiBase(db)
{
    protected override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["RateLimits:AuthenticationPermits"] = "1000", // isolated per test via distinct True-Client-IP
        ["RateLimits:LookupPermitsPerMinute"] = "5",
        ["RateLimits:ExpensivePermitsPerMinute"] = "3",
        ["RateLimits:GlobalPermitsPerMinute"] = "40",
        ["RateLimits:MeaningCheckPermitsPerMinute"] = "3",
    };

    private static HttpRequestMessage Req(HttpMethod method, string path, string? ip = null, object? body = null)
    {
        var r = new HttpRequestMessage(method, path);
        if (ip is not null) r.Headers.TryAddWithoutValidation("True-Client-IP", ip);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    [SkippableFact]
    public async Task R01_lookup_allows_N_refuses_N_plus_1_and_is_per_learner()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var b = await RegisterAsync();

        var codes = new List<int>();
        for (var i = 0; i < 6; i++)
            codes.Add((int)(await a.Client.GetAsync($"/api/words/lookup?q=ha{i}")).StatusCode);
        var refused = await a.Client.GetAsync("/api/words/lookup?q=again");
        output.WriteLine($"A lookups: {string.Join(",", codes)}; 7th={(int)refused.StatusCode}; Retry-After={refused.Headers.RetryAfter?.ToString() ?? "absent"}; body='{await refused.Content.ReadAsStringAsync()}'");

        Assert.All(codes.Take(5), c => Assert.Equal(200, c));
        Assert.Equal(429, codes[5]);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        // Learner B, same process and same socket address, has their own budget.
        Assert.Equal(HttpStatusCode.OK, (await b.Client.GetAsync("/api/words/lookup?q=ha")).StatusCode);
        // A's refusal did not sign A out: a non-limited route still answers.
        Assert.Equal(HttpStatusCode.OK, (await a.Client.GetAsync("/api/me")).StatusCode);
    }

    [SkippableFact]
    public async Task R02_one_learner_from_two_addresses_shares_one_budget()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var codes = new List<int>();
        for (var i = 0; i < 6; i++)
        {
            var r = Req(HttpMethod.Get, $"/api/words/lookup?q=x{i}", ip: i % 2 == 0 ? "203.0.113.10" : "198.51.100.20");
            r.Headers.Authorization = a.Client.DefaultRequestHeaders.Authorization;
            codes.Add((int)(await a.Client.SendAsync(r)).StatusCode);
        }
        output.WriteLine($"alternating IPs: {string.Join(",", codes)}");
        Assert.Equal(429, codes[5]);
    }

    [SkippableFact]
    public async Task R03_a_written_meaning_spends_the_meaning_check_budget()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var checksBefore = Ai.MeaningChecks;

        // MeaningCheck = 3/min. Six learner-written meanings, each a checker call.
        var codes = new List<int>();
        for (var i = 0; i < 6; i++)
        {
            var r = await a.Client.PostAsJsonAsync("/api/words",
                new { text = $"qaword{i}{Guid.NewGuid():N}"[..14], customMeaning = "كلمة اختبار" });
            codes.Add((int)r.StatusCode);
        }
        var aiCalls = Ai.MeaningChecks - checksBefore;
        output.WriteLine($"custom-meaning adds: {string.Join(",", codes)}; checker calls={aiCalls} (budget=3)");

        Assert.Equal(new[] { 200, 200, 200, 429, 429, 429 }, codes);
        Assert.Equal(3, aiCalls);

        // The dictionary add on the same route is not counted.
        var lexicon = await a.Client.PostAsJsonAsync("/api/words",
            new { senseId = await SeedSenseAsync("harbour", "ميناء") });
        Assert.Equal(HttpStatusCode.OK, lexicon.StatusCode);
        var wordId = (await JsonAsync(lexicon)).GetProperty("id").GetGuid();
        var unchanged = await a.Client.PatchAsJsonAsync($"/api/words/{wordId}/meaning", new { meaning = "ميناء" });
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
    }

    [SkippableFact]
    public async Task R04_session_start_is_under_the_expensive_budget()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        await AddWordAsync(a, "harbour", "ميناء");
        var codes = new List<int>();
        for (var i = 0; i < 4; i++)
            codes.Add((int)(await a.Client.PostAsync("/api/sessions/reading/start", null)).StatusCode);
        output.WriteLine($"reading/start x4: {string.Join(",", codes)}");
        Assert.All(codes.Take(3), c => Assert.Equal(200, c));
        Assert.Equal(429, codes[3]);
    }

    [SkippableFact]
    public async Task R05_authentication_is_per_address_and_a_bad_header_falls_back_to_the_socket()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        // A dedicated factory with the production auth budget: 10 per 15 min.
        await using var prod = Root!.WithWebHostBuilder(b =>
            b.UseSetting("RateLimits:AuthenticationPermits", "10"));
        var client = prod.CreateClient();

        // A different account every time, so only the address budget is in play.
        async Task<int> Login(string? ip) => (int)(await client.SendAsync(Req(HttpMethod.Post, "/api/auth/login", ip,
            new { email = $"nobody-{Guid.NewGuid():N}@test.dev", password = "wrong-password-123" }))).StatusCode;

        var first = new List<int>();
        for (var i = 0; i < 11; i++) first.Add(await Login("203.0.113.50"));
        var otherAddress = await Login("203.0.113.51");
        var garbageHeader = await Login("not-an-ip");
        var noHeader = await Login(null);
        output.WriteLine($"same address x11: {string.Join(",", first)}; other address: {otherAddress}; garbage header: {garbageHeader}; no header: {noHeader}");

        Assert.All(first.Take(10), c => Assert.Equal(401, c));
        Assert.Equal(429, first[10]);
        Assert.Equal(401, otherAddress); // a fresh address has a fresh budget
    }

    [SkippableFact]
    public async Task R06_a_classroom_behind_one_address_keeps_refreshing()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        // Eleven learners already signed in (their sign-ins came from home).
        var learners = new List<Learner>();
        for (var i = 0; i < 11; i++) learners.Add(await RegisterAsync());

        // Even with the sign-in budget at the old 10, refresh has its own
        // (production default: 300 per 15 minutes per address).
        await using var prod = Root!.WithWebHostBuilder(b =>
            b.UseSetting("RateLimits:AuthenticationPermits", "10"));
        var client = prod.CreateClient();

        var codes = new List<int>();
        foreach (var l in learners)
            codes.Add((int)(await client.SendAsync(Req(HttpMethod.Post, "/api/auth/refresh", "198.51.100.77",
                new { refreshToken = l.RefreshToken }))).StatusCode);
        output.WriteLine($"11 learners, one address, one refresh each: {string.Join(",", codes)}");

        Assert.All(codes, c => Assert.Equal(200, c));
    }

    [SkippableFact]
    public async Task R06b_refresh_still_has_a_ceiling_per_address()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        await using var small = Root!.WithWebHostBuilder(b => b.UseSetting("RateLimits:RefreshPermits", "3"));
        var client = small.CreateClient();

        var token = a.RefreshToken;
        var codes = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var r = await client.SendAsync(Req(HttpMethod.Post, "/api/auth/refresh", "198.51.100.88", new { refreshToken = token }));
            codes.Add((int)r.StatusCode);
            if (r.IsSuccessStatusCode) token = (await JsonAsync(r)).GetProperty("refreshToken").GetString()!;
        }
        output.WriteLine($"refresh chain under RefreshPermits=3: {string.Join(",", codes)}");
        Assert.Equal(new[] { 200, 200, 200, 429 }, codes);
    }

    [SkippableFact]
    public async Task R09_one_account_is_held_to_ten_tries_from_any_number_of_addresses()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var victim = await RegisterAsync();
        // Production defaults: 10 per account per 15 minutes.
        await using var prod = Root!.WithWebHostBuilder(b =>
            b.UseSetting("RateLimits:AuthenticationPermits", "100"));
        var client = prod.CreateClient();

        var codes = new List<int>();
        for (var i = 0; i < 11; i++)
            codes.Add((int)(await client.SendAsync(Req(HttpMethod.Post, "/api/auth/login", $"203.0.113.{100 + i}",
                new { email = victim.Email, password = "wrong-password-123" }))).StatusCode);
        var right = await client.SendAsync(Req(HttpMethod.Post, "/api/auth/login", "203.0.113.200",
            new { email = victim.Email, password = "correct-horse-battery" }));
        var other = await client.SendAsync(Req(HttpMethod.Post, "/api/auth/login", "203.0.113.200",
            new { email = $"someone-{Guid.NewGuid():N}@test.dev", password = "wrong-password-123" }));
        output.WriteLine($"11 wrong guesses from 11 addresses: {string.Join(",", codes)}; right password after: {(int)right.StatusCode}; other account: {(int)other.StatusCode}");

        Assert.All(codes.Take(10), c => Assert.Equal(401, c));
        Assert.Equal(429, codes[10]);
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode); // locked for the window, like before
        Assert.InRange(right.Headers.RetryAfter?.Delta?.TotalSeconds ?? 0, 1, 900);
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);   // other accounts unaffected
    }

    [SkippableFact]
    public async Task R10_a_refusal_has_the_error_shape_and_says_how_long_to_wait()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        for (var i = 0; i < 5; i++) await a.Client.GetAsync($"/api/words/lookup?q=z{i}");
        var refused = await a.Client.GetAsync("/api/words/lookup?q=zz");
        var body = await refused.Content.ReadAsStringAsync();
        output.WriteLine($"429 body={body} Retry-After={refused.Headers.RetryAfter}");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Contains("RATE_LIMITED", body);
        Assert.NotNull(refused.Headers.RetryAfter?.Delta);
        Assert.InRange(refused.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
    }

    [SkippableFact]
    public async Task R07_global_budget_caps_everything_per_learner()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var codes = new List<int>();
        for (var i = 0; i < 42; i++) codes.Add((int)(await a.Client.GetAsync("/api/hub")).StatusCode);
        var first429 = codes.IndexOf(429);
        output.WriteLine($"hub x42 under Global=40: first 429 at request #{first429 + 1}; 200s={codes.Count(c => c == 200)}");
        // Registration itself is one global permit for the anonymous IP partition,
        // not the learner's, so the learner's 41st call is the first refused.
        Assert.Equal(40, codes.Count(c => c == 200));
        Assert.Equal(429, codes[40]);
    }

    [SkippableFact]
    public async Task R08_a_lookup_budget_comes_back_after_the_window()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        for (var i = 0; i < 5; i++) await a.Client.GetAsync($"/api/words/lookup?q=w{i}");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await a.Client.GetAsync("/api/words/lookup?q=w")).StatusCode);

        // Fixed one-minute window on the real clock (the limiter does not use
        // the fake TimeProvider). 61 s is the bounded wait this test declares.
        await Task.Delay(TimeSpan.FromSeconds(61));
        var after = await a.Client.GetAsync("/api/words/lookup?q=back");
        output.WriteLine($"after 61 s: {(int)after.StatusCode}");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }
}
