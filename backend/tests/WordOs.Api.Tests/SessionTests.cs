using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WordOs.Domain.Common;
using WordOs.Domain.Lexicon;

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
    public async Task Spelling_gives_a_clue_letters_and_a_hint_but_no_level()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        await AddWordAsync("research", "بحث علمي");
        await AdvanceToSpellingAsync();

        var session = await StartAsync("spelling");
        var item = session.GetProperty("items").EnumerateArray().Single();

        Assert.Equal("SPELLING_TASK", item.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("clue").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("hint").GetString()));
        Assert.Equal("LETTER_TILES", item.GetProperty("inputMode").GetString());
        Assert.Equal(
            "research".Length,
            item.GetProperty("letters").GetArrayLength());

        // Spelling carries no CEFR level of its own (ADR-008).
        await using var context = db.CreateContext();
        var level = await context.SkillLevels
            .FirstAsync(l => l.Skill == SkillType.Spelling);
        Assert.Null(level.UserSelectedLevel);
        Assert.Null(level.SystemAssessedLevel);
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
}
