using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Lexicon;
using WordOs.Domain.Words;

namespace WordOs.Api.Tests;

/// <summary>
/// What the phone is told to say, and when (ADR-076).
/// </summary>
/// <remarks>
/// The reminders are local notifications: the device fires them with no network
/// and usually with the app closed, so every word in them is decided here,
/// days ahead. That makes this endpoint the whole of the feature — a bug in it
/// is a learner being told they have four words ready on a morning they have
/// none, and there is no second chance to correct it once the alarm is set.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class DailyReminderTests(PostgresFixture db) : IAsyncLifetime
{
    private ApiFactory? _factory;
    private HttpClient? _client;
    private string _email = "";

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

    private DateTimeOffset Now => _factory!.Clock.GetUtcNow();

    private static readonly WordOsConfiguration Config = new();

    [SkippableFact]
    public async Task A_learner_with_no_words_is_asked_for_their_first_one()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var reminders = await FetchAsync();

        Assert.NotEmpty(reminders);
        // "You have 0 words ready" is a true sentence and a useless one. Never
        // having started is a different situation from having nothing due, and
        // it gets a different key so the phone can say a different thing.
        Assert.All(reminders, r =>
            Assert.Equal("NO_WORDS", r.GetProperty("kind").GetString()));
    }

    [SkippableFact]
    public async Task Both_times_of_day_are_sent_for_every_day_of_the_horizon()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var reminders = await FetchAsync();

        var dates = reminders
            .Select(r => r.GetProperty("date").GetString())
            .Distinct()
            .ToList();

        Assert.Equal(Config.ReminderHorizonDays, dates.Count);

        // One morning and one evening per day — except today, which may have
        // lost one or both to the clock.
        foreach (var day in reminders.GroupBy(r => r.GetProperty("date").GetString()))
        {
            var slots = day.Select(r => r.GetProperty("slot").GetString()).ToList();
            Assert.Equal(slots.Count, slots.Distinct().Count());
        }

        Assert.Contains(reminders, r => r.GetProperty("slot").GetString() == "MORNING");
        Assert.Contains(reminders, r => r.GetProperty("slot").GetString() == "EVENING");
    }

    [SkippableFact]
    public async Task A_time_that_has_already_gone_by_is_never_sent()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // A phone handed a past time either fires it at once — a notification
        // the learner never asked for, at the moment they opened the app — or
        // drops it silently. Neither is a reminder, so it is not sent.
        foreach (var reminder in await FetchAsync())
        {
            Assert.True(InstantOf(reminder) > Now,
                $"a reminder was scheduled for {reminder.GetProperty("date")} "
                + $"{reminder.GetProperty("hour")}:00, which is in the past");
        }
    }

    [SkippableFact]
    public async Task Each_day_carries_the_count_that_will_be_true_on_it()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // A word that has passed Reading and is waiting out the gap before
        // Listening. It is in the pipeline and it is not due — and in two days
        // it will be, on a date this endpoint can already name.
        await SeedPassedFirstSkillAsync("plough", "محراث");

        var reminders = await FetchAsync();
        var dueAt = Now.AddDays(Config.SkillIntervalDays);

        foreach (var reminder in reminders)
        {
            var kind = reminder.GetProperty("kind").GetString();

            if (InstantOf(reminder) < dueAt)
            {
                // Nothing to practise yet, but they have a word — so this is
                // "keep going", not "you have not started".
                Assert.Equal("NOTHING_DUE", kind);
            }
            else
            {
                Assert.Equal("WORDS_DUE", kind);
                Assert.Equal(1, reminder.GetProperty("count").GetInt32());
            }
        }

        // Both halves must actually appear, or this test would pass on an
        // endpoint that always said one of them.
        Assert.Contains(reminders, r => r.GetProperty("kind").GetString() == "NOTHING_DUE");
        Assert.Contains(reminders, r => r.GetProperty("kind").GetString() == "WORDS_DUE");
    }

    [SkippableFact]
    public async Task A_word_due_now_is_counted_on_every_reminder()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = await SeedLexiconAsync("anvil", "سندان");
        var added = await Client.PostAsJsonAsync("/api/words", new { senseId });
        added.EnsureSuccessStatusCode();

        Assert.All(await FetchAsync(), r =>
        {
            Assert.Equal("WORDS_DUE", r.GetProperty("kind").GetString());
            Assert.Equal(1, r.GetProperty("count").GetInt32());
        });
    }

    [SkippableFact]
    public async Task Another_learners_words_are_never_counted()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = await SeedLexiconAsync("kiln", "فرن");
        (await Client.PostAsJsonAsync("/api/words", new { senseId }))
            .EnsureSuccessStatusCode();

        // A second account on the same server sees its own emptiness.
        await SignInAsync();

        Assert.All(await FetchAsync(), r =>
            Assert.Equal("NO_WORDS", r.GetProperty("kind").GetString()));
    }

    [SkippableFact]
    public async Task Reminders_are_not_served_to_a_stranger()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        Client.DefaultRequestHeaders.Authorization = null;
        var response = await Client.GetAsync("/api/notifications/daily");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<List<JsonElement>> FetchAsync()
    {
        var body = await Client.GetFromJsonAsync<JsonElement>("/api/notifications/daily");
        return body.GetProperty("reminders").EnumerateArray().ToList();
    }

    /// <summary>
    /// When a reminder actually fires, as an instant.
    /// </summary>
    /// <remarks>
    /// The endpoint sends a wall-clock date and hour, because the phone
    /// schedules in its own timezone. Reading it back as an instant needs the
    /// same offset the server counted against — the product's reporting offset,
    /// which is the one documented decision about what "a day" means here.
    /// </remarks>
    private static DateTimeOffset InstantOf(JsonElement reminder)
    {
        var date = DateOnly.Parse(reminder.GetProperty("date").GetString()!);

        return new DateTimeOffset(
            date.Year, date.Month, date.Day,
            reminder.GetProperty("hour").GetInt32(),
            reminder.GetProperty("minute").GetInt32(),
            0,
            TimeSpan.FromHours(Config.ReportingUtcOffsetHours));
    }

    private async Task<string> SeedLexiconAsync(string text, string meaningAr)
    {
        var senseId = $"rem-{Guid.NewGuid():N}";

        await using var context = db.CreateContext();
        context.LexiconEntries.Add(LexiconEntry.Create(
            senseId, text, text, "n", $"a {text}", meaningAr,
            CefrLevel.A1, 1, "en=wordos-test;ar=wordos-test",
            DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();

        return senseId;
    }

    /// <summary>
    /// A word mid-pipeline: first skill passed, waiting out the spaced gap.
    /// </summary>
    /// <remarks>
    /// Written through the domain rather than by setting columns, so the gap is
    /// the real one — the point of the test is that the endpoint agrees with
    /// the pipeline about when a word becomes due, and a hand-set date would
    /// only prove it agrees with the test.
    /// </remarks>
    private async Task SeedPassedFirstSkillAsync(string text, string meaningAr)
    {
        await using var context = db.CreateContext();
        var user = await context.Users.FirstAsync(u => u.Email == _email);

        var word = Word.Add(
            user.Id, $"rem-{Guid.NewGuid():N}", text, meaningAr,
            $"a {text}", "n", CefrLevel.A1, Config, Now);

        word.ApplySessionResult(Config.FirstSkill, passed: true, Config, Now);

        context.Words.Add(word);
        await context.SaveChangesAsync();
    }

    private async Task SignInAsync()
    {
        _email = $"rem-{Guid.NewGuid():N}@test.dev";

        var response = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email = _email,
            password = "correct-horse-battery",
            displayName = "Learner",
            phoneCountryCode = "967",
            phoneNumber = "770000002",
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", body.GetProperty("token").GetString());
    }
}
