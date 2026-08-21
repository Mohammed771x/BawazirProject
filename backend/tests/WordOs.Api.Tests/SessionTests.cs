using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WordOs.Domain.Common;
using WordOs.Domain.Lexicon;
using WordOs.Application.Abstractions;
using WordOs.Domain.Users;
using WordOs.Domain.Words;

namespace WordOs.Api.Tests;

/// <summary>
/// The five skill sessions and the weekly review, over real HTTP against real
/// PostgreSQL.
/// </summary>
/// <remarks>
/// These assert the rules the specification is actually about: only a
/// first-attempt success passes a word, a wrong answer returns to the queue
/// instead of being discarded, failing one skill leaves the others alone, and
/// the weekly review changes nothing.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class SessionTests(PostgresFixture db) : IAsyncLifetime
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
            email = $"session-{Guid.NewGuid():N}@test.dev",
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
        var senseId = $"sess-{Guid.NewGuid():N}";
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

    private async Task<JsonElement> StartAsync(string skill)
    {
        var response = await Client.PostAsync($"/api/sessions/{skill}/start", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Answers the current item, correctly or not.</summary>
    private async Task<JsonElement> AnswerAsync(
        Guid sessionId, JsonElement item, bool correct)
    {
        var options = item.GetProperty("options").EnumerateArray()
            .Select(o => o.GetString()!).ToList();

        // The correct answer is never in the payload, so the test recovers it
        // from the database — exactly the position an honest client is in.
        string answer;
        await using (var context = db.CreateContext())
        {
            var itemId = item.GetProperty("id").GetGuid();
            var stored = await context.SessionItems.FirstAsync(i => i.Id == itemId);
            answer = correct
                ? stored.CorrectAnswer
                : options.First(o => o != stored.CorrectAnswer);
        }

        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/answer",
            new { itemId = item.GetProperty("id").GetGuid(), answer });

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement ItemById(JsonElement session, Guid id) =>
        session.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("id").GetGuid() == id);

    private static Guid CurrentItemId(JsonElement session) =>
        session.GetProperty("progress").GetProperty("nextItemId").GetGuid();

    /// <summary>Answers every remaining item correctly, then completes.</summary>
    private async Task<JsonElement> FinishAsync(JsonElement session)
    {
        var sessionId = session.GetProperty("id").GetGuid();

        // Re-read rather than trusting the payload the caller is holding: it may
        // already have answered some items.
        session = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/sessions/{sessionId}");
        var next = session.GetProperty("progress").GetProperty("nextItemId");

        while (next.ValueKind != JsonValueKind.Null)
        {
            var result = await AnswerAsync(
                sessionId, ItemById(session, next.GetGuid()), correct: true);
            next = result.GetProperty("progress").GetProperty("nextItemId");
        }

        var response = await Client.PostAsync(
            $"/api/sessions/{sessionId}/complete", null);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_reading_session_has_five_questions_then_one_per_word()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AddWordAsync("theory", "نظرية");

        var session = await StartAsync("reading");
        var items = session.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(5, items.Count(i =>
            i.GetProperty("type").GetString() == "COMPREHENSION"));
        Assert.Equal(2, items.Count(i =>
            i.GetProperty("type").GetString() == "TARGET_WORD"));

        // Reading shows the surrounding sentences so the meaning is inferred
        // rather than recalled (§25–27).
        var target = items.First(i =>
            i.GetProperty("type").GetString() == "TARGET_WORD");
        var context = target.GetProperty("context");
        Assert.Equal(JsonValueKind.Object, context.ValueKind);
        // camelCase, like every other field on the wire.
        Assert.False(string.IsNullOrWhiteSpace(
            context.GetProperty("sentence").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(
            context.GetProperty("before").GetString()));

        Assert.False(session.GetProperty("usedAiFallback").GetBoolean());
    }

    // ── Practice: the empty-pipeline fallback (Part 2 §5) ────────────────────

    [SkippableFact]
    public async Task Practice_is_offered_only_when_asked_for()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // Nothing due and no request to practise: the honest answer is still
        // "come back later". Practice must never be substituted silently for
        // the session the learner asked for.
        var refused = await Client.PostAsync("/api/sessions/reading/start", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var practice = await Client.PostAsync(
            "/api/sessions/reading/start?practice=true", null);
        practice.EnsureSuccessStatusCode();

        var body = await practice.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("isPractice").GetBoolean());
        Assert.Empty(body.GetProperty("targetWords").EnumerateArray());
        Assert.NotEmpty(body.GetProperty("items").EnumerateArray());

        // Comprehension only — there is no word to ask about.
        Assert.All(body.GetProperty("items").EnumerateArray(),
            i => Assert.Equal("COMPREHENSION", i.GetProperty("type").GetString()));
    }

    [SkippableFact]
    public async Task A_practice_session_leaves_the_pipeline_exactly_as_it_was()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();
        var wordId = await AddWordAsync("research", "بحث علمي");

        // Clear the word out of Reading so nothing is due there, then practise.
        await FinishAsync(await StartAsync("reading"));

        Domain.Levels.SkillLevel before;
        Domain.Words.Word wordBefore;
        await using (var context = db.CreateContext())
        {
            before = await context.SkillLevels.AsNoTracking()
                .FirstAsync(l => l.UserId == userId && l.Skill == SkillType.Reading);
            wordBefore = await context.Words.AsNoTracking()
                .Include(w => w.Skills)
                .FirstAsync(w => w.Id == wordId);
        }

        var session = await Client.PostAsync(
            "/api/sessions/reading/start?practice=true", null);
        session.EnsureSuccessStatusCode();
        var body = await session.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = body.GetProperty("id").GetGuid();

        // Answer everything wrong, which for a real session would fail words
        // and drag the level down.
        foreach (var item in body.GetProperty("items").EnumerateArray())
        {
            await Client.PostAsJsonAsync($"/api/sessions/{sessionId}/answer", new
            {
                itemId = item.GetProperty("id").GetGuid(),
                answer = "definitely not the answer",
            });
        }

        var complete = await Client.PostAsync(
            $"/api/sessions/{sessionId}/complete", null);
        complete.EnsureSuccessStatusCode();
        var result = await complete.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(result.GetProperty("isPractice").GetBoolean());
        Assert.Empty(result.GetProperty("words").EnumerateArray());

        await using var after = db.CreateContext();
        var levelAfter = await after.SkillLevels.AsNoTracking()
            .FirstAsync(l => l.UserId == userId && l.Skill == SkillType.Reading);
        var wordAfter = await after.Words.AsNoTracking()
            .Include(w => w.Skills)
            .FirstAsync(w => w.Id == wordId);

        // Nothing moved: not the word, not its schedule, not the level's
        // evidence. Practice measures nothing, which is what makes it safe to
        // offer on an empty day.
        Assert.Equal(wordBefore.State, wordAfter.State);
        Assert.Equal(wordBefore.CurrentSkill, wordAfter.CurrentSkill);
        Assert.Equal(
            wordBefore.SkillState(SkillType.Listening).AvailableAt,
            wordAfter.SkillState(SkillType.Listening).AvailableAt);
        Assert.Equal(before.SystemAssessedLevel, levelAfter.SystemAssessedLevel);
        Assert.Equal(before.EvaluationSessions, levelAfter.EvaluationSessions);
        Assert.Equal(before.RollingAccuracy, levelAfter.RollingAccuracy);
    }

    [SkippableFact]
    public async Task Practice_is_refused_for_the_skills_it_cannot_serve()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // Speaking, Writing and Spelling are *about* the words. Without any,
        // there is no activity to generate — only a blank screen with a title.
        foreach (var skill in new[] { "speaking", "writing", "spelling" })
        {
            var response = await Client.PostAsync(
                $"/api/sessions/{skill}/start?practice=true", null);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
    }

    [SkippableFact]
    public async Task The_passage_says_where_its_target_words_are()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        var session = await StartAsync("reading");
        var content = session.GetProperty("content");
        var text = content.GetProperty("text").GetString()!;
        var spans = content.GetProperty("targetSpans").EnumerateArray().ToList();

        // Without these the reading screen cannot underline the words the
        // session is about, and cannot tell a target word from an ordinary one
        // when the learner taps it (Part 2 §16, §19). A client that searched
        // the text itself would miss "researching" and "researched".
        Assert.NotEmpty(spans);
        foreach (var span in spans)
        {
            var start = span.GetProperty("start").GetInt32();
            var length = span.GetProperty("length").GetInt32();
            var slice = text.Substring(start, length);

            Assert.StartsWith("research", slice, StringComparison.OrdinalIgnoreCase);
        }
    }

    [SkippableFact]
    public async Task The_correct_answer_is_never_sent_with_the_question()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        var response = await Client.PostAsync("/api/sessions/reading/start", null);
        var raw = await response.Content.ReadAsStringAsync();

        // A client that could see the key could pass every session for free.
        Assert.DoesNotContain("correctAnswer", raw, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_wrong_answer_returns_to_the_queue_and_costs_the_pass()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var wordId = await AddWordAsync("research", "بحث علمي");

        var session = await StartAsync("reading");
        var sessionId = session.GetProperty("id").GetGuid();

        // Miss the target word once...
        var target = session.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("type").GetString() == "TARGET_WORD");
        var targetId = target.GetProperty("id").GetGuid();

        // ...but it is not the first item, so clear the comprehension queue
        // until the target comes up.
        var next = CurrentItemId(session);
        while (next != targetId)
        {
            var step = await AnswerAsync(
                sessionId, ItemById(session, next), correct: true);
            next = step.GetProperty("progress").GetProperty("nextItemId").GetGuid();
        }

        var wrong = await AnswerAsync(sessionId, target, correct: false);
        Assert.False(wrong.GetProperty("isCorrect").GetBoolean());
        Assert.True(wrong.GetProperty("requeued").GetBoolean());
        // Wrong is never discarded: the meaning is shown, and the item comes
        // back (§28, §48).
        Assert.False(string.IsNullOrWhiteSpace(
            wrong.GetProperty("explanation").GetString()));

        // The retry succeeds, so the session finishes...
        var retry = await AnswerAsync(sessionId, target, correct: true);
        Assert.True(retry.GetProperty("isCorrect").GetBoolean());

        var result = await FinishAsync(session);

        // ...but the word does not pass: only a first attempt counts (§31).
        var outcome = result.GetProperty("words").EnumerateArray()
            .First(w => w.GetProperty("wordId").GetGuid() == wordId);
        Assert.False(outcome.GetProperty("passed").GetBoolean());
        Assert.Equal("READING", outcome.GetProperty("nextSkill").GetString());
    }

    [SkippableFact]
    public async Task Passing_reading_schedules_listening_two_days_later()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var wordId = await AddWordAsync("research", "بحث علمي");

        var result = await FinishAsync(await StartAsync("reading"));

        var outcome = result.GetProperty("words").EnumerateArray().Single();
        Assert.True(outcome.GetProperty("passed").GetBoolean());
        Assert.Equal("LISTENING", outcome.GetProperty("nextSkill").GetString());

        var eligibleAt = outcome.GetProperty("nextEligibleAt").GetDateTimeOffset();
        Assert.Equal(2, (eligibleAt - Clock.GetUtcNow()).TotalDays, 1);

        // And the gap is enforced: the word is not available yet.
        var early = await Client.PostAsync("/api/sessions/listening/start", null);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);

        await using var context = db.CreateContext();
        var word = await context.Words.Include(w => w.Skills)
            .FirstAsync(w => w.Id == wordId);
        Assert.Equal(SkillType.Listening, word.CurrentSkill);
        Assert.Equal(SkillStatus.Passed,
            word.Skills.First(s => s.Skill == SkillType.Reading).Status);
    }

    // ── Listening ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Listening_supplies_audio_text_and_never_the_written_sentence()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        await FinishAsync(await StartAsync("reading"));
        Clock.SkipDays(2);

        var session = await StartAsync("listening");
        var target = session.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("type").GetString() == "TARGET_WORD");

        // Audio-first: the sentence exists to be spoken, not read (§32–34).
        Assert.False(string.IsNullOrWhiteSpace(
            target.GetProperty("audioText").GetString()));
        Assert.Equal(JsonValueKind.Null, target.GetProperty("context").ValueKind);
        Assert.True(session.GetProperty("content")
            .GetProperty("revealTextAfterTest").GetBoolean());
    }

    // ── Speaking → Writing (the order is fixed) ──────────────────────────────

    [SkippableFact]
    public async Task Speaking_comes_before_writing()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var wordId = await AddWordAsync("research", "بحث علمي");

        await FinishAsync(await StartAsync("reading"));
        Clock.SkipDays(2);
        await FinishAsync(await StartAsync("listening"));

        await using var context = db.CreateContext();
        var word = await context.Words.FirstAsync(w => w.Id == wordId);
        Assert.Equal(SkillType.Speaking, word.CurrentSkill);
    }

    [SkippableFact]
    public async Task A_speaking_session_is_a_conversation_with_turns()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        await FinishAsync(await StartAsync("reading"));
        Clock.SkipDays(2);
        await FinishAsync(await StartAsync("listening"));
        Clock.SkipDays(2);

        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        // It opens by speaking first, and asks no multiple-choice questions.
        Assert.Empty(session.GetProperty("items").EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(session.GetProperty("conversation")
            .GetProperty("opening").GetString()));

        var turn = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/speaking/turn",
            new { transcript = "I read a research paper about sleep last night." });

        turn.EnsureSuccessStatusCode();
        var body = await turn.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(string.IsNullOrWhiteSpace(
            body.GetProperty("aiMessage").GetString()));
        Assert.Contains("research", body.GetProperty("wordsUsed").EnumerateArray()
            .Select(w => w.GetString()));

        var complete = await Client.PostAsync(
            $"/api/sessions/{sessionId}/complete", null);
        complete.EnsureSuccessStatusCode();
        var result = await complete.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(result.GetProperty("words").EnumerateArray()
            .Single().GetProperty("passed").GetBoolean());
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Writing_asks_the_learner_to_use_the_word()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToWritingAsync();

        var session = await StartAsync("writing");
        var item = session.GetProperty("items").EnumerateArray().Single();

        Assert.Equal("WRITING_TASK", item.GetProperty("type").GetString());
        // Never an unrelated topic (§41–43).
        Assert.Contains("research", item.GetProperty("prompt").GetString()!);
    }

    // ── What follows the app language, and what does not (ADR-035) ──────────

    [SkippableTheory]
    [InlineData("ar", "ar")]
    [InlineData("en", "en")]
    // A full browser-style header: the first tag is the learner's choice.
    [InlineData("ar-YE,ar;q=0.9,en;q=0.8", "ar")]
    // Nothing sent, and a language nobody here speaks, both mean the default.
    [InlineData(null, "ar")]
    [InlineData("fr", "ar")]
    public async Task Feedback_is_written_in_the_language_the_app_is_read_in(
        string? header, string expected)
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToWritingAsync();

        var session = await StartAsync("writing");
        var sessionId = session.GetProperty("id").GetGuid();
        var item = session.GetProperty("items").EnumerateArray().Single();

        Client.DefaultRequestHeaders.Remove("Accept-Language");
        if (header is not null)
            Client.DefaultRequestHeaders.Add("Accept-Language", header);

        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/writing",
            new
            {
                itemId = item.GetProperty("id").GetGuid(),
                answer = "I did research about sleep yesterday.",
            });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The feedback is the app talking to the learner, so it is written in
        // the language they read the app in. The sentence they wrote and the
        // word they used are untouched.
        Assert.Contains($"[{expected}]", body.GetProperty("feedback").GetString()!);
    }

    [SkippableFact]
    public async Task A_fixed_instruction_travels_as_a_key_and_content_does_not()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        // Reading first: its questions are written for one passage, so they are
        // content and carry no key — translating them would change what they
        // measure.
        var reading = await StartAsync("reading");
        var questions = reading.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("type").GetString() == "COMPREHENSION")
            .ToList();

        Assert.NotEmpty(questions);
        Assert.All(questions, q =>
            Assert.Null(q.GetProperty("promptKey").GetString()));

        await AdvanceToWritingAsync();
        var writing = await StartAsync("writing");
        var task = writing.GetProperty("items").EnumerateArray().Single();

        // The instruction is fixed, so it travels as a key the client can say
        // in the learner's own language — and the English text stays too, for a
        // client that does not know the key.
        Assert.Equal("WRITE_A_SENTENCE", task.GetProperty("promptKey").GetString());
        Assert.Contains("research", task.GetProperty("prompt").GetString()!);
    }

    [SkippableFact]
    public async Task A_grammar_slip_does_not_fail_correct_usage()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToWritingAsync();

        var session = await StartAsync("writing");
        var sessionId = session.GetProperty("id").GetGuid();
        var item = session.GetProperty("items").EnumerateArray().Single();

        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/writing",
            new
            {
                itemId = item.GetProperty("id").GetGuid(),
                answer = "I did research about sleep yesterday.",
            });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The AI reports the slip; the domain decides it does not matter (§32).
        Assert.Equal("missing article", body.GetProperty("grammarNote").GetString());
        Assert.True(body.GetProperty("passed").GetBoolean());
    }

    [SkippableFact]
    public async Task A_sentence_without_the_word_does_not_pass()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToWritingAsync();

        var session = await StartAsync("writing");
        var sessionId = session.GetProperty("id").GetGuid();
        var item = session.GetProperty("items").EnumerateArray().Single();

        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/writing",
            new
            {
                itemId = item.GetProperty("id").GetGuid(),
                answer = "I went to the shop yesterday morning.",
            });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("passed").GetBoolean());
        Assert.True(body.GetProperty("requeued").GetBoolean());
    }

    // ── Spelling ─────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Spelling_gives_a_clue_and_letters_but_no_level()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpellingAsync();

        var session = await StartAsync("spelling");
        var item = session.GetProperty("items").EnumerateArray().Single();

        Assert.Equal("SPELLING_TASK", item.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("clue").GetString()));
        Assert.NotEmpty(item.GetProperty("hints").EnumerateArray());
        Assert.Equal("LETTER_TILES", item.GetProperty("inputMode").GetString());

        // More tiles than the word needs (Part 2 §36–§37). A pool holding
        // exactly the right letters can be finished by using them all up,
        // which is an anagram with the answer built in rather than spelling.
        var letters = item.GetProperty("letters").EnumerateArray()
            .Select(l => l.GetString()!)
            .ToList();
        Assert.True(letters.Count > "research".Length,
            $"expected decoy tiles, got {letters.Count}");

        // Every letter of the word is still there, counted properly — two
        // "r"s in "research" need two "r" tiles.
        foreach (var group in "research".GroupBy(c => c))
        {
            Assert.True(
                letters.Count(l => l == group.Key.ToString()) >= group.Count(),
                $"not enough '{group.Key}' tiles");
        }

        // Spelling carries no CEFR level of its own (ADR-008).
        await using var context = db.CreateContext();
        var level = await context.SkillLevels
            .FirstAsync(l => l.Skill == SkillType.Spelling);
        Assert.Null(level.UserSelectedLevel);
        Assert.Null(level.SystemAssessedLevel);
    }

    [SkippableFact]
    public async Task Naming_a_word_is_not_using_it()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpeakingAsync();

        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        // Reported from the device: the learner asked the tutor to move the
        // conversation somewhere the word would fit — and naming it in that
        // request counted as using it, so the tutor never asked for it again
        // and the word failed the session it was never given a chance in.
        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/speaking/turn",
            new { transcript = "Can you change the topic so I can use research in a sentence?" });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var used = body.GetProperty("wordsUsed").EnumerateArray()
            .Select(w => w.GetString()).ToList();
        var remaining = body.GetProperty("remaining").EnumerateArray()
            .Select(w => w.GetString()).ToList();

        Assert.DoesNotContain("research", used);
        Assert.Contains("research", remaining);
    }

    [SkippableFact]
    public async Task A_word_the_learner_actually_uses_is_counted_once()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpeakingAsync();

        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/speaking/turn",
            new { transcript = "I did some research about sleep last week." });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains("research", body.GetProperty("wordsUsed").EnumerateArray()
            .Select(w => w.GetString()));

        // And it stays counted across turns — the record is kept, not
        // recomputed from a transcript search each time (ADR-040).
        var second = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/speaking/turn",
            new { transcript = "It was interesting." });

        second.EnsureSuccessStatusCode();
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains("research", secondBody.GetProperty("wordsUsed").EnumerateArray()
            .Select(w => w.GetString()));
    }

    [SkippableFact]
    public async Task A_word_the_session_saw_used_is_not_failed_as_never_used()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var wordId = await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpeakingAsync();

        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        // The learner uses it, and the turn records that with the sentence in
        // front of it.
        var turn = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/speaking/turn",
            new { transcript = "I did some research about how people sleep at night." });
        turn.EnsureSuccessStatusCode();

        var turnBody = await turn.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("research", turnBody.GetProperty("wordsUsed").EnumerateArray()
            .Select(w => w.GetString()));

        // The end-of-conversation judge then says it was never used at all —
        // a disagreement about a fact, not about quality.
        Ai.SpeakingVerdict = new SpeakingWordObservation(
            Word: "research",
            Used: false,
            MeaningCorrect: false,
            Understandable: false,
            GrammarAcceptable: true,
            MajorGrammarProblem: false,
            Evidence: string.Empty,
            Feedback: "stub");

        var complete = await Client.PostAsync(
            $"/api/sessions/{sessionId}/complete", null);
        complete.EnsureSuccessStatusCode();

        await using var context = db.CreateContext();
        var state = await context.Set<WordSkillState>()
            .FirstAsync(x => x.WordId == wordId && x.Skill == SkillType.Speaking);

        // The evidence wins: the learner said it, in a sentence of their own.
        Assert.Equal(SkillStatus.Passed, state.Status);
    }

    [SkippableFact]
    public async Task A_conversation_ends_on_a_goodbye_not_on_a_question()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpeakingAsync();

        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        var turn = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/speaking/turn",
            new { transcript = "I did some research about how people sleep." });

        turn.EnsureSuccessStatusCode();
        var body = await turn.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("isFinal").GetBoolean());
        Assert.Empty(body.GetProperty("remaining").EnumerateArray());

        // The reply is written before the turn is judged, so without asking
        // again the learner's last line was a question about the word they had
        // just finished — "try to use the word research" one line after they
        // used it (ADR-042). The last thing asked for is a turn with nothing
        // left to practise, which is the closing.
        Assert.Empty(Ai.LastRemainingWords);
    }

    [SkippableFact]
    public async Task A_session_whose_word_has_moved_on_can_still_be_closed()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();
        var wordId = await AddWordAsync("research", "بحث علمي");

        var session = await StartAsync("reading");
        var sessionId = session.GetProperty("id").GetGuid();

        // The word moves on while this session is open — the same word
        // finished elsewhere, or a schedule brought forward (ADR-037).
        await using (var context = db.CreateContext())
        {
            await context.Database.ExecuteSqlAsync(
                $"""UPDATE words SET "CurrentSkill" = 'Writing' WHERE "Id" = {wordId}""");
        }

        var complete = await Client.PostAsync(
            $"/api/sessions/{sessionId}/complete", null);

        // The domain refuses to apply a Reading result to a word now at
        // Writing, and should. What must not happen is a 500: the session is
        // then resumed on every visit and can never be finished, which is a
        // learner permanently stuck on a loading screen (ADR-043).
        complete.EnsureSuccessStatusCode();

        var body = await complete.Content.ReadFromJsonAsync<JsonElement>();
        var outcome = body.GetProperty("words").EnumerateArray().Single();

        Assert.True(outcome.GetProperty("superseded").GetBoolean());

        // And the word itself is untouched by a session that no longer owns it.
        await using (var context = db.CreateContext())
        {
            var word = await context.Words.FirstAsync(w => w.Id == wordId);
            Assert.Equal(SkillType.Writing, word.CurrentSkill);
        }
    }

    // ── What counts as saying the word (ADR-049) ────────────────────────────

    [SkippableTheory]
    // The plural of a plain noun is the same word: "How many books do you
    // read?" answered with "books" is the learner using `book`, and asking
    // again for it is the tutor not listening.
    [InlineData("I read three books every week.", true)]
    [InlineData("I read one book every week.", true)]
    [InlineData("My book is on the table.", true)]
    // Another word that merely starts the same is not the word.
    [InlineData("I am booking a flight tomorrow.", false)]
    [InlineData("The bookshop near my house is small.", false)]
    [InlineData("I like reading in the evening.", false)]
    public async Task A_regular_plural_counts_and_a_different_word_does_not(
        string said, bool shouldCount)
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("book", "كتاب");
        await AdvanceToSpeakingAsync();

        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/speaking/turn",
            new { transcript = said });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var used = body.GetProperty("wordsUsed").EnumerateArray()
            .Select(w => w.GetString()).ToList();

        Assert.Equal(shouldCount, used.Contains("book"));
    }

    [SkippableFact]
    public async Task An_irregular_plural_is_a_different_word_and_does_not_count()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // `mice` is its own entry with its own pipeline (ADR-045), so a learner
        // practising `mouse` has not practised it by saying `mice`.
        await AddWordWithPluralAsync("mouse", "فأر", "mice");
        await AdvanceToSpeakingAsync();

        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/speaking/turn",
            new { transcript = "There are two mice in the kitchen." });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.DoesNotContain("mouse", body.GetProperty("wordsUsed").EnumerateArray()
            .Select(w => w.GetString()));
    }

    [SkippableTheory]
    // A verb is not pluralised, and another tense is another vocabulary item.
    [InlineData("Yesterday I researched the topic for hours.", false)]
    [InlineData("I am researching the topic today.", false)]
    [InlineData("I did some research about sleep.", true)]
    public async Task Another_form_of_a_verb_is_another_word(
        string said, bool shouldCount)
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpeakingAsync();

        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/speaking/turn",
            new { transcript = said });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(shouldCount, body.GetProperty("wordsUsed").EnumerateArray()
            .Select(w => w.GetString()).Contains("research"));
    }

    [SkippableFact]
    public async Task Two_senses_of_one_spelling_are_one_thing_to_say()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // A learner may hold the same spelling twice — `book` the object and
        // `book` the verb are separate senses and separate pipelines. A
        // transcript cannot tell them apart, so the turn must treat the
        // spelling as one thing to say rather than look each word up by name.
        await AddWordAsync("book", "كتاب");
        await AddWordAsync("book", "يحجز");
        await AdvanceToSpeakingAsync();

        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/speaking/turn",
            new { transcript = "I read three books every week." });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Said once, reported once — not once per sense.
        var used = body.GetProperty("wordsUsed").EnumerateArray()
            .Select(w => w.GetString()).ToList();
        Assert.Equal(["book"], used);
    }

    /// <summary>Adds a noun the lexicon also carries an irregular plural for.</summary>
    private async Task<Guid> AddWordWithPluralAsync(
        string text, string meaning, string plural)
    {
        var senseId = $"pl-{Guid.NewGuid():N}"[..24];

        await using (var context = db.CreateContext())
        {
            context.LexiconEntries.Add(LexiconEntry.Create(
                senseId, text, text, "n", $"a {text}", meaning,
                CefrLevel.A1, 1, "en=wordos-test;ar=wordos-test",
                DateTimeOffset.UtcNow));

            // The plural entry is what marks the plural as irregular: a regular
            // one is never given a row of its own (ADR-045).
            context.LexiconEntries.Add(LexiconEntry.Create(
                $"{senseId}#pl", plural, text, "n",
                $"plural of \"{text}\" — a {text}", $"{meaning} (الجمع)",
                CefrLevel.A1, 2, "en=wordos-test;ar=wordos-test;form=pl",
                DateTimeOffset.UtcNow));

            await context.SaveChangesAsync();
        }

        var response = await Client.PostAsJsonAsync("/api/words", new { senseId });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    // ── One conversation at a time (ADR-048) ────────────────────────────────

    [SkippableFact]
    public async Task Adding_words_mid_conversation_does_not_start_a_second_one()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpeakingAsync();

        var started = await StartAsync("speaking");
        var sessionId = started.GetProperty("id").GetGuid();
        var words = started.GetProperty("targetWords").GetArrayLength();

        // The learner leaves mid-conversation and adds vocabulary — the most
        // ordinary thing in the world, and the moment a second session would
        // appear and split them in two.
        await AddWordAsync("allocate", "يخصص");

        var resumed = await StartAsync("speaking");

        Assert.Equal(sessionId, resumed.GetProperty("id").GetGuid());
        Assert.Equal(words, resumed.GetProperty("targetWords").GetArrayLength());

        // And the conversation they were having is still there.
        Assert.NotEmpty(resumed.GetProperty("conversation")
            .GetProperty("turns").EnumerateArray());
    }

    [SkippableFact]
    public async Task A_conversation_only_ever_asks_for_its_own_words()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpeakingAsync();

        var started = await StartAsync("speaking");
        var sessionId = started.GetProperty("id").GetGuid();
        var announced = started.GetProperty("targetWords").EnumerateArray()
            .Select(w => w.GetProperty("text").GetString()).ToList();

        // Words that arrive at Speaking while the conversation is open — added
        // now, or finished elsewhere and still parked here. Reading the queue
        // instead of the session's own record turned a two-word session into a
        // twelve-word interrogation, and the learner saw a screen promising two
        // words and a tutor asking for a dozen (ADR-039).
        await AddWordAsync("allocate", "يخصص");
        await AdvanceToSpeakingAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/speaking/turn",
            new { transcript = "I am fine, thank you for asking." });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var remaining = body.GetProperty("remaining").EnumerateArray()
            .Select(w => w.GetString()).ToList();

        Assert.Equal(announced, remaining);
        Assert.DoesNotContain("allocate", remaining);
    }

    [SkippableFact]
    public async Task Reaching_for_a_word_and_missing_the_form_is_told_which_form()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // Stands for `went`, the past tense of `go` — an entry of its own
        // (ADR-045). A learner who says "I go there every year" has not used
        // it, but they are one step away, and "try to use went" does not say
        // which step. The verb is invented so the fixture never seeds real
        // English into the dictionary every other test searches.
        await AddInflectedWordAsync("vurned", "ذهب", lemma: "vurn", suffix: "pst",
            siblings: ["vurn", "vurning"]);
        await AdvanceToSpeakingAsync();

        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/speaking/turn",
            new { transcript = "I vurn to my village every year with my family." });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Not used — another form of a verb is another word (ADR-049).
        Assert.Empty(body.GetProperty("wordsUsed").EnumerateArray());

        // But the tutor was told exactly what to say back.
        var reminder = Assert.Single(Ai.LastFormReminders);
        Assert.Equal("vurned", reminder.Word);
        Assert.Equal("past tense", reminder.Form);
        Assert.Equal("vurn", reminder.Said);

        // And it knows what shape the word is, so the question can invite it.
        Assert.Contains(Ai.LastRemainingShapes,
            w => w.Text == "vurned" && w.Form == "past tense");
    }

    [SkippableFact]
    public async Task Saying_the_word_itself_is_never_reported_as_a_near_miss()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddInflectedWordAsync("vurned", "ذهب", lemma: "vurn", suffix: "pst",
            siblings: ["vurn", "vurning"]);
        await AdvanceToSpeakingAsync();

        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        // A turn containing both the word and another of its forms. They used
        // it, so there is nothing to repair.
        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/speaking/turn",
            new { transcript = "I vurned there last year and I vurn every summer." });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains("vurned", body.GetProperty("wordsUsed").EnumerateArray()
            .Select(w => w.GetString()));
        Assert.Empty(Ai.LastFormReminders);
    }

    /// <summary>Adds an inflected entry the lexicon knows the family of.</summary>
    private async Task<Guid> AddInflectedWordAsync(
        string text, string meaning, string lemma, string suffix,
        string[] siblings)
    {
        var stem = $"{lemma}-{Guid.NewGuid():N}"[..20];
        var senseId = $"{stem}#{suffix}";

        await using (var context = db.CreateContext())
        {
            context.LexiconEntries.Add(LexiconEntry.Create(
                senseId, text, lemma, "v", $"the {suffix} of {lemma}", meaning,
                CefrLevel.A1, 1, "en=wordos-test;ar=wordos-test;form=" + suffix,
                DateTimeOffset.UtcNow));

            // The rest of the family, which is how a near miss is recognised.
            foreach (var sibling in siblings)
            {
                context.LexiconEntries.Add(LexiconEntry.Create(
                    $"{stem}-{sibling}", sibling, lemma, "v",
                    $"a form of {lemma}", meaning,
                    CefrLevel.A1, 1, "en=wordos-test;ar=wordos-test",
                    DateTimeOffset.UtcNow));
            }

            await context.SaveChangesAsync();
        }

        var response = await Client.PostAsJsonAsync("/api/words", new { senseId });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    [SkippableFact]
    public async Task A_finished_conversation_gives_way_to_the_next_one()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpeakingAsync();

        var first = await StartAsync("speaking");
        var firstId = first.GetProperty("id").GetGuid();

        await Client.PostAsJsonAsync(
            $"/api/sessions/{firstId}/speaking/turn",
            new { transcript = "I did some research about sleep last night." });

        var completed = await Client.PostAsync(
            $"/api/sessions/{firstId}/complete", null);
        completed.EnsureSuccessStatusCode();

        // A second word arrives at Speaking afterwards.
        await AddWordAsync("allocate", "يخصص");
        await AdvanceToSpeakingAsync();

        var second = await StartAsync("speaking");

        // A finished session is finished: the next one is its own conversation,
        // which is what makes a second day of practice possible at all.
        Assert.NotEqual(firstId, second.GetProperty("id").GetGuid());
        Assert.NotEmpty(second.GetProperty("targetWords").EnumerateArray());
    }

    [SkippableFact]
    public async Task A_conversation_ends_with_coaching_on_every_word()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpeakingAsync();

        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/speaking/turn",
            new { transcript = "I did some research about how people sleep." });

        var complete = await Client.PostAsync(
            $"/api/sessions/{sessionId}/complete", null);
        complete.EnsureSuccessStatusCode();

        var body = await complete.Content.ReadFromJsonAsync<JsonElement>();
        var word = body.GetProperty("words").EnumerateArray().Single();

        // The evaluation always had this; it was computed for the verdict and
        // then thrown away. A learner told only that a word failed has learned
        // that they failed and nothing else (ADR-048).
        Assert.False(string.IsNullOrWhiteSpace(
            word.GetProperty("feedback").GetString()));
    }

    // ── A session remembers what it is for (ADR-039) ────────────────────────

    [SkippableFact]
    public async Task A_conversation_still_has_its_words_when_the_learner_returns()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpeakingAsync();

        var started = await StartAsync("speaking");
        var words = started.GetProperty("targetWords").EnumerateArray()
            .Select(w => w.GetProperty("text").GetString())
            .ToList();

        Assert.NotEmpty(words);
        Assert.NotEmpty(started.GetProperty("warmup").EnumerateArray());

        // The learner leaves and comes back — the hub says words are due, and
        // this is the request the app makes when they tap the skill again.
        var resumed = await StartAsync("speaking");

        Assert.Equal(started.GetProperty("id").GetGuid(),
            resumed.GetProperty("id").GetGuid());

        // A conversation has no items to recover its words from, so they used
        // to vanish here: five words due on the hub, and nothing to talk about
        // on the screen.
        Assert.Equal(words, resumed.GetProperty("targetWords").EnumerateArray()
            .Select(w => w.GetProperty("text").GetString())
            .ToList());
        Assert.NotEmpty(resumed.GetProperty("warmup").EnumerateArray());
    }

    [SkippableFact]
    public async Task A_resumed_session_keeps_its_words_even_as_others_move_on()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpeakingAsync();

        var started = await StartAsync("speaking");
        var sessionId = started.GetProperty("id").GetGuid();

        // Something else changes the pipeline underneath the open session —
        // another word arriving at this skill, or a schedule moving.
        await AddWordAsync("allocate", "يخصص");

        var resumed = await StartAsync("speaking");

        Assert.Equal(sessionId, resumed.GetProperty("id").GetGuid());

        // The session is about the words it started with. Reading "whatever is
        // at this skill now" would quietly change what the learner is being
        // asked about, mid-session.
        Assert.Equal(
            started.GetProperty("targetWords").GetArrayLength(),
            resumed.GetProperty("targetWords").GetArrayLength());
    }

    [SkippableFact]
    public async Task An_empty_session_left_by_an_older_build_is_replaced()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpeakingAsync();

        var started = await StartAsync("speaking");
        var sessionId = started.GetProperty("id").GetGuid();

        // Exactly what a session saved before ADR-039 looks like: a
        // conversation, so no items, and no record of what it was about.
        await using (var context = db.CreateContext())
        {
            await context.Database.ExecuteSqlAsync(
                $"""UPDATE skill_sessions SET "WordIdsJson" = NULL WHERE "Id" = {sessionId}""");
        }

        var resumed = await StartAsync("speaking");

        // Not resumed into an empty screen the learner can never leave: the
        // words never left the queue, so a fresh session is the right answer.
        Assert.NotEqual(sessionId, resumed.GetProperty("id").GetGuid());
        Assert.NotEmpty(resumed.GetProperty("targetWords").EnumerateArray());
    }

    // ── Changing the level of a writing task (ADR-038) ──────────────────────

    [SkippableFact]
    public async Task Writing_can_be_relevelled_and_the_rewrite_follows_it()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToWritingAsync();

        var session = await StartAsync("writing");
        var sessionId = session.GetProperty("id").GetGuid();

        var relevelled = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/level", new { level = "C1" });

        relevelled.EnsureSuccessStatusCode();
        var body = await relevelled.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("C1", body.GetProperty("levelUsed").GetString());

        // Nothing was regenerated: a writing task has no passage to re-tell, so
        // the learner keeps the task they were part-way through.
        Assert.Equal(
            session.GetProperty("items").GetArrayLength(),
            body.GetProperty("items").GetArrayLength());

        // And the level is what the rewrite is written at — the reason the
        // control exists on this screen at all.
        var item = body.GetProperty("items").EnumerateArray().First();
        var answer = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/writing",
            new
            {
                itemId = item.GetProperty("id").GetGuid(),
                answer = "I did research about sleep yesterday.",
            });

        answer.EnsureSuccessStatusCode();
        var evaluation = await answer.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("C1", evaluation.GetProperty("suggestion").GetString()!);
    }

    [SkippableFact]
    public async Task The_level_a_learner_picks_in_a_session_becomes_their_level()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        var session = await StartAsync("reading");
        var sessionId = session.GetProperty("id").GetGuid();

        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/level", new { level = "B2" });
        response.EnsureSuccessStatusCode();

        // Settings renders the profile, so this is what the learner sees there.
        var me = await Client.GetFromJsonAsync<JsonElement>("/api/me");
        var reading = me.GetProperty("skillLevels").EnumerateArray()
            .First(l => l.GetProperty("skill").GetString() == "READING");

        Assert.Equal("B2", reading.GetProperty("userSelectedLevel").GetString());

        // And only the user-selected level: performance decides the validated
        // one, and a tap is not performance (rule R6).
        Assert.NotEqual("B2",
            reading.GetProperty("systemAssessedLevel").GetString() ?? "none");
    }

    // ── The Owner's time skip (ADR-037) ─────────────────────────────────────

    [SkippableFact]
    public async Task Skipping_two_days_opens_the_next_skill_without_waiting()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();
        var wordId = await AddWordAsync("research", "بحث علمي");

        // Pass Reading, which schedules Listening two days out. Without the
        // skip, testing the rest of the pipeline means waiting eight days.
        await FinishAsync(await StartAsync("reading"));

        await using (var context = db.CreateContext())
        {
            var listening = await context.Set<WordSkillState>()
                .FirstAsync(x => x.WordId == wordId && x.Skill == SkillType.Listening);

            Assert.Equal(SkillStatus.Pending,
                listening.EffectiveStatus(Clock.GetUtcNow()));
        }

        var owner = await SignInAsOwnerAsync();
        var response = await owner.PostAsJsonAsync(
            $"/api/admin/users/{userId}/advance-schedule", new { days = 2 });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("days").GetInt32());

        await using (var context = db.CreateContext())
        {
            var listening = await context.Set<WordSkillState>()
                .FirstAsync(x => x.WordId == wordId && x.Skill == SkillType.Listening);

            Assert.Equal(SkillStatus.Available,
                listening.EffectiveStatus(Clock.GetUtcNow()));
        }

        // And the session it unlocked actually starts.
        var session = await StartAsync("listening");
        Assert.NotEmpty(session.GetProperty("items").EnumerateArray());
    }

    [SkippableFact]
    public async Task The_skip_moves_the_waiting_and_nothing_that_already_happened()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();
        var wordId = await AddWordAsync("research", "بحث علمي");
        await FinishAsync(await StartAsync("reading"));

        DateTimeOffset? passedAt;
        int attempts;
        await using (var context = db.CreateContext())
        {
            var reading = await context.Set<WordSkillState>()
                .FirstAsync(x => x.WordId == wordId && x.Skill == SkillType.Reading);
            passedAt = reading.PassedAt;
            attempts = reading.Attempts;
        }

        var owner = await SignInAsOwnerAsync();
        await owner.PostAsJsonAsync(
            $"/api/admin/users/{userId}/advance-schedule", new { days = 2 });

        await using (var context = db.CreateContext())
        {
            var reading = await context.Set<WordSkillState>()
                .FirstAsync(x => x.WordId == wordId && x.Skill == SkillType.Reading);

            // History is the evidence the experiment runs on. A tool that
            // rewrote it would make every figure on the dashboard a guess.
            Assert.Equal(passedAt, reading.PassedAt);
            Assert.Equal(attempts, reading.Attempts);
            Assert.Equal(SkillStatus.Passed, reading.Status);

            // And the skip itself is recorded, so a pipeline finished in an
            // afternoon does not read as an extraordinary learner.
            Assert.True(await context.ActivityEvents.AnyAsync(
                e => e.UserId == userId && e.Type == ActivityType.ScheduleAdvanced));
        }
    }

    [SkippableFact]
    public async Task Only_the_owner_may_move_a_schedule()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        // The caller here is an ordinary learner — their own account, even.
        // Hiding the button is not access control.
        var response = await Client.PostAsJsonAsync(
            $"/api/admin/users/{userId}/advance-schedule", new { days = 2 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(999999999)]
    public async Task A_nonsense_number_of_days_is_clamped_not_obeyed(int days)
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        var owner = await SignInAsOwnerAsync();
        var response = await owner.PostAsJsonAsync(
            $"/api/admin/users/{userId}/advance-schedule", new { days });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var applied = body.GetProperty("days").GetInt32();
        Assert.InRange(applied, 1, 30);
    }

    /// <summary>A second client, signed in as the Owner.</summary>
    private async Task<HttpClient> SignInAsOwnerAsync()
    {
        var email = $"owner-{Guid.NewGuid():N}@test.dev";

        var client = _factory!.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = "correct-horse-battery",
            displayName = "Owner",
            phoneCountryCode = "967",
            phoneNumber = "770000008",
        });
        response.EnsureSuccessStatusCode();

        // Directly in the database, because the API offers no way to grant the
        // role — which is itself a property worth keeping.
        await using (var context = db.CreateContext())
        {
            await context.Database.ExecuteSqlAsync(
                $"""UPDATE users SET "Role" = 'Owner' WHERE "Email" = {email}""");
        }

        // Re-issued after the change, so the token carries the new role.
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email, password = "correct-horse-battery" });
        login.EnsureSuccessStatusCode();
        var body = await login.Content.ReadFromJsonAsync<JsonElement>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", body.GetProperty("token").GetString());

        return client;
    }

    /// <summary>
    /// Adds a word that has a synonym in the lexicon.
    /// </summary>
    /// <remarks>
    /// Two lemmas of one synset, which in this data means two rows carrying the
    /// same gloss — that shared definition is what identifies them as synonyms.
    /// </remarks>
    private async Task<Guid> AddWordWithSynonymAsync(
        string text, string meaning, string synonym)
    {
        var senseId = $"syn-{Guid.NewGuid():N}";
        const string gloss = "systematic investigation; a careful, detailed study";
        await using (var context = db.CreateContext())
        {
            context.LexiconEntries.Add(LexiconEntry.Create(
                senseId, text, text, "n", gloss,
                meaning, CefrLevel.B1, 1, "en=oewn;ar=awn", DateTimeOffset.UtcNow));
            context.LexiconEntries.Add(LexiconEntry.Create(
                $"{senseId}-2", synonym, synonym, "n", gloss,
                meaning, CefrLevel.B1, 2, "en=oewn;ar=awn", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        var response = await Client.PostAsJsonAsync("/api/words", new { senseId });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("id").GetGuid();
    }

    [SkippableFact]
    public async Task Passing_spelling_matures_the_word_into_active_vocabulary()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var wordId = await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpellingAsync();

        var session = await StartAsync("spelling");
        var sessionId = session.GetProperty("id").GetGuid();
        var item = session.GetProperty("items").EnumerateArray().Single();

        var answer = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/answer",
            new { itemId = item.GetProperty("id").GetGuid(), answer = "research" });
        answer.EnsureSuccessStatusCode();

        var complete = await Client.PostAsync(
            $"/api/sessions/{sessionId}/complete", null);
        complete.EnsureSuccessStatusCode();
        var result = await complete.Content.ReadFromJsonAsync<JsonElement>();

        var outcome = result.GetProperty("words").EnumerateArray().Single();
        Assert.True(outcome.GetProperty("becameActive").GetBoolean());

        await using var context = db.CreateContext();
        var word = await context.Words.Include(w => w.Skills)
            .FirstAsync(w => w.Id == wordId);

        Assert.Equal(WordState.Active, word.State);
        Assert.Null(word.CurrentSkill);
        Assert.All(word.Skills, s => Assert.Equal(SkillStatus.Passed, s.Status));
    }

    // ── Rules that hold across skills ────────────────────────────────────────

    [SkippableFact]
    public async Task Failing_a_later_skill_never_resets_the_earlier_ones()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var wordId = await AddWordAsync("research", "بحث علمي");
        await AdvanceToWritingAsync();

        var session = await StartAsync("writing");
        var sessionId = session.GetProperty("id").GetGuid();
        var item = session.GetProperty("items").EnumerateArray().Single();
        var itemId = item.GetProperty("id").GetGuid();

        // Fail it repeatedly until the retry budget is spent.
        for (var i = 0; i < 3; i++)
        {
            var attempt = await Client.PostAsJsonAsync(
                $"/api/sessions/{sessionId}/writing",
                new { itemId, answer = "Something else entirely happened today." });
            if (!attempt.IsSuccessStatusCode) break;
        }

        var complete = await Client.PostAsync(
            $"/api/sessions/{sessionId}/complete", null);
        complete.EnsureSuccessStatusCode();

        await using var context = db.CreateContext();
        var word = await context.Words.Include(w => w.Skills)
            .FirstAsync(w => w.Id == wordId);

        // Rule R5 — the single most important property of the pipeline.
        Assert.Equal(SkillType.Writing, word.CurrentSkill);
        Assert.Equal(SkillStatus.Passed,
            word.Skills.First(s => s.Skill == SkillType.Reading).Status);
        Assert.Equal(SkillStatus.Passed,
            word.Skills.First(s => s.Skill == SkillType.Listening).Status);
        Assert.Equal(SkillStatus.Passed,
            word.Skills.First(s => s.Skill == SkillType.Speaking).Status);
        Assert.NotEqual(SkillStatus.Passed,
            word.Skills.First(s => s.Skill == SkillType.Writing).Status);
    }

    [SkippableFact]
    public async Task A_session_belonging_to_another_learner_is_not_reachable()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        var session = await StartAsync("reading");
        var sessionId = session.GetProperty("id").GetGuid();
        var itemId = CurrentItemId(session);

        // A second learner, with a valid token of their own.
        await SignInAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/answer",
            new { itemId, answer = "anything" });

        // Not 403: knowing the id must not even confirm the session exists.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task An_ai_outage_degrades_the_session_instead_of_ending_it()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        Ai.Fail = true;
        try
        {
            var session = await StartAsync("reading");

            // Weaker content, but a complete session — and it says so, which is
            // what stops the dip being read as learners getting worse (§62).
            Assert.True(session.GetProperty("usedAiFallback").GetBoolean());
            Assert.Equal(5, session.GetProperty("items").EnumerateArray()
                .Count(i => i.GetProperty("type").GetString() == "COMPREHENSION"));

            var result = await FinishAsync(session);
            Assert.True(result.GetProperty("usedAiFallback").GetBoolean());
            Assert.True(result.GetProperty("words").EnumerateArray()
                .Single().GetProperty("passed").GetBoolean());
        }
        finally
        {
            Ai.Fail = false;
        }
    }

    [SkippableFact]
    public async Task Starting_again_resumes_the_open_session_instead_of_replacing_it()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        var first = await StartAsync("reading");
        var sessionId = first.GetProperty("id").GetGuid();
        var passage = first.GetProperty("content").GetProperty("text").GetString();

        await AnswerAsync(
            sessionId, ItemById(first, CurrentItemId(first)), correct: true);

        // The learner killed the app and came back. Starting is the only thing
        // the hub can do, and it must not cost them the answer they gave.
        var again = await StartAsync("reading");

        Assert.Equal(sessionId, again.GetProperty("id").GetGuid());
        Assert.Equal(passage,
            again.GetProperty("content").GetProperty("text").GetString());
        Assert.Equal(1, again.GetProperty("progress")
            .GetProperty("answered").GetInt32());

        // No second Gemini call was spent on it.
        Assert.Equal(1, Ai.ContentCalls);
    }

    [SkippableFact]
    public async Task The_hub_reports_an_open_session_so_it_survives_a_restart()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        var session = await StartAsync("reading");
        var sessionId = session.GetProperty("id").GetGuid();

        // Nothing is stored on the device (rule R4) — the client asks the hub.
        var hub = await Client.GetFromJsonAsync<JsonElement>("/api/hub");
        var reading = hub.GetProperty("skills").EnumerateArray()
            .First(s => s.GetProperty("skill").GetString() == "READING");

        Assert.Equal(sessionId, reading.GetProperty("activeSessionId").GetGuid());

        // And it can be read back in full, content and all.
        var resumed = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/sessions/{sessionId}");
        Assert.Equal(sessionId, resumed.GetProperty("id").GetGuid());

        // Once abandoned, the hub stops offering it.
        (await Client.PostAsync($"/api/sessions/{sessionId}/abandon", null))
            .EnsureSuccessStatusCode();

        hub = await Client.GetFromJsonAsync<JsonElement>("/api/hub");
        reading = hub.GetProperty("skills").EnumerateArray()
            .First(s => s.GetProperty("skill").GetString() == "READING");
        Assert.Equal(JsonValueKind.Null,
            reading.GetProperty("activeSessionId").ValueKind);
    }

    [SkippableFact]
    public async Task Speaking_is_judged_on_the_whole_conversation_at_the_end()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var wordId = await AddWordAsync("research", "بحث علمي");

        await FinishAsync(await StartAsync("reading"));
        Clock.SkipDays(2);
        await FinishAsync(await StartAsync("listening"));
        Clock.SkipDays(2);

        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        // The learner uses the word with the wrong meaning. Turn by turn this
        // looks fine — the word is there — which is exactly why the judgement
        // happens once, over the whole conversation.
        Ai.SpeakingVerdict = new WordOs.Application.Abstractions
            .SpeakingWordObservation(
                Word: "research",
                Used: true,
                MeaningCorrect: false,
                Understandable: true,
                GrammarAcceptable: true,
                MajorGrammarProblem: false,
                Evidence: "My research is my telephone.",
                Feedback: "That is not what research means.");

        (await Client.PostAsJsonAsync($"/api/sessions/{sessionId}/speaking/turn",
            new { transcript = "My research is my telephone and I like it." }))
            .EnsureSuccessStatusCode();

        var complete = await Client.PostAsync(
            $"/api/sessions/{sessionId}/complete", null);
        complete.EnsureSuccessStatusCode();
        var result = await complete.Content.ReadFromJsonAsync<JsonElement>();

        var outcome = result.GetProperty("words").EnumerateArray().Single();
        Assert.False(outcome.GetProperty("passed").GetBoolean(),
            "the word was said but meant something else, so it must not pass");

        await using var context = db.CreateContext();
        var word = await context.Words.FirstAsync(w => w.Id == wordId);
        Assert.Equal(SkillType.Speaking, word.CurrentSkill);
    }

    [SkippableFact]
    public async Task A_grammar_slip_in_speech_does_not_fail_correct_use()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        await FinishAsync(await StartAsync("reading"));
        Clock.SkipDays(2);
        await FinishAsync(await StartAsync("listening"));
        Clock.SkipDays(2);

        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        // "I research about AI yesterday" — wrong tense, right meaning (§32).
        Ai.SpeakingVerdict = new WordOs.Application.Abstractions
            .SpeakingWordObservation(
                Word: "research",
                Used: true,
                MeaningCorrect: true,
                Understandable: true,
                GrammarAcceptable: false,
                MajorGrammarProblem: false,
                Evidence: "I research about AI yesterday.",
                Feedback: "Good use — try \"researched\" for the past.");

        (await Client.PostAsJsonAsync($"/api/sessions/{sessionId}/speaking/turn",
            new { transcript = "I research about AI yesterday for two hours." }))
            .EnsureSuccessStatusCode();

        var complete = await Client.PostAsync(
            $"/api/sessions/{sessionId}/complete", null);
        complete.EnsureSuccessStatusCode();
        var result = await complete.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(result.GetProperty("words").EnumerateArray()
            .Single().GetProperty("passed").GetBoolean());
    }

    [SkippableFact]
    public async Task Speaking_and_writing_sessions_record_which_prompt_judged_them()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        await FinishAsync(await StartAsync("reading"));
        Clock.SkipDays(2);
        await FinishAsync(await StartAsync("listening"));
        Clock.SkipDays(2);
        await FinishSpeakingAsync();
        Clock.SkipDays(2);

        var writing = await StartAsync("writing");
        var writingId = writing.GetProperty("id").GetGuid();
        var item = writing.GetProperty("items").EnumerateArray().Single();

        (await Client.PostAsJsonAsync($"/api/sessions/{writingId}/writing", new
        {
            itemId = item.GetProperty("id").GetGuid(),
            answer = "Yesterday I did research about sleep for my class.",
        })).EnsureSuccessStatusCode();

        (await Client.PostAsync($"/api/sessions/{writingId}/complete", null))
            .EnsureSuccessStatusCode();

        Assert.True(Ai.SpeakingEvaluations > 0,
            "the conversation should have been evaluated at the end");

        await using var context = db.CreateContext();
        // Scoped to this learner. The suite shares one database, and other
        // tests deliberately run sessions through the AI-outage fallback, which
        // records no attribution by design.
        var sessions = await context.SkillSessions
            .Where(s => s.UserId == userId
                        && (s.Skill == SkillType.Speaking
                            || s.Skill == SkillType.Writing))
            .ToListAsync();

        Assert.Equal(2, sessions.Count);

        // Without attribution a shift in Speaking or Writing pass rates cannot
        // be traced to a prompt change the way Reading's can (§62).
        Assert.All(sessions, s =>
        {
            Assert.True(s.PromptVersion.Length > 0 && s.AiModel.Length > 0,
                $"{s.Skill} recorded no attribution "
                + $"(promptVersion='{s.PromptVersion}', model='{s.AiModel}')");
        });
    }

    [SkippableFact]
    public async Task Abandoning_a_session_leaves_the_words_due()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        var session = await StartAsync("reading");
        var abandon = await Client.PostAsync(
            $"/api/sessions/{session.GetProperty("id").GetGuid()}/abandon", null);
        Assert.Equal(HttpStatusCode.NoContent, abandon.StatusCode);

        // Nothing was consumed: the same word starts a fresh session.
        var again = await StartAsync("reading");
        Assert.Single(again.GetProperty("targetWords").EnumerateArray());
    }

    // ── Weekly review ────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task The_weekly_review_measures_without_changing_anything()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var wordId = await AddWordAsync("research", "بحث علمي");
        await AddWordAsync("theory", "نظرية");

        // Put one word mid-pipeline so a state change would be visible.
        await FinishAsync(await StartAsync("reading"));

        WordSnapshot before;
        await using (var context = db.CreateContext())
        {
            var word = await context.Words.Include(w => w.Skills)
                .FirstAsync(w => w.Id == wordId);
            before = WordSnapshot.Of(word);
        }

        var start = await Client.PostAsync("/api/weekly-review/start", null);
        start.EnsureSuccessStatusCode();
        var review = await start.Content.ReadFromJsonAsync<JsonElement>();
        var reviewId = review.GetProperty("id").GetGuid();

        Assert.Equal(2, review.GetProperty("totalWords").GetInt32());

        // Answer one right and one wrong.
        var queue = review.GetProperty("queue").EnumerateArray().ToList();
        await AnswerReviewAsync(reviewId, queue[0], correct: true);
        var missed = await AnswerReviewAsync(reviewId, queue[1], correct: false);

        Assert.True(missed.GetProperty("requeued").GetBoolean());
        // The learner is shown the right meaning rather than left wrong.
        Assert.False(string.IsNullOrWhiteSpace(
            missed.GetProperty("correctAnswer").GetString()));

        // The retry gets it right, but the score already recorded the miss.
        await AnswerReviewAsync(reviewId, queue[1], correct: true);

        var complete = await Client.PostAsync(
            $"/api/weekly-review/{reviewId}/complete", null);
        complete.EnsureSuccessStatusCode();
        var result = await complete.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, result.GetProperty("totalWords").GetInt32());
        Assert.Equal(1, result.GetProperty("firstPassCorrect").GetInt32());
        Assert.Equal(0.5, result.GetProperty("weeklyScore").GetDouble(), 3);
        Assert.Equal(3, result.GetProperty("totalAttempts").GetInt32());

        // Rule R9: after all that, the pipeline is exactly where it was.
        await using (var context = db.CreateContext())
        {
            var word = await context.Words.Include(w => w.Skills)
                .FirstAsync(w => w.Id == wordId);
            Assert.Equal(before, WordSnapshot.Of(word));
        }
    }

    private async Task<JsonElement> AnswerReviewAsync(
        Guid reviewId, JsonElement item, bool correct)
    {
        var itemId = item.GetProperty("id").GetGuid();

        string answer;
        await using (var context = db.CreateContext())
        {
            var stored = await context.WeeklyReviewItems.FirstAsync(i => i.Id == itemId);
            answer = correct
                ? stored.CorrectAnswer
                : item.GetProperty("options").EnumerateArray()
                    .Select(o => o.GetString()!)
                    .First(o => o != stored.CorrectAnswer);
        }

        var response = await Client.PostAsJsonAsync(
            $"/api/weekly-review/{reviewId}/answer", new { itemId, answer });

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Everything a weekly review must not touch.</summary>
    private sealed record WordSnapshot(
        WordState State,
        SkillType? CurrentSkill,
        string Skills)
    {
        public static WordSnapshot Of(WordOs.Domain.Words.Word word) => new(
            word.State,
            word.CurrentSkill,
            string.Join(',', word.Skills
                .OrderBy(s => s.Skill)
                .Select(s => $"{s.Skill}:{s.Status}:{s.AvailableAt:O}")));
    }

    // ── Pipeline helpers ─────────────────────────────────────────────────────

    /// <summary>Walks a word to Speaking: Reading, then Listening, then due.</summary>
    private async Task AdvanceToSpeakingAsync()
    {
        await FinishAsync(await StartAsync("reading"));
        Clock.SkipDays(2);
        await FinishAsync(await StartAsync("listening"));
        Clock.SkipDays(2);
    }

    private async Task AdvanceToWritingAsync()
    {
        await FinishAsync(await StartAsync("reading"));
        Clock.SkipDays(2);
        await FinishAsync(await StartAsync("listening"));
        Clock.SkipDays(2);
        await FinishSpeakingAsync();
        Clock.SkipDays(2);
    }

    private async Task AdvanceToSpellingAsync()
    {
        await AdvanceToWritingAsync();

        var session = await StartAsync("writing");
        var sessionId = session.GetProperty("id").GetGuid();

        foreach (var item in session.GetProperty("items").EnumerateArray())
        {
            var word = session.GetProperty("targetWords").EnumerateArray()
                .First(w => w.GetProperty("wordId").GetGuid()
                            == item.GetProperty("wordId").GetGuid())
                .GetProperty("text").GetString();

            var response = await Client.PostAsJsonAsync(
                $"/api/sessions/{sessionId}/writing",
                new
                {
                    itemId = item.GetProperty("id").GetGuid(),
                    answer = $"Yesterday I used {word} in a real sentence.",
                });
            response.EnsureSuccessStatusCode();
        }

        var complete = await Client.PostAsync(
            $"/api/sessions/{sessionId}/complete", null);
        complete.EnsureSuccessStatusCode();
        Clock.SkipDays(2);
    }

    private async Task FinishSpeakingAsync()
    {
        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        foreach (var word in session.GetProperty("targetWords").EnumerateArray())
        {
            var turn = await Client.PostAsJsonAsync(
                $"/api/sessions/{sessionId}/speaking/turn",
                new
                {
                    transcript =
                        $"I think {word.GetProperty("text").GetString()} is "
                        + "interesting and useful to me.",
                });
            turn.EnsureSuccessStatusCode();
        }

        var complete = await Client.PostAsync(
            $"/api/sessions/{sessionId}/complete", null);
        complete.EnsureSuccessStatusCode();
    }

    // ── The passage glossary and its level (Reading) ─────────────────────────

    [SkippableFact]
    public async Task The_passage_carries_the_meaning_of_its_own_words()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        var session = await StartAsync("reading");
        var glossary = session.GetProperty("content").GetProperty("glossary");

        // Written while the passage was generated, when the model still knew
        // which sense it meant. A dictionary consulted at tap time can only
        // offer every sense a word has ever had.
        Assert.NotEqual(JsonValueKind.Null, glossary.ValueKind);
        Assert.NotEmpty(glossary.EnumerateArray());

        Assert.All(glossary.EnumerateArray(), entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(
                entry.GetProperty("word").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(
                entry.GetProperty("meaning").GetString()));
            // The part of speech is what tells a learner what they are adding:
            // "will" as an auxiliary is a different thing to learn than "will"
            // as a noun.
            Assert.False(string.IsNullOrWhiteSpace(
                entry.GetProperty("partOfSpeech").GetString()));
        });
    }

    [SkippableFact]
    public async Task A_passage_can_be_retold_at_another_level_before_the_questions()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        var session = await StartAsync("reading");
        var sessionId = session.GetProperty("id").GetGuid();
        var before = session.GetProperty("content").GetProperty("text").GetString();

        Assert.True(session.GetProperty("content")
            .GetProperty("canChangeLevel").GetBoolean());

        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/level", new { level = "A2" });
        response.EnsureSuccessStatusCode();

        var retold = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("A2", retold.GetProperty("levelUsed").GetString());
        Assert.NotEqual(before,
            retold.GetProperty("content").GetProperty("text").GetString());

        // The comprehension questions come back with it. Keeping the old ones
        // would leave the learner answering about sentences that no longer
        // exist. (The target-word question is built here rather than by the
        // model, so it keeps its own wording.)
        var comprehension = retold.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("type").GetString() == "COMPREHENSION")
            .ToList();

        Assert.NotEmpty(comprehension);
        Assert.All(comprehension, i => Assert.Contains("Re-told",
            i.GetProperty("prompt").GetString() ?? string.Empty));

        // Same session, same words — this is the same reading, re-told.
        Assert.Equal(sessionId, retold.GetProperty("id").GetGuid());
        Assert.Equal(1, retold.GetProperty("targetWords").GetArrayLength());
    }

    [SkippableFact]
    public async Task The_level_locks_once_the_learner_has_answered()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        var session = await StartAsync("reading");
        var sessionId = session.GetProperty("id").GetGuid();
        var first = session.GetProperty("items").EnumerateArray().First();

        await Client.PostAsJsonAsync($"/api/sessions/{sessionId}/answer", new
        {
            itemId = first.GetProperty("id").GetGuid(),
            answer = "anything at all",
        });

        // Re-telling now would throw away the answer they just gave, because
        // the item it belongs to would cease to exist.
        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/level", new { level = "A2" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var resumed = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/sessions/{sessionId}");
        Assert.False(resumed.GetProperty("content")
            .GetProperty("canChangeLevel").GetBoolean());
    }

    [SkippableFact]
    public async Task Re_telling_a_passage_records_the_learners_choice()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        var session = await StartAsync("reading");
        var sessionId = session.GetProperty("id").GetGuid();

        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/level", new { level = "A2" });
        response.EnsureSuccessStatusCode();

        // The same setting Settings shows. A preference that reverted the next
        // session would be worse than no control at all — a learner who has
        // said twice that the level is too hard should not have to say it a
        // third time.
        var me = await Client.GetFromJsonAsync<JsonElement>("/api/me");
        var reading = me.GetProperty("skillLevels").EnumerateArray()
            .First(l => l.GetProperty("skill").GetString() == "READING");

        Assert.Equal("A2", reading.GetProperty("userSelectedLevel").GetString());

        // And it is on the record as the learner's own change, not the
        // system's — the two are never conflated (rule R6, MVP Core §60).
        await using var context = db.CreateContext();
        var change = await context.LevelChanges
            .Where(c => c.UserId == userId && c.Skill == SkillType.Reading)
            .OrderByDescending(c => c.CreatedAt)
            .FirstAsync();

        Assert.Equal(LevelChangeType.UserManualChange, change.ChangeType);
        Assert.Equal(CefrLevel.A2, change.NewLevel);
    }

    [SkippableFact]
    public async Task Re_telling_a_passage_never_touches_the_validated_level()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        var session = await StartAsync("reading");
        var sessionId = session.GetProperty("id").GetGuid();

        Domain.Levels.SkillLevel before;
        await using (var context = db.CreateContext())
        {
            before = await context.SkillLevels.AsNoTracking()
                .FirstAsync(l => l.UserId == userId && l.Skill == SkillType.Reading);
        }

        await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/level", new { level = "C1" });

        await using var after = db.CreateContext();
        var level = await after.SkillLevels.AsNoTracking()
            .FirstAsync(l => l.UserId == userId && l.Skill == SkillType.Reading);

        // A level a learner sets by tapping is a preference, not evidence.
        // Only performance moves the *validated* level (rule R6) — otherwise
        // asking for an easier passage would quietly demote them, and asking
        // for a harder one would promote them for free.
        Assert.Equal(before.SystemAssessedLevel, level.SystemAssessedLevel);
        Assert.Equal(before.RollingAccuracy, level.RollingAccuracy);
        Assert.Equal(before.EvaluationSessions, level.EvaluationSessions);
    }

    // ── What comes back and what does not ────────────────────────────────────

    [SkippableFact]
    public async Task A_missed_comprehension_question_is_never_asked_again()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        var session = await StartAsync("reading");
        var sessionId = session.GetProperty("id").GetGuid();

        var first = session.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("type").GetString() == "COMPREHENSION");
        var missedId = first.GetProperty("id").GetGuid();

        var answer = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/answer",
            new { itemId = missedId, answer = "deliberately wrong" });
        answer.EnsureSuccessStatusCode();

        var result = await answer.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(result.GetProperty("isCorrect").GetBoolean());

        // It measured whether the passage was pitched right, and it has. Asking
        // it again teaches nothing — the learner has already been shown the
        // answer — and it strands them re-reading questions they finished with.
        Assert.False(result.GetProperty("requeued").GetBoolean());

        var seen = new List<Guid> { missedId };
        var next = result.GetProperty("progress").GetProperty("nextItemId");

        while (next.ValueKind != JsonValueKind.Null && seen.Count < 40)
        {
            var itemId = next.GetGuid();
            Assert.DoesNotContain(missedId, seen.Skip(1));
            seen.Add(itemId);

            var step = await Client.PostAsJsonAsync(
                $"/api/sessions/{sessionId}/answer",
                new { itemId, answer = AnswerFor(session, itemId) });
            step.EnsureSuccessStatusCode();
            next = (await step.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("progress").GetProperty("nextItemId");
        }

        Assert.DoesNotContain(missedId, seen.Skip(1));
    }

    [SkippableFact]
    public async Task A_missed_word_comes_back_but_still_does_not_pass()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var wordId = await AddWordAsync("research", "بحث علمي");

        var session = await StartAsync("reading");
        var sessionId = session.GetProperty("id").GetGuid();

        // Items are answered in the order the server issues them, so the walk
        // reaches the target word rather than jumping to it.
        var next = session.GetProperty("progress").GetProperty("nextItemId");
        var missedOnce = false;
        var requeued = false;
        var guard = 0;

        while (next.ValueKind != JsonValueKind.Null && guard++ < 40)
        {
            var itemId = next.GetGuid();
            var item = session.GetProperty("items").EnumerateArray()
                .First(i => i.GetProperty("id").GetGuid() == itemId);
            var isTarget = item.GetProperty("type").GetString() == "TARGET_WORD";

            // Wrong the first time the word is asked; right thereafter.
            var answer = isTarget && !missedOnce
                ? "deliberately wrong"
                : AnswerFor(session, itemId);
            if (isTarget && !missedOnce) missedOnce = true;

            var response = await Client.PostAsJsonAsync(
                $"/api/sessions/{sessionId}/answer", new { itemId, answer });
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (result.GetProperty("requeued").GetBoolean()) requeued = true;

            next = result.GetProperty("progress").GetProperty("nextItemId");
        }

        // The word is what the session exists to teach, so it comes back —
        // meeting it again is what turns a miss into reinforcement.
        Assert.True(requeued, "the missed word should have been asked again");

        var complete = await Client.PostAsync(
            $"/api/sessions/{sessionId}/complete", null);
        var outcome = (await complete.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("words").EnumerateArray().Single();

        // Reinforced, not passed. Only a first-attempt success advances a word
        // — the retry exists to fix the memory, not the score (§31).
        Assert.False(outcome.GetProperty("passed").GetBoolean());
        Assert.Equal(wordId, outcome.GetProperty("wordId").GetGuid());
    }

    /// <summary>The right answer for an item, from the session it belongs to.</summary>
    private static string AnswerFor(JsonElement session, Guid itemId)
    {
        var item = session.GetProperty("items").EnumerateArray()
            .FirstOrDefault(i => i.GetProperty("id").GetGuid() == itemId);

        if (item.ValueKind == JsonValueKind.Undefined) return "anything";

        // The target-word question asks for the meaning the learner chose.
        if (item.GetProperty("type").GetString() == "TARGET_WORD")
        {
            return session.GetProperty("targetWords").EnumerateArray()
                .First(w => w.GetProperty("wordId").GetGuid()
                            == item.GetProperty("wordId").GetGuid())
                .GetProperty("meaning").GetString()!;
        }

        return item.GetProperty("options").EnumerateArray()
            .First().GetString() ?? "anything";
    }

    // ── The Speaking warm-up ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task Speaking_opens_with_its_words_and_four_meanings_each()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpeakingAsync();

        var session = await StartAsync("speaking");
        var warmup = session.GetProperty("warmup").EnumerateArray().ToList();

        // A spoken conversation gives no time to look anything up: by the time
        // the learner realises they cannot recall a word, the tutor has asked
        // the question. So the meanings are checked first — actively.
        Assert.NotEmpty(warmup);
        Assert.All(warmup, w =>
        {
            Assert.False(string.IsNullOrWhiteSpace(
                w.GetProperty("text").GetString()));
            Assert.Equal(4, w.GetProperty("options").GetArrayLength());
        });

        // The key is never sent. A warm-up the client could mark itself is a
        // warm-up the client could skip.
        var raw = await (await Client.PostAsync(
            "/api/sessions/speaking/start", null)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("correctAnswer", raw, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task A_missed_warmup_word_changes_nothing_at_all()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();
        var wordId = await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpeakingAsync();

        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        Domain.Words.Word before;
        Domain.Levels.SkillLevel levelBefore;
        await using (var context = db.CreateContext())
        {
            before = await context.Words.AsNoTracking()
                .Include(w => w.Events)
                .FirstAsync(w => w.Id == wordId);
            levelBefore = await context.SkillLevels.AsNoTracking()
                .FirstAsync(l => l.UserId == userId && l.Skill == SkillType.Speaking);
        }

        var wrong = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/warmup/answer",
            new { wordId, answer = "not the meaning" });
        wrong.EnsureSuccessStatusCode();

        var result = await wrong.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(result.GetProperty("isCorrect").GetBoolean());
        // Told, so the learner learns it before meeting it again.
        Assert.Equal("بحث علمي", result.GetProperty("correctAnswer").GetString());

        var right = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/warmup/answer",
            new { wordId, answer = "بحث علمي" });
        Assert.True((await right.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("isCorrect").GetBoolean());

        await using var after = db.CreateContext();
        var word = await after.Words.AsNoTracking()
            .Include(w => w.Events)
            .FirstAsync(w => w.Id == wordId);
        var levelAfter = await after.SkillLevels.AsNoTracking()
            .FirstAsync(l => l.UserId == userId && l.Skill == SkillType.Speaking);

        // It is a warm-up, not a test. Nothing about the learner changed: not
        // the word, not its history, not their level.
        Assert.Equal(before.State, word.State);
        Assert.Equal(before.CurrentSkill, word.CurrentSkill);
        Assert.Equal(before.Events.Count, word.Events.Count);
        Assert.Equal(levelBefore.RollingAccuracy, levelAfter.RollingAccuracy);
        Assert.Equal(levelBefore.EvaluationSessions, levelAfter.EvaluationSessions);
    }

    [SkippableFact]
    public async Task A_warmup_word_belongs_to_the_caller_or_it_is_not_found()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpeakingAsync();
        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        // Somebody else's word id, guessed. The session is the caller's; the
        // word in the body is not trusted on its own.
        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/warmup/answer",
            new { wordId = Guid.NewGuid(), answer = "بحث علمي" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task A_conversation_changes_level_immediately_and_keeps_talking()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpeakingAsync();

        var session = await StartAsync("speaking");
        var sessionId = session.GetProperty("id").GetGuid();

        // Say something first: a conversation in progress must still accept a
        // level change, unlike a passage, because there is nothing to replace.
        var turn = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/speaking/turn",
            new { transcript = "I do research about sleep every weekend." });
        turn.EnsureSuccessStatusCode();

        var changed = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/level", new { level = "A2" });
        changed.EnsureSuccessStatusCode();

        var body = await changed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("A2", body.GetProperty("levelUsed").GetString());

        // The next thing the tutor says is generated at the new level.
        await using var context = db.CreateContext();
        var stored = await context.SkillSessions.AsNoTracking()
            .FirstAsync(x => x.Id == sessionId);
        Assert.Equal(CefrLevel.A2, stored.LevelUsed);

        // And it is the learner's setting now, as it is for a passage.
        var level = await context.SkillLevels.AsNoTracking()
            .FirstAsync(l => l.UserId == userId && l.Skill == SkillType.Speaking);
        Assert.Equal(CefrLevel.A2, level.UserSelectedLevel);
    }

    [SkippableFact]
    public async Task A_two_word_entry_can_actually_be_spelled()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("alarm clock", "منبّه");
        await AdvanceToSpellingAsync();

        var session = await StartAsync("spelling");
        var item = session.GetProperty("items").EnumerateArray().Single();

        var letters = item.GetProperty("letters").EnumerateArray()
            .Select(l => l.GetString()!)
            .ToList();

        // Without a space tile the pool is a puzzle with no solution: every
        // letter can be laid out and the word still is not written (ADR-042).
        Assert.Contains(" ", letters);

        // Every letter is there too, counted properly.
        foreach (var group in "alarmclock".GroupBy(c => c))
        {
            Assert.True(
                letters.Count(l => l == group.Key.ToString()) >= group.Count(),
                $"not enough '{group.Key}' tiles");
        }
    }

    [SkippableTheory]
    [InlineData("alarm clock")]
    [InlineData("alarmclock")]
    [InlineData("ALARM CLOCK")]
    [InlineData("alarm  clock")]
    public async Task Spelling_ignores_how_the_spaces_fell(string answer)
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("alarm clock", "منبّه");
        await AdvanceToSpellingAsync();

        var session = await StartAsync("spelling");
        var sessionId = session.GetProperty("id").GetGuid();
        var item = session.GetProperty("items").EnumerateArray().Single();

        var response = await Client.PostAsJsonAsync(
            $"/api/sessions/{sessionId}/answer",
            new { itemId = item.GetProperty("id").GetGuid(), answer });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The exercise is the letters. A learner with every letter right must
        // not be failed by a gap — least of all one they had to tap for.
        Assert.True(body.GetProperty("isCorrect").GetBoolean(),
            $"\"{answer}\" should count as spelling \"alarm clock\"");
    }

    // ── The spelling hint ladder ─────────────────────────────────────────────

    [SkippableTheory]
    // One ladder, entered at the rung that suits the learner. A full WordNet
    // definition is often harder than the word it defines, so handing one to an
    // A2 learner tests their reading rather than helping them spell.
    [InlineData(CefrLevel.C1, new[]
    {
        "DEFINITION_EN", "SIMPLIFIED_DEFINITION", "SYNONYM",
        "ARABIC_MEANING", "LETTER_COUNT",
    })]
    [InlineData(CefrLevel.B2, new[]
    {
        "SIMPLIFIED_DEFINITION", "SYNONYM", "ARABIC_MEANING", "LETTER_COUNT",
    })]
    [InlineData(CefrLevel.B1, new[]
    {
        "SYNONYM", "ARABIC_MEANING", "LETTER_COUNT",
    })]
    [InlineData(CefrLevel.A2, new[] { "ARABIC_MEANING", "LETTER_COUNT" })]
    [InlineData(CefrLevel.A1, new[] { "ARABIC_MEANING", "LETTER_COUNT" })]
    public async Task The_hint_ladder_starts_where_the_learner_is(
        CefrLevel level, string[] expected)
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();
        await AddWordWithSynonymAsync("research", "بحث علمي", "inquiry");
        await AdvanceToSpellingAsync();

        // Spelling carries no band of its own, so its content follows Reading
        // (ADR-008).
        await using (var context = db.CreateContext())
        {
            var reading = await context.SkillLevels.FirstAsync(
                l => l.UserId == userId && l.Skill == SkillType.Reading);
            reading.SetUserSelectedLevel(level);
            await context.SaveChangesAsync();
        }

        var session = await StartAsync("spelling");
        var item = session.GetProperty("items").EnumerateArray().Single();

        var rungs = item.GetProperty("hints").EnumerateArray()
            .Select(h => h.GetProperty("kind").GetString())
            .ToArray();

        Assert.Equal(expected, rungs);

        // The first rung is what the learner sees before asking for anything;
        // the rest arrive one press at a time.
        Assert.Equal(expected[0], item.GetProperty("clueKind").GetString());
        Assert.Equal(
            item.GetProperty("hints")[0].GetProperty("text").GetString(),
            item.GetProperty("clue").GetString());
    }

    [SkippableFact]
    public async Task Every_rung_says_something_and_none_gives_the_word_away()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordWithSynonymAsync("research", "بحث علمي", "inquiry");
        await AdvanceToSpellingAsync();

        var session = await StartAsync("spelling");
        var item = session.GetProperty("items").EnumerateArray().Single();
        var hints = item.GetProperty("hints").EnumerateArray().ToList();

        Assert.NotEmpty(hints);
        Assert.All(hints, h =>
        {
            var text = h.GetProperty("text").GetString();
            Assert.False(string.IsNullOrWhiteSpace(text));

            // A hint that contains the answer is not a hint.
            Assert.DoesNotContain("research", text!,
                StringComparison.OrdinalIgnoreCase);
        });

        // No two rungs say the same thing — pressing "hint" must change
        // something, or the learner concludes it is broken.
        var texts = hints.Select(h => h.GetProperty("text").GetString()).ToList();
        Assert.Equal(texts.Count, texts.Distinct().Count());

        // The last rung is the count of letters, which is the final resort and
        // the only rung that is about the spelling rather than the meaning.
        Assert.Equal("LETTER_COUNT", hints[^1].GetProperty("kind").GetString());
        Assert.Equal("8", hints[^1].GetProperty("text").GetString());
    }

    [SkippableFact]
    public async Task A_word_with_no_synonym_simply_has_one_rung_fewer()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        // Seeded through the ordinary path: no second lemma shares its gloss.
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpellingAsync();

        var session = await StartAsync("spelling");
        var item = session.GetProperty("items").EnumerateArray().Single();

        var kinds = item.GetProperty("hints").EnumerateArray()
            .Select(h => h.GetProperty("kind").GetString())
            .ToList();

        // Missing material is skipped rather than shown blank: a rung that says
        // nothing is a press that appears to do nothing.
        Assert.DoesNotContain("SYNONYM", kinds);
        Assert.Contains("ARABIC_MEANING", kinds);
        Assert.Contains("LETTER_COUNT", kinds);
    }

    // ── Starting a session is exclusive, and cheap to lose ───────────────────
    //
    // These cover the defects found by the August 2026 audit. Each one is a
    // rule the service already believed it enforced.

    [SkippableFact]
    public async Task Simultaneous_starts_produce_one_session_and_one_AI_call()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        var before = Ai.ContentCalls;

        // Six taps arriving together, which is what a double-tap on a slow
        // connection looks like at the server. This used to produce six
        // sessions and six generations; the passage is the expensive part, so
        // that was the bill multiplied by six for one learner's impatience.
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            Client.PostAsync("/api/sessions/reading/start", null)));

        // Two honest answers, depending on where in the winner's generation a
        // request landed: the finished session, or "it is still being
        // prepared". Never a second session, and never a 500.
        var ids = new HashSet<Guid>();
        foreach (var response in responses)
        {
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("SESSION_STARTING", problem
                    .GetProperty("error").GetProperty("code").GetString());
                continue;
            }

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            ids.Add(body.GetProperty("id").GetGuid());
        }

        // The two invariants that matter, and the two that used to break: one
        // session, and one passage paid for.
        Assert.Single(ids);
        Assert.Equal(before + 1, Ai.ContentCalls);

        // Scoped to this learner: the suite shares one database, and the index
        // being tested is per (user, skill) — other tests legitimately hold an
        // open Reading session of their own.
        await using var context = db.CreateContext();
        Assert.Equal(1, await context.SkillSessions.CountAsync(
            x => x.UserId == userId && !x.IsComplete
                 && x.Skill == SkillType.Reading));
    }

    [SkippableFact]
    public async Task Abandoning_a_finished_session_is_refused_not_obeyed()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");

        var session = await StartAsync("reading");
        var sessionId = session.GetProperty("id").GetGuid();
        await FinishAsync(session);

        var response = await Client.PostAsync(
            $"/api/sessions/{sessionId}/abandon", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // The point of the refusal: the session is the record of what happened,
        // and this service exists to measure itself. Obeying the abandon
        // deleted the row, its items, the comprehension score and the token
        // cost, leaving word outcomes with no evidence behind them.
        await using var context = db.CreateContext();
        Assert.True(await context.SkillSessions.AnyAsync(x => x.Id == sessionId));
        Assert.True(await context.SessionItems.AnyAsync(x => x.SessionId == sessionId));
    }

    [SkippableFact]
    public async Task Completing_early_leaves_the_unasked_words_exactly_as_they_were()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();
        var wordId = await AddWordAsync("research", "بحث علمي");

        var session = await StartAsync("reading");
        var sessionId = session.GetProperty("id").GetGuid();

        DateTimeOffset? availableBefore;
        await using (var context = db.CreateContext())
        {
            var word = await context.Words.Include(w => w.Skills)
                .FirstAsync(w => w.Id == wordId);
            availableBefore = word.SkillState(SkillType.Reading).AvailableAt;
        }

        // Not a single item answered — a client that posts /complete with the
        // queue still full, or a learner who force-quits at the wrong moment.
        var response = await Client.PostAsync(
            $"/api/sessions/{sessionId}/complete", null);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();

        var outcome = result.GetProperty("words").EnumerateArray()
            .Single(w => w.GetProperty("wordId").GetGuid() == wordId);

        Assert.True(outcome.GetProperty("untouched").GetBoolean());
        Assert.False(outcome.GetProperty("passed").GetBoolean());

        // A word nobody asked about did not fail. It used to come back FAILED
        // with zero attempts and a two-day wait, which is a false reading in
        // the only data the experiment has.
        await using (var context = db.CreateContext())
        {
            var word = await context.Words.Include(w => w.Skills)
                .FirstAsync(w => w.Id == wordId);
            var state = word.SkillState(SkillType.Reading);

            Assert.Equal(SkillType.Reading, word.CurrentSkill);
            Assert.NotEqual(SkillStatus.Failed, state.Status);
            Assert.Equal(availableBefore, state.AvailableAt);
        }

        // And it is still there to be practised today.
        var hub = await Client.GetFromJsonAsync<JsonElement>("/api/hub");
        Assert.Equal(1, hub.GetProperty("skills").EnumerateArray()
            .First(s => s.GetProperty("skill").GetString() == "READING")
            .GetProperty("dueWordCount").GetInt32());
    }

    [SkippableFact]
    public async Task An_answered_word_is_still_judged_normally()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var wordId = await AddWordAsync("research", "بحث علمي");

        var result = await FinishAsync(await StartAsync("reading"));

        var outcome = result.GetProperty("words").EnumerateArray()
            .Single(w => w.GetProperty("wordId").GetGuid() == wordId);

        // The guard above must not turn into "nothing ever fails or passes".
        Assert.True(outcome.GetProperty("passed").GetBoolean());
        Assert.Equal("LISTENING", outcome.GetProperty("nextSkill").GetString());
        Assert.False(
            outcome.TryGetProperty("untouched", out var untouched)
            && untouched.GetBoolean());
    }

    // ── Practice must not stand in the way of real words ─────────────────────

    [SkippableFact]
    public async Task A_practice_session_gives_way_when_real_words_are_due()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var practice = await Client.PostAsync(
            "/api/sessions/reading/start?practice=true", null);
        practice.EnsureSuccessStatusCode();
        var practiceId = (await practice.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        await AddWordAsync("research", "بحث علمي");

        var real = await StartAsync("reading");

        // The learner asked for their pipeline and got it. Before this, they
        // were handed the practice session back and had no way past it.
        Assert.NotEqual(practiceId, real.GetProperty("id").GetGuid());
        Assert.False(real.GetProperty("isPractice").GetBoolean());
        Assert.Contains(
            real.GetProperty("targetWords").EnumerateArray(),
            w => w.GetProperty("text").GetString() == "research");
    }

    [SkippableFact]
    public async Task A_practice_session_survives_a_start_that_had_nothing_to_offer()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var practice = await Client.PostAsync(
            "/api/sessions/reading/start?practice=true", null);
        practice.EnsureSuccessStatusCode();
        var practiceId = (await practice.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        // Nothing is due, so there is nothing to give way *to*. Dropping the
        // practice here would cost the learner their place for no gain.
        var real = await Client.PostAsync("/api/sessions/reading/start", null);
        Assert.Equal(HttpStatusCode.Conflict, real.StatusCode);

        var resumed = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/sessions/{practiceId}");
        Assert.Equal(practiceId, resumed.GetProperty("id").GetGuid());
    }

    [SkippableFact]
    public async Task A_practice_session_left_open_overnight_is_not_resumed()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var first = await Client.PostAsync(
            "/api/sessions/reading/start?practice=true", null);
        first.EnsureSuccessStatusCode();
        var staleId = (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        Clock.Advance(TimeSpan.FromHours(25));

        var second = await Client.PostAsync(
            "/api/sessions/reading/start?practice=true", null);
        second.EnsureSuccessStatusCode();
        var fresh = await second.Content.ReadFromJsonAsync<JsonElement>();

        // Sessions never expired at all, so one opened in April was still what
        // the skill handed back in August.
        Assert.NotEqual(staleId, fresh.GetProperty("id").GetGuid());
    }

    [SkippableFact]
    public async Task Practice_resumed_the_same_day_still_returns_the_learner_to_it()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var first = await Client.PostAsync(
            "/api/sessions/reading/start?practice=true", null);
        first.EnsureSuccessStatusCode();
        var id = (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetGuid();

        // The case the expiry must not break: away for lunch, not for a week.
        Clock.Advance(TimeSpan.FromHours(3));

        var second = await Client.PostAsync(
            "/api/sessions/reading/start?practice=true", null);
        second.EnsureSuccessStatusCode();
        var resumed = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(id, resumed.GetProperty("id").GetGuid());
    }
}
