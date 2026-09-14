using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace WordOs.Api.Tests;

/// <summary>
/// Forgotten-password recovery (ADR-078).
/// </summary>
/// <remarks>
/// Two kinds of claim live here and they are worth telling apart. Some are
/// about the feature working — a learner who forgot their password can get back
/// in. The rest are about what the feature must refuse to leak: whether an
/// address is registered, how many guesses a code will take, and whether a
/// stolen code outlives its use. The second kind is the reason this file is
/// long.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class PasswordResetTests(PostgresFixture db) : IAsyncLifetime
{
    private const string OriginalPassword = "correct-horse-battery";
    private const string NewPassword = "a-completely-different-one";

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
    private ApiFactory Factory => _factory!;

    private static string Unique(string prefix) =>
        $"{prefix}-{Guid.NewGuid():N}@test.dev";

    private async Task<string> RegisterAsync()
    {
        var email = Unique("learner");
        var response = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = OriginalPassword,
            displayName = "Learner",
            phoneCountryCode = "967",
            phoneNumber = "770000001",
        });
        response.EnsureSuccessStatusCode();
        return email;
    }

    private Task<HttpResponseMessage> ForgotAsync(string email) =>
        Client.PostAsJsonAsync("/api/auth/password/forgot", new { email });

    private Task<HttpResponseMessage> ResetAsync(
        string email, string code, string password = NewPassword) =>
        Client.PostAsJsonAsync("/api/auth/password/reset", new
        {
            email,
            code,
            newPassword = password,
        });

    private Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        Client.PostAsJsonAsync("/api/auth/login", new { email, password });

    /// <summary>Requests a code and reads it out of the email, as a learner does.</summary>
    private async Task<string> RequestCodeAsync(string email)
    {
        Factory.Email.Clear();
        (await ForgotAsync(email)).EnsureSuccessStatusCode();
        return Factory.Email.LastCode
            ?? throw new InvalidOperationException("No code was emailed.");
    }

    // ── The feature ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_learner_who_forgot_their_password_can_set_a_new_one()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var email = await RegisterAsync();
        var code = await RequestCodeAsync(email);

        var reset = await ResetAsync(email, code);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        // The point of the whole exercise.
        var fresh = await LoginAsync(email, NewPassword);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);

        // And the password they forgot is genuinely gone, not merely bypassed.
        var stale = await LoginAsync(email, OriginalPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
    }

    [SkippableFact]
    public async Task The_reset_does_not_hand_back_a_session()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var email = await RegisterAsync();
        var code = await RequestCodeAsync(email);

        var reset = await ResetAsync(email, code);
        var body = await reset.Content.ReadFromJsonAsync<JsonElement>();

        // Whoever holds the code must still know the new password to get in.
        // Returning tokens here would make one intercepted email a full
        // account takeover.
        Assert.False(body.TryGetProperty("token", out _));
        Assert.False(body.TryGetProperty("refreshToken", out _));
    }

    [SkippableFact]
    public async Task Resetting_signs_out_every_other_device()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var email = await RegisterAsync();

        // A session that already exists — the other phone, or the thief's.
        var signedIn = await LoginAsync(email, OriginalPassword);
        var session = await signedIn.Content.ReadFromJsonAsync<JsonElement>();
        var refreshToken = session.GetProperty("refreshToken").GetString()!;

        var code = await RequestCodeAsync(email);
        (await ResetAsync(email, code)).EnsureSuccessStatusCode();

        // A reset exists to answer "someone else knows my password". Stopping
        // future sign-ins while leaving their current session alive would
        // answer it only halfway.
        var refresh = await Client.PostAsJsonAsync(
            "/api/auth/refresh", new { refreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    // ── What it must not leak ───────────────────────────────────────────────

    [SkippableFact]
    public async Task An_unknown_address_is_answered_exactly_like_a_known_one()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var registered = await RegisterAsync();

        var known = await ForgotAsync(registered);
        var knownBody = await known.Content.ReadAsStringAsync();

        Factory.Email.Clear();
        var unknown = await ForgotAsync(Unique("nobody"));
        var unknownBody = await unknown.Content.ReadAsStringAsync();

        // Byte-identical. Anything that differs — a status, a message, a
        // field — turns this endpoint into a way to ask "does this person
        // have an account here".
        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(knownBody, unknownBody);

        // And nothing was sent to an address nobody registered.
        Assert.Empty(Factory.Email.Sent);
    }

    [SkippableFact]
    public async Task A_provider_outage_is_answered_the_same_way_too()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var email = await RegisterAsync();

        var healthy = await ForgotAsync(email);
        var healthyBody = await healthy.Content.ReadAsStringAsync();

        Factory.Email.Fails = true;
        var broken = await ForgotAsync(email);
        var brokenBody = await broken.Content.ReadAsStringAsync();

        // The subtle one. If a failed send produced a 500 while a successful
        // send produced a 202, then an attacker could tell registered
        // addresses from unregistered ones by watching which requests error —
        // because only a registered address causes a send at all.
        Assert.Equal(healthy.StatusCode, broken.StatusCode);
        Assert.Equal(healthyBody, brokenBody);
    }

    [SkippableFact]
    public async Task A_wrong_email_and_a_wrong_code_are_refused_identically()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var email = await RegisterAsync();
        await RequestCodeAsync(email);

        var wrongCode = await ResetAsync(email, "000000");
        var wrongEmail = await ResetAsync(Unique("nobody"), "000000");

        Assert.Equal(HttpStatusCode.BadRequest, wrongCode.StatusCode);
        Assert.Equal(wrongCode.StatusCode, wrongEmail.StatusCode);
        Assert.Equal(
            await wrongCode.Content.ReadAsStringAsync(),
            await wrongEmail.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task The_code_is_never_stored_in_a_readable_form()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var email = await RegisterAsync();
        var code = await RequestCodeAsync(email);

        await using var context = db.CreateContext();
        var hashes = await context.PasswordResetCodes
            .Select(c => c.CodeHash)
            .ToListAsync();

        // A stolen database must not be a stack of working reset codes.
        Assert.NotEmpty(hashes);
        Assert.DoesNotContain(hashes, h => h.Contains(code));
    }

    // ── The limits that make six digits safe ────────────────────────────────

    [SkippableFact]
    public async Task A_code_works_only_once()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var email = await RegisterAsync();
        var code = await RequestCodeAsync(email);

        (await ResetAsync(email, code)).EnsureSuccessStatusCode();

        var again = await ResetAsync(email, code, "yet-another-password");
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        // The second attempt changed nothing.
        var login = await LoginAsync(email, NewPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [SkippableFact]
    public async Task Wrong_guesses_burn_the_code_before_a_million_can_be_tried()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var email = await RegisterAsync();
        var code = await RequestCodeAsync(email);

        // Five wrong guesses — the configured cap.
        for (var i = 0; i < 5; i++)
        {
            var wrong = await ResetAsync(email, WrongVersionOf(code));
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        }

        // The real code no longer opens it. This is the property that makes a
        // six-digit secret defensible at all: the search space is a million,
        // and this caps the search at five.
        var correct = await ResetAsync(email, code);
        Assert.Equal(HttpStatusCode.BadRequest, correct.StatusCode);

        var login = await LoginAsync(email, OriginalPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [SkippableFact]
    public async Task Asking_again_retires_the_previous_code()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var email = await RegisterAsync();
        var first = await RequestCodeAsync(email);
        var second = await RequestCodeAsync(email);

        Skip.If(first == second, "Two random codes collided; rerun.");

        // Otherwise a learner who taps the button three times has three live
        // codes and fifteen guesses, and the attempt cap above stops meaning
        // anything.
        var stale = await ResetAsync(email, first);
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);

        var current = await ResetAsync(email, second);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
    }

    [SkippableFact]
    public async Task An_expired_code_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var email = await RegisterAsync();
        var code = await RequestCodeAsync(email);

        // Past the fifteen-minute window.
        Factory.Clock.Advance(TimeSpan.FromMinutes(16));

        var late = await ResetAsync(email, code);
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);

        var login = await LoginAsync(email, OriginalPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [SkippableFact]
    public async Task A_code_shorter_than_six_digits_is_rejected_as_malformed()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var email = await RegisterAsync();
        await RequestCodeAsync(email);

        // Shape is checked before anything is looked up, so a client bug
        // cannot spend one of the five attempts (docs/07-SECURITY.md §5).
        foreach (var malformed in new[] { "123", "12345a", "" })
        {
            var response = await ResetAsync(email, malformed);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    /// <summary>A code that is certainly not the right one, same shape.</summary>
    private static string WrongVersionOf(string code)
    {
        var first = code[0] == '0' ? '1' : '0';
        return first + code[1..];
    }
}
