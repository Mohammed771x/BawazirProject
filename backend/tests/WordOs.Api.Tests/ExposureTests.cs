using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Lexicon;
using WordOs.Domain.Words;

namespace WordOs.Api.Tests;

/// <summary>
/// Exposure counting, over real HTTP against real PostgreSQL.
/// </summary>
/// <remarks>
/// Exposure feeds archiving (rule R8), so an inflated count can retire a word
/// the learner barely met. These tests pin both halves of that: a word actually
/// reused in generated content counts, and nothing else does — not a second
/// mention, not a second turn, and not anything a client says.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class ExposureTests(PostgresFixture db) : IAsyncLifetime
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

    private FakeClock Clock => _factory!.Clock;

    private StubAiContentService Ai => _factory!.Ai;

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private async Task<Guid> SignInAsync()
    {
        var response = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"exposure-{Guid.NewGuid():N}@test.dev",
            password = "correct-horse-battery",
            displayName = "Learner",
            phoneCountryCode = "967",
            phoneNumber = "770000001",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", body.GetProperty("token").GetString());

        return body.GetProperty("user").GetProperty("id").GetGuid();
    }

    private async Task<Guid> AddWordAsync(string text, string meaning)
    {
        var senseId = $"exp-{Guid.NewGuid():N}";
        await using (var context = db.CreateContext())
        {
            context.LexiconEntries.Add(LexiconEntry.Create(
                senseId, text, text, "n", $"the meaning of {text}", meaning,
                CefrLevel.B1, 1, "en=oewn;ar=awn", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        var response = await Client.PostAsJsonAsync("/api/words", new { senseId });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Drives a word all the way to Active, which is the only state whose words
    /// are offered back to the generator.
    /// </summary>
    private async Task<Guid> MakeActiveAsync(string text, string meaning)
    {
        var wordId = await AddWordAsync(text, meaning);

        // Reading → Listening → Speaking → Writing → Spelling, two days apart.
        await FinishQuestionSessionAsync("reading");
        Clock.SkipDays(2);
        await FinishQuestionSessionAsync("listening");
        Clock.SkipDays(2);
        await FinishSpeakingAsync();
        Clock.SkipDays(2);
        await FinishWritingAsync();
        Clock.SkipDays(2);
        await FinishQuestionSessionAsync("spelling");

        await using var context = db.CreateContext();
        var word = await context.Words.FirstAsync(w => w.Id == wordId);
        Assert.Equal(WordState.Active, word.State);
        return wordId;
    }

    private async Task<JsonElement> StartAsync(string skill)
    {
        var response = await Client.PostAsync($"/api/sessions/{skill}/start", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task FinishQuestionSessionAsync(string skill)
    {
        var session = await StartAsync(skill);
        var sessionId = session.GetProperty("id").GetGuid();
        var next = session.GetProperty("progress").GetProperty("nextItemId");

        while (next.ValueKind != JsonValueKind.Null)
        {
            var itemId = next.GetGuid();
            string answer;
            await using (var context = db.CreateContext())
            {
                answer = (await context.SessionItems.FirstAsync(i => i.Id == itemId))
                    .CorrectAnswer;
            }

            var result = await Client.PostAsJsonAsync(
                $"/api/sessions/{sessionId}/answer", new { itemId, answer });
            result.EnsureSuccessStatusCode();

            next = (await result.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("progress").GetProperty("nextItemId");
        }

        (await Client.PostAsync($"/api/sessions/{sessionId}/complete", null))
            .EnsureSuccessStatusCode();
    }

    private async Task FinishWritingAsync()
    {
        var session = await StartAsync("writing");
        var sessionId = session.GetProperty("id").GetGuid();

        foreach (var item in session.GetProperty("items").EnumerateArray())
        {
            var word = session.GetProperty("targetWords").EnumerateArray()
                .First(w => w.GetProperty("wordId").GetGuid()
                            == item.GetProperty("wordId").GetGuid())
                .GetProperty("text").GetString();

            (await Client.PostAsJsonAsync($"/api/sessions/{sessionId}/writing", new
            {
                itemId = item.GetProperty("id").GetGuid(),
                answer = $"Yesterday I used {word} in a real sentence.",
            })).EnsureSuccessStatusCode();
        }

        (await Client.PostAsync($"/api/sessions/{sessionId}/complete", null))
            .EnsureSuccessStatusCode();
    }

    private async Task FinishSpeakingAsync()
    {
        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        foreach (var word in session.GetProperty("targetWords").EnumerateArray())
        {
            (await Client.PostAsJsonAsync(
                $"/api/sessions/{sessionId}/speaking/turn", new
                {
                    transcript = $"I think {word.GetProperty("text").GetString()} "
                                 + "is interesting and useful to me.",
                })).EnsureSuccessStatusCode();
        }

        (await Client.PostAsync($"/api/sessions/{sessionId}/complete", null))
            .EnsureSuccessStatusCode();
    }

    private async Task<int> ExposureCountAsync(Guid wordId)
    {
        await using var context = db.CreateContext();
        return (await context.Words.FirstAsync(w => w.Id == wordId)).ExposureCount;
    }

    private async Task<List<WordExposure>> ExposuresAsync(Guid wordId)
    {
        await using var context = db.CreateContext();
        return await context.WordExposures
            .Where(e => e.WordId == wordId)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync();
    }

    // ── The rule ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task An_active_word_reused_in_generated_content_gains_one_exposure()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var activeId = await MakeActiveAsync("research", "بحث علمي");

        var before = await ExposureCountAsync(activeId);

        // A new word starts its own pipeline, and the passage generated for it
        // reuses the Active vocabulary.
        await AddWordAsync("theory", "نظرية");
        await StartAsync("reading");

        Assert.Contains("research", Ai.LastReuseWords);
        Assert.Equal(before + 1, await ExposureCountAsync(activeId));

        var exposures = await ExposuresAsync(activeId);
        Assert.Contains(exposures, e => e.Source == ExposureSource.AiContentReuse);
    }

    [SkippableFact]
    public async Task A_word_the_generator_did_not_use_gains_nothing()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var activeId = await MakeActiveAsync("research", "بحث علمي");

        var before = await ExposureCountAsync(activeId);

        // Offering a word is not using it — the generator may reasonably decide
        // it does not fit, and then the learner never met it.
        Ai.ReuseActiveWords = false;
        await AddWordAsync("theory", "نظرية");
        await StartAsync("reading");

        Assert.Contains("research", Ai.LastReuseWords);
        Assert.Equal(before, await ExposureCountAsync(activeId));
    }

    [SkippableFact]
    public async Task Repeating_a_word_within_one_passage_is_still_one_exposure()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var activeId = await MakeActiveAsync("research", "بحث علمي");

        var before = await ExposureCountAsync(activeId);

        Ai.RepeatReusedWords = 4;
        await AddWordAsync("theory", "نظرية");
        await StartAsync("reading");

        Assert.Equal(before + 1, await ExposureCountAsync(activeId));
    }

    [SkippableFact]
    public async Task Every_turn_of_one_conversation_is_still_one_exposure()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var activeId = await MakeActiveAsync("research", "بحث علمي");

        // Reach Speaking with a second word, with the AI mentioning the Active
        // word in every single reply. The Reading and Listening passages on the
        // way each credit their own exposure — they are separate encounters —
        // so the baseline is taken immediately before the conversation.
        Ai.SpeakingReuseWord = "research";
        await AddWordAsync("theory", "نظرية");
        await FinishQuestionSessionAsync("reading");
        Clock.SkipDays(2);
        await FinishQuestionSessionAsync("listening");
        Clock.SkipDays(2);

        var before = await ExposureCountAsync(activeId);
        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        for (var i = 0; i < 4; i++)
        {
            (await Client.PostAsJsonAsync(
                $"/api/sessions/{sessionId}/speaking/turn",
                new { transcript = "I think theory is useful and interesting." }))
                .EnsureSuccessStatusCode();
        }

        // The opening line plus four replies all named it; the learner met it
        // once, in one conversation.
        Assert.Equal(before + 1, await ExposureCountAsync(activeId));

        var thisSession = (await ExposuresAsync(activeId))
            .Count(e => e.SourceId == sessionId);
        Assert.Equal(1, thisSession);
    }

    [SkippableFact]
    public async Task A_later_session_is_a_new_encounter_and_counts_again()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var activeId = await MakeActiveAsync("research", "بحث علمي");

        var before = await ExposureCountAsync(activeId);

        await AddWordAsync("theory", "نظرية");
        await FinishQuestionSessionAsync("reading");
        Clock.SkipDays(2);
        await StartAsync("listening");

        // Two separate sessions, two genuine encounters — this is the signal
        // working, not double counting.
        Assert.Equal(before + 2, await ExposureCountAsync(activeId));
        Assert.Equal(2, (await ExposuresAsync(activeId))
            .Count(e => e.Source == ExposureSource.AiContentReuse));
    }

    [SkippableFact]
    public async Task Resuming_or_answering_never_adds_an_exposure()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var activeId = await MakeActiveAsync("research", "بحث علمي");

        await AddWordAsync("theory", "نظرية");
        var session = await StartAsync("reading");
        var sessionId = session.GetProperty("id").GetGuid();

        var afterStart = await ExposureCountAsync(activeId);

        // Re-reading the session replays stored content; nothing was generated.
        for (var i = 0; i < 3; i++)
        {
            (await Client.GetAsync($"/api/sessions/{sessionId}"))
                .EnsureSuccessStatusCode();
        }

        var itemId = session.GetProperty("progress")
            .GetProperty("nextItemId").GetGuid();
        (await Client.PostAsJsonAsync($"/api/sessions/{sessionId}/answer",
            new { itemId, answer = "whatever" })).EnsureSuccessStatusCode();

        Assert.Equal(afterStart, await ExposureCountAsync(activeId));
    }

    [SkippableFact]
    public async Task A_requeued_review_answer_does_not_count_twice()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var wordId = await AddWordAsync("research", "بحث علمي");

        var start = await Client.PostAsync("/api/weekly-review/start", null);
        start.EnsureSuccessStatusCode();
        var review = await start.Content.ReadFromJsonAsync<JsonElement>();
        var reviewId = review.GetProperty("id").GetGuid();
        var item = review.GetProperty("queue").EnumerateArray().First();
        var itemId = item.GetProperty("id").GetGuid();

        var options = item.GetProperty("options").EnumerateArray()
            .Select(o => o.GetString()!).ToList();

        string correct;
        await using (var context = db.CreateContext())
        {
            correct = (await context.WeeklyReviewItems.FirstAsync(i => i.Id == itemId))
                .CorrectAnswer;
        }

        // Wrong, then right — the item was answered twice, but met once.
        (await Client.PostAsJsonAsync($"/api/weekly-review/{reviewId}/answer",
            new { itemId, answer = options.First(o => o != correct) }))
            .EnsureSuccessStatusCode();
        (await Client.PostAsJsonAsync($"/api/weekly-review/{reviewId}/answer",
            new { itemId, answer = correct })).EnsureSuccessStatusCode();

        Assert.Equal(1, await ExposureCountAsync(wordId));
        Assert.Equal(1, (await ExposuresAsync(wordId))
            .Count(e => e.Source == ExposureSource.WeeklyReview));
    }

    // ── The client never gets a say ──────────────────────────────────────────

    [SkippableFact]
    public async Task A_client_cannot_submit_an_exposure_count()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = $"exp-{Guid.NewGuid():N}";
        await using (var context = db.CreateContext())
        {
            context.LexiconEntries.Add(LexiconEntry.Create(
                senseId, "research", "research", "n", "careful study", "بحث علمي",
                CefrLevel.B1, 1, "en=oewn;ar=awn", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        // Every field a client might hope to seed, sent together.
        var response = await Client.PostAsJsonAsync("/api/words", new
        {
            senseId,
            exposureCount = 9999,
            state = "ACTIVE",
            cefrLevel = "C2",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var wordId = body.GetProperty("id").GetGuid();

        await using (var context = db.CreateContext())
        {
            var word = await context.Words.FirstAsync(w => w.Id == wordId);

            // Everything is re-derived from the lexicon and the rules.
            Assert.Equal(0, word.ExposureCount);
            Assert.Equal(WordState.Learning, word.State);
            Assert.Equal(CefrLevel.B1, word.CefrLevel);
        }

        // And there is no route that takes one, under any verb.
        foreach (var (method, path) in new (HttpMethod, string)[]
                 {
                     (HttpMethod.Post, $"/api/words/{wordId}/exposure"),
                     (HttpMethod.Patch, $"/api/words/{wordId}/exposure"),
                     (HttpMethod.Post, "/api/exposure"),
                 })
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = JsonContent.Create(new { exposureCount = 50 }),
            };
            var attempt = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotFound, attempt.StatusCode);
        }

        Assert.Equal(0, await ExposureCountAsync(wordId));
    }
}
