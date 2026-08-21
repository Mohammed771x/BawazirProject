using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace WordOs.Api.Tests;

/// <summary>
/// A learner writing to the Owner, and the Owner reading it (ADR-053).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FeedbackTests(PostgresFixture db) : IAsyncLifetime
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

    private async Task<(Guid Id, string Email)> SignInAsync(string prefix)
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@test.dev";
        var response = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = "correct-horse-battery",
            displayName = "Learner",
            phoneCountryCode = "967",
            phoneNumber = "771234567",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());

        return (body.GetProperty("user").GetProperty("id").GetGuid(), email);
    }

    private async Task SignInAsOwnerAsync(string prefix)
    {
        var email = $"{prefix}-{Guid.NewGuid():N}@test.dev";
        (await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = "correct-horse-battery",
            displayName = "Owner",
            phoneCountryCode = "967",
            phoneNumber = "770000002",
        })).EnsureSuccessStatusCode();

        await using (var context = db.CreateContext())
        {
            await context.Database.ExecuteSqlAsync(
                $"""UPDATE users SET "Role" = 'Owner' WHERE "Email" = {email}""");
        }

        var login = await Client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = "correct-horse-battery",
        });
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();

        Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
    }

    [SkippableFact]
    public async Task A_learner_can_reach_the_owner_and_the_owner_can_reach_them_back()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var (_, email) = await SignInAsync("reporter");

        var sent = await Client.PostAsJsonAsync("/api/feedback", new
        {
            body = "The speaking screen freezes when I press the microphone.",
            appVersion = "1.2.0",
            platform = "ios",
        });
        sent.EnsureSuccessStatusCode();

        await SignInAsOwnerAsync("inbox-owner");

        var inbox = await Client.GetFromJsonAsync<JsonElement>("/api/admin/feedback");
        var message = inbox.GetProperty("items").EnumerateArray()
            .First(m => m.GetProperty("user").GetProperty("email").GetString() == email);

        // Their words, unedited.
        Assert.Equal(
            "The speaking screen freezes when I press the microphone.",
            message.GetProperty("body").GetString());
        Assert.Equal("NEW", message.GetProperty("status").GetString());

        // And how to answer them — the point of the whole feature: the Owner
        // reads a report and can contact the person who wrote it.
        var user = message.GetProperty("user");
        Assert.Equal(email, user.GetProperty("email").GetString());
        Assert.Equal("771234567", user.GetProperty("phoneNumber").GetString());
        Assert.Equal("967", user.GetProperty("phoneCountryCode").GetString());

        // The build it came from, so "it crashed" arrives with a version.
        Assert.Equal("1.2.0", message.GetProperty("appVersion").GetString());
    }

    [SkippableFact]
    public async Task Marking_a_message_handled_can_be_undone()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        await SignInAsync("undo-reporter");
        (await Client.PostAsJsonAsync("/api/feedback",
            new { body = "A suggestion about the word list." }))
            .EnsureSuccessStatusCode();

        await SignInAsOwnerAsync("undo-owner");

        var inbox = await Client.GetFromJsonAsync<JsonElement>(
            "/api/admin/feedback?status=NEW");
        var id = inbox.GetProperty("items").EnumerateArray().First()
            .GetProperty("id").GetGuid();

        var handled = await Client.PatchAsJsonAsync(
            $"/api/admin/feedback/{id}", new { handled = true });
        handled.EnsureSuccessStatusCode();
        Assert.Equal("HANDLED",
            (await handled.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("status").GetString());

        // Reversible, because an Owner reading a long list will mark the wrong
        // one eventually and a message that cannot come back is lost.
        var reopened = await Client.PatchAsJsonAsync(
            $"/api/admin/feedback/{id}", new { handled = false });
        reopened.EnsureSuccessStatusCode();
        Assert.Equal("NEW",
            (await reopened.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("status").GetString());
    }

    [SkippableFact]
    public async Task A_learner_can_write_but_can_never_read()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        await SignInAsync("nosy");

        (await Client.PostAsJsonAsync("/api/feedback",
            new { body = "Something to say." })).EnsureSuccessStatusCode();

        // Not their own messages back, and certainly not anybody else's. The
        // API refuses regardless of what any client shows
        // (docs/07-SECURITY.md §3).
        var read = await Client.GetAsync("/api/admin/feedback");
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);

        var mark = await Client.PatchAsJsonAsync(
            $"/api/admin/feedback/{Guid.NewGuid()}", new { handled = true });
        Assert.Equal(HttpStatusCode.Forbidden, mark.StatusCode);
    }

    [SkippableFact]
    public async Task An_unauthenticated_caller_cannot_write_at_all()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        Client.DefaultRequestHeaders.Authorization = null;

        var response = await Client.PostAsJsonAsync("/api/feedback",
            new { body = "anonymous" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    // A NUL byte: the one character PostgreSQL refuses outright, so it has to
    // be caught at the edge rather than become a 500 (ADR-036). Written as an
    // escape — a raw NUL inside a source file is invisible and does not survive
    // an editor.
    [InlineData("\0")]
    public async Task Unwritable_text_is_refused_not_stored(string body)
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        await SignInAsync("blank");

        var response = await Client.PostAsJsonAsync("/api/feedback", new { body });

        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest
                or HttpStatusCode.UnprocessableEntity,
            $"expected a 4xx, got {(int)response.StatusCode}");
    }

    [SkippableFact]
    public async Task A_message_longer_than_the_field_allows_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        await SignInAsync("verbose");

        var response = await Client.PostAsJsonAsync("/api/feedback",
            new { body = new string('x', 5000) });

        // Bounded at the edge: an unbounded write of free text is how a table
        // fills up overnight.
        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest
                or HttpStatusCode.UnprocessableEntity,
            $"expected a 4xx, got {(int)response.StatusCode}");
    }

    [SkippableFact]
    public async Task Sending_feedback_is_written_to_the_activity_log()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var (learnerId, _) = await SignInAsync("logged");

        (await Client.PostAsJsonAsync("/api/feedback",
            new { body = "Worth recording." })).EnsureSuccessStatusCode();

        await using var context = db.CreateContext();

        // Recorded like everything else a learner does, so "did anyone report
        // anything the day it broke?" is answerable from the same trail
        // (ADR-025) — without copying their words into a log that deliberately
        // holds no free text.
        var events = await context.ActivityEvents
            .Where(e => e.UserId == learnerId)
            .ToListAsync();

        Assert.Contains(events, e =>
            e.Type == Domain.Users.ActivityType.FeedbackSent);
    }
}
