using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Placement;

namespace WordOs.Api.Tests;

/// <summary>
/// The adaptive placement test driven over real HTTP against real PostgreSQL.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PlacementApiTests(PostgresFixture db) : IAsyncLifetime
{
    private ApiFactory? _factory;

    private StubAiContentService Ai => _factory!.Ai;
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

    private async Task<Guid> SignInAsync()
    {
        var response = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"place-{Guid.NewGuid():N}@test.dev",
            password = "correct-horse-battery",
            displayName = "Learner",
            phoneCountryCode = "967",
            phoneNumber = "770000001",
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return body.GetProperty("user").GetProperty("id").GetGuid();
    }

    private static string AnswerFor(string itemId, bool correctly)
    {
        var item = PlacementItemBank.Find(itemId)!;

        if (item.IsFreeText)
        {
            return correctly
                ? "I usually plan my week carefully because it helps me focus, and "
                  + "when something unexpected happens I adjust the plan instead of "
                  + "abandoning it entirely, which keeps my progress steady."
                : "no";
        }

        return correctly
            ? item.CorrectAnswer!
            : item.Options.First(o => o != item.CorrectAnswer);
    }

    /// <summary>Plays a whole placement over HTTP and returns the result body.</summary>
    private async Task<(JsonElement Result, int Questions)> PlayAsync(
        Func<string, bool> answerCorrectly)
    {
        var start = await Client.PostAsync("/api/placement/start", null);
        start.EnsureSuccessStatusCode();
        var step = await start.Content.ReadFromJsonAsync<JsonElement>();

        var sessionId = step.GetProperty("sessionId").GetGuid();
        var asked = 0;

        while (!step.GetProperty("isComplete").GetBoolean())
        {
            Assert.True(++asked < 60, "the adaptive loop must terminate");

            var itemId = step.GetProperty("item").GetProperty("id").GetString()!;
            var response = await Client.PostAsJsonAsync(
                $"/api/placement/{sessionId}/answer",
                new { itemId, answer = AnswerFor(itemId, answerCorrectly(itemId)) });

            response.EnsureSuccessStatusCode();
            step = await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        var complete = await Client.PostAsync(
            $"/api/placement/{sessionId}/complete", null);
        complete.EnsureSuccessStatusCode();

        return (await complete.Content.ReadFromJsonAsync<JsonElement>(), asked);
    }

    // ── The protocol ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Starting_returns_the_first_question_without_its_answer()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var response = await Client.PostAsync("/api/placement/start", null);
        response.EnsureSuccessStatusCode();
        var step = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(step.GetProperty("isComplete").GetBoolean());
        var item = step.GetProperty("item");

        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("prompt").GetString()));

        // Neither the answer nor the difficulty band ever reaches the client.
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("correctAnswer", raw);
        Assert.DoesNotContain("\"level\"", raw);
        Assert.DoesNotContain("difficulty", raw, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Answering_a_question_that_is_not_the_current_one_is_rejected()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var start = await Client.PostAsync("/api/placement/start", null);
        var step = await start.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = step.GetProperty("sessionId").GetGuid();
        var current = step.GetProperty("item").GetProperty("id").GetString()!;

        var other = PlacementItemBank.All.First(i => i.Id != current).Id;
        var response = await Client.PostAsJsonAsync(
            $"/api/placement/{sessionId}/answer",
            new { itemId = other, answer = "anything" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("ITEM_NOT_CURRENT", await response.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task Replaying_the_same_answer_cannot_inflate_the_estimate()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var start = await Client.PostAsync("/api/placement/start", null);
        var step = await start.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = step.GetProperty("sessionId").GetGuid();
        var itemId = step.GetProperty("item").GetProperty("id").GetString()!;

        await Client.PostAsJsonAsync($"/api/placement/{sessionId}/answer",
            new { itemId, answer = AnswerFor(itemId, true) });

        // The same item again: it is no longer current, so it is refused.
        var replay = await Client.PostAsJsonAsync($"/api/placement/{sessionId}/answer",
            new { itemId, answer = AnswerFor(itemId, true) });

        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);

        await using var context = db.CreateContext();
        var answers = await context.PlacementAnswers
            .CountAsync(a => a.SessionId == sessionId && a.ItemId == itemId);
        Assert.Equal(1, answers);
    }

    [SkippableFact]
    public async Task Completing_an_unfinished_test_is_rejected()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var start = await Client.PostAsync("/api/placement/start", null);
        var step = await start.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = step.GetProperty("sessionId").GetGuid();

        var response = await Client.PostAsync(
            $"/api/placement/{sessionId}/complete", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("PLACEMENT_INCOMPLETE",
            await response.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task Another_learners_run_is_not_addressable()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        await SignInAsync();
        var start = await Client.PostAsync("/api/placement/start", null);
        var step = await start.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = step.GetProperty("sessionId").GetGuid();
        var itemId = step.GetProperty("item").GetProperty("id").GetString()!;

        await SignInAsync(); // a different learner, who knows the id

        var answer = await Client.PostAsJsonAsync(
            $"/api/placement/{sessionId}/answer",
            new { itemId, answer = AnswerFor(itemId, true) });
        var complete = await Client.PostAsync(
            $"/api/placement/{sessionId}/complete", null);

        Assert.Equal(HttpStatusCode.NotFound, answer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, complete.StatusCode);
    }

    // ── A full run ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_full_run_asks_between_12_and_22_questions_and_places_the_learner()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();

        var (result, asked) = await PlayAsync(_ => true);

        Assert.InRange(asked, 12, 22);

        var levels = result.GetProperty("levels").EnumerateArray().ToList();

        // Four, not five: Spelling is measured but is not a primary skill the
        // learner is shown (§13, §21).
        Assert.Equal(4, levels.Count);
        Assert.DoesNotContain(levels,
            l => l.GetProperty("skill").GetString() == "SPELLING");

        // A strong run places at B1 or above on every levelled skill.
        foreach (var level in levels.Where(
                     l => l.GetProperty("skill").GetString() != "SPELLING"))
        {
            var wire = level.GetProperty("systemAssessedLevel").GetString();
            Assert.NotNull(wire);
            var parsed = CefrLevelExtensions.TryFromWire(wire)!.Value;
            Assert.True(parsed.Rank() >= CefrLevel.B1.Rank(),
                $"{level.GetProperty("skill")} placed at {wire}");
        }

        // Onboarding advances and the levels are persisted.
        await using var context = db.CreateContext();
        var user = await context.Users
            .Include(u => u.SkillLevels)
            .SingleAsync(u => u.Id == userId);

        Assert.Equal(OnboardingStage.Complete, user.OnboardingStage);
        Assert.All(user.SkillLevels.Where(l => l.CarriesCefrLevel),
            l => Assert.NotNull(l.SystemAssessedLevel));
    }

    [SkippableFact]
    public async Task A_weak_run_places_low()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var (result, _) = await PlayAsync(_ => false);

        foreach (var level in result.GetProperty("levels").EnumerateArray()
                     .Where(l => l.GetProperty("skill").GetString() != "SPELLING"))
        {
            var wire = level.GetProperty("systemAssessedLevel").GetString()!;
            var parsed = CefrLevelExtensions.TryFromWire(wire)!.Value;
            Assert.True(parsed.Rank() <= CefrLevel.A2.Rank(),
                $"{level.GetProperty("skill")} placed at {wire}");
        }
    }

    [SkippableFact]
    public async Task Every_answer_is_stored_as_evidence_not_just_the_final_level()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();

        var (result, _) = await PlayAsync(_ => true);

        await using var context = db.CreateContext();
        var session = await context.PlacementSessions
            .Include(p => p.Answers)
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.StartedAt)
            .FirstAsync();

        // §27: a historical result stays interpretable only if the instrument
        // that produced it is recorded alongside it.
        Assert.Equal(PlacementVersion.Current, session.TestVersion);
        Assert.Equal(session.TestVersion,
            result.GetProperty("testVersion").GetInt32());

        // §26: the underlying evidence, not only the level.
        Assert.NotEmpty(session.Answers);
        Assert.All(session.Answers, a =>
        {
            Assert.NotEqual(string.Empty, a.ItemId);
            // The band the item was authored for — a correct answer means
            // nothing without knowing how hard the question was.
            Assert.True(a.Level.Rank() >= CefrLevel.A1.Rank());
        });

        // What the learner actually produced, kept for the analytics layer.
        var produced = session.Answers.Where(a => a.RawAnswer != null).ToList();
        Assert.NotEmpty(produced);

        // Grammar is recorded as grammar, even though it scores into Writing —
        // otherwise the evidence could never be re-examined as grammar.
        var grammar = session.Answers
            .Where(a => a.Domain == PlacementDomain.Grammar)
            .ToList();

        if (grammar.Count > 0)
        {
            Assert.All(grammar,
                a => Assert.Equal(SkillType.Speaking, a.AlsoEvidenceFor));
        }
    }

    [SkippableFact]
    public async Task Spelling_is_measured_but_never_levelled()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();

        var (result, _) = await PlayAsync(_ => true);

        // Measured, stored, and used — but never presented as a fifth level.
        Assert.DoesNotContain(result.GetProperty("levels").EnumerateArray(),
            l => l.GetProperty("skill").GetString() == "SPELLING");

        var diagnostic = result.GetProperty("spelling");
        Assert.True(diagnostic.GetProperty("itemsAnswered").GetInt32() > 0);
        Assert.Equal("FREE_TYPING", diagnostic.GetProperty("supportMode").GetString());

        // And it stays null in the database, not a sentinel A1.
        await using var context = db.CreateContext();
        var level = await context.SkillLevels.SingleAsync(
            l => l.UserId == userId && l.Skill == SkillType.Spelling);

        Assert.Null(level.SystemAssessedLevel);
        Assert.Equal(SpellingInputMode.FreeTyping, level.SpellingSupportMode);
    }

    [SkippableFact]
    public async Task A_weak_speller_starts_on_letter_tiles()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var (result, _) = await PlayAsync(_ => false);

        Assert.Equal("LETTER_TILES",
            result.GetProperty("spelling").GetProperty("supportMode").GetString());
    }

    [SkippableFact]
    public async Task An_erratic_learner_is_told_the_level_is_provisional()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var flip = false;
        var (result, _) = await PlayAsync(_ => flip = !flip);

        // The exact words are copy and will change; what must not change is
        // that a shaky result admits it rather than presenting itself as final.
        var summary = result.GetProperty("summary").GetString()!;
        Assert.True(
            summary.Contains("rough", StringComparison.OrdinalIgnoreCase) ||
            summary.Contains("settle", StringComparison.OrdinalIgnoreCase) ||
            summary.Contains("provisional", StringComparison.OrdinalIgnoreCase),
            $"a low-confidence result should say so — got: {summary}");

        // A level is still assigned — we never refuse to place a learner.
        Assert.All(result.GetProperty("levels").EnumerateArray()
                .Where(l => l.GetProperty("skill").GetString() != "SPELLING")
                .ToList(),
            l => Assert.Equal(JsonValueKind.String,
                l.GetProperty("systemAssessedLevel").ValueKind));
    }

    [SkippableFact]
    public async Task Placement_records_the_level_history_as_PLACEMENT()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();

        await PlayAsync(_ => true);

        await using var context = db.CreateContext();
        var changes = await context.LevelChanges
            .Where(c => c.UserId == userId).ToListAsync();

        Assert.Equal(5, changes.Count);
        Assert.All(changes,
            c => Assert.Equal(LevelChangeType.Placement, c.ChangeType));
    }

    [SkippableFact]
    public async Task Starting_again_abandons_the_previous_unfinished_run()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();

        var first = await Client.PostAsync("/api/placement/start", null);
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("sessionId").GetGuid();

        var second = await Client.PostAsync("/api/placement/start", null);
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("sessionId").GetGuid();

        Assert.NotEqual(firstId, secondId);

        // Resuming a half-scored estimate would corrupt the level.
        await using var context = db.CreateContext();
        var open = await context.PlacementSessions
            .CountAsync(s => s.UserId == userId && !s.IsComplete);
        Assert.Equal(1, open);
    }

    [SkippableFact]
    public async Task Every_answer_is_scored_server_side_and_persisted()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var start = await Client.PostAsync("/api/placement/start", null);
        var step = await start.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = step.GetProperty("sessionId").GetGuid();
        var itemId = step.GetProperty("item").GetProperty("id").GetString()!;

        await Client.PostAsJsonAsync($"/api/placement/{sessionId}/answer",
            new { itemId, answer = AnswerFor(itemId, true) });

        await using var context = db.CreateContext();
        var answer = await context.PlacementAnswers
            .SingleAsync(a => a.SessionId == sessionId && a.ItemId == itemId);

        // Score and difficulty are derived from the item the server issued —
        // the client sends neither.
        Assert.Equal(1.0, answer.Score, 3);
        var expected = new PlacementConfig().Scale.DifficultyOf(
            PlacementItemBank.Find(itemId)!.Level);
        Assert.Equal(expected, answer.Difficulty, 6);
    }

    [SkippableFact]
    public async Task An_oversized_answer_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var start = await Client.PostAsync("/api/placement/start", null);
        var step = await start.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = step.GetProperty("sessionId").GetGuid();
        var itemId = step.GetProperty("item").GetProperty("id").GetString()!;

        var response = await Client.PostAsJsonAsync(
            $"/api/placement/{sessionId}/answer",
            new { itemId, answer = new string('a', 10_000) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task A_spoken_item_is_marked_spoken_so_the_client_shows_a_microphone()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // Walk the whole test collecting what each item claimed to be.
        var start = await Client.PostAsync("/api/placement/start", null);
        start.EnsureSuccessStatusCode();
        var step = await start.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = step.GetProperty("sessionId").GetGuid();

        var types = new List<(string Skill, string Type)>();
        var guard = 0;

        while (!step.GetProperty("isComplete").GetBoolean() && guard++ < 80)
        {
            var item = step.GetProperty("item");
            types.Add((item.GetProperty("skill").GetString()!,
                       item.GetProperty("type").GetString()!));

            var options = item.GetProperty("options").EnumerateArray().ToList();
            var answer = options.Count > 0
                ? options[0].GetString()!
                : "I usually study in the evening because it is quieter then.";

            var response = await Client.PostAsJsonAsync(
                $"/api/placement/{sessionId}/answer",
                new { itemId = item.GetProperty("id").GetString(), answer });
            response.EnsureSuccessStatusCode();
            step = await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        var speaking = types.Where(t => t.Skill == "SPEAKING").ToList();
        Assert.NotEmpty(speaking);

        // Every Speaking item must say SPOKEN. Reported as FREE_TEXT the client
        // renders a text box, which measures writing and files it under
        // Speaking — the one thing a placement test must never do (§17).
        Assert.All(speaking, t => Assert.Equal("SPOKEN", t.Type));

        // And the distinction is real: Writing still asks for text.
        Assert.DoesNotContain(types, t => t.Skill == "WRITING" && t.Type == "SPOKEN");
    }

    // ── Who scores what ──────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Speaking_and_writing_are_scored_by_the_AI_and_nothing_else_is()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var userId = await SignInAsync();

        await PlayAsync(_ => true);

        // One call per productive skill, at the end — not one per answer
        // during the test, which would put a round-trip between every question
        // and the next.
        Assert.Equal(2, Ai.PlacementEvaluations);

        await using var context = db.CreateContext();
        var answers = await context.PlacementAnswers
            .Where(a => context.PlacementSessions
                .Any(s => s.Id == a.SessionId && s.UserId == userId))
            .ToListAsync();

        // Reading and Listening have an answer key, so they are matched against
        // it: a correct answer is exactly 1, and no model was consulted.
        Assert.All(
            answers.Where(a => a.Skill is SkillType.Reading or SkillType.Listening),
            a =>
            {
                Assert.True(a.Score is 0 or 1, $"{a.ItemId} scored {a.Score}");
                Assert.Null(a.AiEstimatedLevel);
            });

        // Speaking and Writing have no key *when they are produced language*,
        // and there the AI's rating is what stands. Grammar items are filed
        // under Writing but are multiple-choice — they have a key, so they are
        // matched against it like any other keyed item and never sent.
        Assert.All(
            answers.Where(a => a.Skill is SkillType.Speaking or SkillType.Writing
                               && a.Domain != PlacementDomain.Grammar),
            a => Assert.NotNull(a.AiEstimatedLevel));

        Assert.All(
            answers.Where(a => a.Domain == PlacementDomain.Grammar),
            a => Assert.Null(a.AiEstimatedLevel));
    }

    [SkippableFact]
    public async Task The_AIs_rating_moves_the_productive_bands()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        // The same learner, the same substantial answers, the same correct
        // multiple-choice — only the model's opinion of the produced language
        // differs. Whatever else feeds these bands (grammar items are evidence
        // for both), the AI's rating has to be able to move them.
        await SignInAsync();
        Ai.PlacementScore = 1;
        var (strong, _) = await PlayAsync(_ => true);

        await SignInAsync();
        Ai.PlacementScore = 0;
        var (weak, _) = await PlayAsync(_ => true);

        foreach (var skill in new[] { "SPEAKING", "WRITING" })
        {
            var high = BandOf(strong, skill);
            var low = BandOf(weak, skill);

            Assert.True(low.Rank() < high.Rank(),
                $"{skill}: rating every produced answer 0 placed at {low}, "
                + $"rating them 1 placed at {high} — the AI's judgement is not "
                + "reaching the band");
        }
    }

    private static CefrLevel BandOf(JsonElement result, string skill) =>
        CefrLevelExtensions.TryFromWire(
            result.GetProperty("levels").EnumerateArray()
                .First(l => l.GetProperty("skill").GetString() == skill)
                .GetProperty("systemAssessedLevel").GetString())!.Value;

    [SkippableFact]
    public async Task A_placement_still_finishes_when_the_AI_is_down()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        Ai.Fail = true;

        var (result, _) = await PlayAsync(_ => true);

        // Degraded, not broken: the offline scores recorded during the test
        // stand, and the learner is still placed rather than stranded on the
        // last question of onboarding.
        Assert.All(result.GetProperty("levels").EnumerateArray(),
            l => Assert.NotEqual(JsonValueKind.Null,
                l.GetProperty("systemAssessedLevel").ValueKind));
    }
}
