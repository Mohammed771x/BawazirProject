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
/// What a learner may do to their own vocabulary: remove a word, and decide
/// what a word means.
/// </summary>
/// <remarks>
/// Both are answers to the same complaint. The Arabic glosses are a machine
/// join of three datasets, and it shows: <c>sell</c> offers "أَقْنَعَ بِـ" before it
/// offers "باع". A learner who cannot fix that and cannot remove the result is
/// stuck with a card they know to be wrong for the eight days it takes to
/// mature.
///
/// So: deletion is real to the learner and invisible to the Owner's data
/// (ADR-071), and a meaning may be written by the learner (ADR-072) or taken
/// from the passage that taught it (ADR-073). The <i>word</i> is no longer
/// required to be in the lexicon (ADR-075) — but a CEFR level and a part of
/// speech are still not the learner's to invent, so a word the lexicon lacks
/// gets them from the checker, and a string the checker does not recognise as
/// English gets no further.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class WordOwnershipTests(PostgresFixture db) : IAsyncLifetime
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

    /// <summary>The stubbed AI, for steering the meaning checker (ADR-074).</summary>
    private StubAiContentService Ai => _factory!.Ai;

    // ── Deleting (ADR-071) ────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_deleted_word_leaves_the_learners_vocabulary()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = await SeedAsync("plough", "n", "a farming tool", "محراث");
        var wordId = await AddAsync(new { senseId });

        var deleted = await Client.DeleteAsync($"/api/words/{wordId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // Gone from the list …
        var list = await Client.GetFromJsonAsync<JsonElement>("/api/words");
        Assert.Equal(0, list.GetProperty("total").GetInt32());

        // … and gone as an addressable word. 404 rather than an empty payload:
        // as far as this learner is concerned the id names nothing.
        var detail = await Client.GetAsync($"/api/words/{wordId}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [SkippableFact]
    public async Task A_deleted_word_keeps_its_row_and_its_history()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = await SeedAsync("harrow", "n", "a farming tool", "مسلفة");
        var wordId = await AddAsync(new { senseId });

        (await Client.DeleteAsync($"/api/words/{wordId}")).EnsureSuccessStatusCode();

        await using var context = db.CreateContext();

        // The whole point of a soft delete: the evidence the MVP exists to
        // gather is still there to be read (docs/00-PROJECT-PLAN.md §1).
        var word = await context.Words
            .IgnoreQueryFilters()
            .Include(w => w.Skills)
            .Include(w => w.Events)
            .FirstAsync(w => w.Id == wordId);

        Assert.Equal(WordState.Deleted, word.State);
        Assert.NotNull(word.DeletedAt);
        Assert.Equal(5, word.Skills.Count);
        Assert.Contains(word.Events, e => e.Type == WordEventType.Deleted);

        // And where it had got to is preserved, which is the question the Owner
        // will actually ask about a deletion.
        Assert.Equal(SkillType.Reading, word.CurrentSkill);
    }

    [SkippableFact]
    public async Task A_deleted_word_can_be_added_again_as_a_new_journey()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = await SeedAsync("scythe", "n", "a cutting tool", "منجل");
        var first = await AddAsync(new { senseId });

        (await Client.DeleteAsync($"/api/words/{first}")).EnsureSuccessStatusCode();

        // Without the filter on the unique index this is a 409 for ever, on the
        // strength of a row the learner believes is gone.
        var second = await AddAsync(new { senseId });
        Assert.NotEqual(first, second);

        var list = await Client.GetFromJsonAsync<JsonElement>("/api/words");
        Assert.Equal(1, list.GetProperty("total").GetInt32());
    }

    [SkippableFact]
    public async Task Deleting_twice_is_not_an_error()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = await SeedAsync("sickle", "n", "a cutting tool", "منجل صغير");
        var wordId = await AddAsync(new { senseId });

        // A retried request must not become a failure the learner sees.
        (await Client.DeleteAsync($"/api/words/{wordId}")).EnsureSuccessStatusCode();
        var again = await Client.DeleteAsync($"/api/words/{wordId}");
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [SkippableFact]
    public async Task Another_learners_word_cannot_be_deleted()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = await SeedAsync("spade", "n", "a digging tool", "مجرفة");
        var wordId = await AddAsync(new { senseId });

        // A second learner, holding only the id.
        await SignInAsync();
        var response = await Client.DeleteAsync($"/api/words/{wordId}");

        // 404, not 403: the id must not confirm that it names anything
        // (docs/07-SECURITY.md §4).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var context = db.CreateContext();
        var word = await context.Words.IgnoreQueryFilters()
            .FirstAsync(w => w.Id == wordId);
        Assert.Equal(WordState.Learning, word.State);
    }

    [SkippableFact]
    public async Task A_deleted_word_is_never_served_to_a_session()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var kept = await SeedAsync("furrow", "n", "a trench", "أخدود");
        var dropped = await SeedAsync("tractor", "n", "a farm vehicle", "جرار");

        await AddAsync(new { senseId = kept });
        var droppedId = await AddAsync(new { senseId = dropped });

        (await Client.DeleteAsync($"/api/words/{droppedId}")).EnsureSuccessStatusCode();

        // The eligibility scan is one of two dozen places that read words. It
        // has no `Where(state != Deleted)` of its own and must not need one —
        // that is what the global query filter buys.
        await using var context = db.CreateContext();
        var eligible = await context.Words
            .Where(w => w.State == WordState.Learning)
            .Select(w => w.Id)
            .ToListAsync();

        Assert.DoesNotContain(droppedId, eligible);
    }

    // ── A meaning the learner writes (ADR-072) ────────────────────────────

    [SkippableFact]
    public async Task A_learner_may_write_the_meaning_themselves()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // Seeded the way the real lexicon is wrong: the first sense on offer is
        // not the one anybody means by the word.
        await SeedAsync("vend", "v", "to sell something", "أَقْنَعَ بِـ", rank: 0);

        var body = await AddAndReadAsync(new { text = "vend", customMeaning = "باع" });

        Assert.Equal("باع", body.GetProperty("meaning").GetString());

        // The word's grammar still comes from the lexicon — it is the meaning
        // that is the learner's, not the part of speech or the level.
        Assert.Equal("v", body.GetProperty("partOfSpeech").GetString());
        Assert.Equal("A1", body.GetProperty("cefrLevel").GetString());

        await using var context = db.CreateContext();
        var word = await context.Words.FirstAsync(
            w => w.Id == Guid.Parse(body.GetProperty("id").GetString()!));
        Assert.Equal(MeaningSource.Learner, word.MeaningSource);
    }

    // ── A word the lexicon does not have (ADR-075) ────────────────────────

    [SkippableFact]
    public async Task A_word_the_lexicon_never_heard_of_can_still_be_added()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // Deliberately not seeded. The lexicon is a machine join with holes in
        // it, and the word a learner most wants to write their own meaning for
        // is exactly the one that fell down one.
        Ai.UnknownWordPartOfSpeech = "verb";
        Ai.UnknownWordLevel = "C1";

        var body = await AddAndReadAsync(
            new { text = "flabbergast", customMeaning = "يذهل" });

        Assert.Equal("flabbergast", body.GetProperty("text").GetString());
        Assert.Equal("يذهل", body.GetProperty("meaning").GetString());

        // The three facts the lexicon could not supply came from the checker
        // and were stored — a word missing them enters the pipeline half-built.
        Assert.Equal("verb", body.GetProperty("partOfSpeech").GetString());
        Assert.Equal("C1", body.GetProperty("cefrLevel").GetString());
        Assert.NotEmpty(body.GetProperty("definitionEn").GetString()!);

        // And the checker was told there was no dictionary row, which is what
        // makes it answer the extra question at all.
        Assert.False(Ai.LastCheckKnownWord);
    }

    [SkippableFact]
    public async Task A_string_that_is_not_a_word_is_refused_with_no_way_past_it()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // What used to be the lexicon's job. Nothing can generate a passage
        // around `asdfghjkl`, or clue it in Spelling, so it must not enter a
        // pipeline that would spend five sessions on it — and unlike a
        // contested *meaning*, insisting does not make it a word.
        Ai.RejectWords = true;
        Ai.WordCorrection = null;

        var response = await Client.PostAsJsonAsync("/api/words", new
        {
            text = "asdfghjkl",
            customMeaning = "كلمة",
            acceptAnyway = true,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "WORD_NOT_RECOGNIZED",
            body.GetProperty("error").GetProperty("code").GetString());

        // By text, not by counting the table: these tests share one database,
        // so an empty-collection assertion would be asserting that no other
        // test ran first.
        await using var context = db.CreateContext();
        Assert.False(await context.Words.AnyAsync(w => w.Text == "asdfghjkl"));
    }

    [SkippableFact]
    public async Task A_misspelled_word_comes_back_with_the_spelling_meant()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // The useful half of a refusal: "that is not a word" leaves the learner
        // to guess, and the guess they need is one keystroke away.
        Ai.RejectWords = true;
        Ai.WordCorrection = "receive";

        var response = await Client.PostAsJsonAsync("/api/words", new
        {
            text = "recieve",
            customMeaning = "يستلم",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "receive",
            body.GetProperty("error").GetProperty("correctedWord").GetString());
    }

    [SkippableFact]
    public async Task A_word_the_lexicon_has_is_never_questioned_as_a_word()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        await SeedAsync("obsidian", "n", "a dark volcanic glass", "سبج");

        // The checker is in a mood to deny everything. It is not asked: the
        // lexicon contains the word, so the word is a word, and a model saying
        // otherwise must not be able to refuse a dictionary entry.
        Ai.RejectWords = true;

        var body = await AddAndReadAsync(
            new { text = "obsidian", customMeaning = "زجاج بركاني" });

        Assert.Equal("obsidian", body.GetProperty("text").GetString());
        Assert.True(Ai.LastCheckKnownWord);

        // And its own facts won, rather than the checker's stub ones.
        Assert.Equal("n", body.GetProperty("partOfSpeech").GetString());
        Assert.Equal("a dark volcanic glass",
            body.GetProperty("definitionEn").GetString());
    }

    [SkippableFact]
    public async Task A_part_of_speech_the_app_cannot_say_is_not_stored()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // Rule R2: the checker reports and the backend decides. The field is
        // not decoration — it decides how Speaking invites the word and how
        // Spelling clues it — so a value outside the set this app can label is
        // dropped rather than written through.
        Ai.UnknownWordPartOfSpeech = "gerundive";
        Ai.UnknownWordLevel = "Z9";

        var body = await AddAndReadAsync(
            new { text = "flabbergast", customMeaning = "يذهل" });

        Assert.Equal("", body.GetProperty("partOfSpeech").GetString());

        // And a band off the ladder falls back to the neutral default the
        // level engine corrects from real performance.
        Assert.Equal("B1", body.GetProperty("cefrLevel").GetString());
    }

    [SkippableFact]
    public async Task A_written_meaning_must_be_in_Arabic()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        await SeedAsync("barter", "v", "to trade goods", "قايض");

        // Every skill asks "what does this mean?" and marks the answer against
        // this string. An English one makes its own questions unanswerable, so
        // it is refused now rather than discovered two days later in a session.
        var response = await Client.PostAsJsonAsync("/api/words", new
        {
            text = "barter",
            customMeaning = "to trade",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task The_same_written_meaning_cannot_be_added_twice()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        await SeedAsync("peddle", "v", "to sell goods", "تجول للبيع");

        await AddAsync(new { text = "peddle", customMeaning = "باع متجولاً" });

        var again = await Client.PostAsJsonAsync("/api/words", new
        {
            text = "peddle",
            customMeaning = "باع متجولاً",
        });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [SkippableFact]
    public async Task A_written_meaning_that_duplicates_a_lexicon_one_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // Two different sense ids, one visible card. The unique index cannot see
        // this — only the application can, which is why it checks.
        var senseId = await SeedAsync("hawk", "v", "to sell in the street", "باع");
        await AddAsync(new { senseId });

        var again = await Client.PostAsJsonAsync("/api/words", new
        {
            text = "hawk",
            customMeaning = "باع",
        });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [SkippableFact]
    public async Task A_forged_custom_sense_id_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // The lexicon path takes a sense id on trust that the lexicon issued it.
        // A client that mints a `custom:` id and posts it there would be writing
        // its own meaning through the one door that does not expect one.
        var response = await Client.PostAsJsonAsync("/api/words", new
        {
            senseId = "custom:0123456789abcdef0123456789abcdef",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task An_add_with_neither_a_sense_nor_a_meaning_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var response = await Client.PostAsJsonAsync("/api/words", new { text = "sell" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── The meaning checker (ADR-074) ─────────────────────────────────────

    [SkippableFact]
    public async Task A_written_meaning_is_checked_before_it_is_stored()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        await SeedAsync("ledger", "n", "a book of accounts", "دفتر حسابات");

        var response = await Client.PostAsJsonAsync("/api/words", new
        {
            text = "ledger",
            customMeaning = "دفتر حسابات",
        });

        response.EnsureSuccessStatusCode();
        Assert.Equal(1, Ai.MeaningChecks);

        await using var context = db.CreateContext();
        var word = await context.Words.FirstAsync(w => w.Text == "ledger");
        Assert.Equal(MeaningCheckResult.Approved, word.MeaningCheck);
    }

    [SkippableFact]
    public async Task Only_a_written_meaning_is_checked()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // A lexicon gloss is curated and a passage gloss was written by this
        // service. Neither is the learner's guess, so neither is worth a Gemini
        // call — a check on them spends the learner's money asking the model
        // whether the dictionary is right.
        var senseId = await SeedAsync("quill", "n", "a pen", "ريشة كتابة");
        await AddAsync(new { senseId });

        var sessionId = await SeedSessionWithGlossaryAsync(
            [("quill", "قلم ريشة", "noun")]);
        await AddAsync(new { text = "quill", fromSessionId = sessionId });

        Assert.Equal(0, Ai.MeaningChecks);
    }

    [SkippableFact]
    public async Task A_rejected_meaning_comes_back_with_what_would_be_accepted()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        await SeedAsync("anvil", "n", "a block for shaping metal", "سندان");
        Ai.RejectMeanings = true;

        var response = await Client.PostAsJsonAsync("/api/words", new
        {
            text = "anvil",
            customMeaning = "سيارة",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var error = body.GetProperty("error");

        Assert.Equal("MEANING_REJECTED", error.GetProperty("code").GetString());

        // The refusal has to be actionable: "that is wrong" with nothing beside
        // it leaves the learner exactly where this feature found them.
        Assert.NotEmpty(error.GetProperty("suggestions").EnumerateArray());
        Assert.False(
            string.IsNullOrWhiteSpace(error.GetProperty("message").GetString()));

        await using var context = db.CreateContext();
        Assert.False(await context.Words.AnyAsync(w => w.Text == "anvil"));
    }

    [SkippableFact]
    public async Task The_learner_may_insist_and_it_is_recorded()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        await SeedAsync("bellows", "n", "a device for blowing air", "منفاخ");
        Ai.RejectMeanings = true;

        var body = await AddAndReadAsync(new
        {
            text = "bellows",
            customMeaning = "منفاخ الحداد",
            acceptAnyway = true,
        });

        // Their wording, untouched — the checker does not get to rewrite it.
        Assert.Equal("منفاخ الحداد", body.GetProperty("meaning").GetString());

        await using var context = db.CreateContext();
        var word = await context.Words.FirstAsync(w => w.Text == "bellows");

        // "The model said no and the learner said yes" is what explains a word
        // failing a fortnight later, and it is unrecoverable if nobody wrote it
        // down.
        Assert.Equal(MeaningCheckResult.Overridden, word.MeaningCheck);
    }

    [SkippableFact]
    public async Task A_spelling_correction_is_offered_rather_than_applied()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        await SeedAsync("forge", "n", "a blacksmith's workshop", "مسبك");

        // The checker agrees with the meaning and would spell it differently.
        // Storing the correction silently puts words in the learner's mouth;
        // storing the misspelling teaches it. So it is offered.
        Ai.MeaningCorrection = "مَسبك";

        var response = await Client.PostAsJsonAsync("/api/words", new
        {
            text = "forge",
            customMeaning = "مسبك",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "مَسبك",
            body.GetProperty("error").GetProperty("corrected").GetString());
    }

    [SkippableFact]
    public async Task Nothing_is_stored_when_the_checker_cannot_be_reached()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        await SeedAsync("crucible", "n", "a melting pot", "بوتقة");
        Ai.Fail = true;

        var response = await Client.PostAsJsonAsync("/api/words", new
        {
            text = "crucible",
            customMeaning = "بوتقة",
        });

        // 503, not a silent pass. There is no fallback for "does this Arabic
        // mean what this English word means", and an unchecked meaning that
        // looks exactly like a checked one is worse than asking the learner to
        // wait (ADR-074).
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        await using var context = db.CreateContext();
        Assert.False(await context.Words.AnyAsync(w => w.Text == "crucible"));
    }

    [SkippableFact]
    public async Task An_outage_does_not_block_a_lexicon_add()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // The check exists for meanings the learner invented. A dictionary
        // sense needs none, so a dead AI service must not stop somebody adding
        // a word the ordinary way.
        var senseId = await SeedAsync("tongs", "n", "a gripping tool", "ملقط");
        Ai.Fail = true;

        var response = await Client.PostAsJsonAsync("/api/words", new { senseId });
        response.EnsureSuccessStatusCode();
    }

    // ── The meaning a passage gave it (ADR-073) ───────────────────────────

    [SkippableFact]
    public async Task A_word_added_from_a_passage_keeps_the_passages_meaning()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // The lexicon's answer for this word is wrong for the sentence — which
        // is the ordinary case, not a contrived one: "bank" has six senses and
        // five of them are wrong wherever it appears.
        await SeedAsync("bank", "n", "a financial institution", "مصرف", rank: 0);

        var sessionId = await SeedSessionWithGlossaryAsync(
            [("bank", "ضفة النهر", "noun")]);

        var body = await AddAndReadAsync(new
        {
            text = "bank",
            fromSessionId = sessionId,
        });

        // The meaning the learner actually read, not the commonest sense.
        Assert.Equal("ضفة النهر", body.GetProperty("meaning").GetString());

        await using var context = db.CreateContext();
        var word = await context.Words.FirstAsync(
            w => w.Id == Guid.Parse(body.GetProperty("id").GetString()!));
        Assert.Equal(MeaningSource.Passage, word.MeaningSource);
    }

    [SkippableFact]
    public async Task The_passage_meaning_comes_from_the_server_not_the_request()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        await SeedAsync("crane", "n", "a lifting machine", "رافعة");

        var sessionId = await SeedSessionWithGlossaryAsync(
            [("crane", "طائر الكركي", "noun")]);

        // A client sending its own meaning alongside the session id gets the
        // server's answer, not its own. The client says *which word*; the
        // server says what it meant.
        var body = await AddAndReadAsync(new
        {
            text = "crane",
            customMeaning = "شيء آخر تماماً",
            fromSessionId = sessionId,
        });

        Assert.Equal("طائر الكركي", body.GetProperty("meaning").GetString());
    }

    [SkippableFact]
    public async Task A_word_the_passage_never_glossed_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        await SeedAsync("otter", "n", "a river animal", "قضاعة");
        var sessionId = await SeedSessionWithGlossaryAsync(
            [("bank", "ضفة النهر", "noun")]);

        // Names and numbers appear in generated text and carry no gloss. The
        // refusal is what sends the client back to the ordinary dictionary
        // sheet, which is the right answer for a word this passage never
        // explained.
        var response = await Client.PostAsJsonAsync("/api/words", new
        {
            text = "otter",
            fromSessionId = sessionId,
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Another_learners_passage_cannot_be_read_through_this()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        await SeedAsync("vault", "n", "a secure room", "خزنة");
        var sessionId = await SeedSessionWithGlossaryAsync(
            [("vault", "قبو محصن", "noun")]);

        // A second learner with the session id. Answering anything but "no such
        // session" would make this endpoint a way to read another learner's
        // generated passage one word at a time.
        await SignInAsync();
        var response = await Client.PostAsJsonAsync("/api/words", new
        {
            text = "vault",
            fromSessionId = sessionId,
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<Guid> AddAsync(object body) =>
        Guid.Parse((await AddAndReadAsync(body)).GetProperty("id").GetString()!);

    private async Task<JsonElement> AddAndReadAsync(object body)
    {
        var response = await Client.PostAsJsonAsync("/api/words", body);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// A completed session carrying a stored glossary, written straight to the
    /// database.
    /// </summary>
    /// <remarks>
    /// Generating one for real would need Gemini, and what is under test here
    /// is not generation — it is whether the meaning that was stored is the
    /// meaning that comes back out.
    /// </remarks>
    private async Task<Guid> SeedSessionWithGlossaryAsync(
        (string Word, string Meaning, string PartOfSpeech)[] glossary)
    {
        await using var context = db.CreateContext();

        var userId = await CurrentUserIdAsync(context);
        var session = Domain.Sessions.SkillSession.Start(
            userId, SkillType.Reading, CefrLevel.B1, DateTimeOffset.UtcNow);

        session.SetContent(
            "A short passage.",
            promptVersion: "test",
            model: "test",
            tokens: 0,
            fromFallback: false,
            // PascalCase, because that is what `SessionEndpoints.GlossaryJson`
            // writes — a test that seeds a prettier shape than production would
            // pass while the real rows failed to parse.
            glossaryJson: JsonSerializer.Serialize(glossary.Select(g => new
            {
                g.Word,
                g.Meaning,
                g.PartOfSpeech,
            })),
            title: "Title");

        context.SkillSessions.Add(session);
        await context.SaveChangesAsync();

        return session.Id;
    }

    private async Task<Guid> CurrentUserIdAsync(
        Infrastructure.Persistence.WordOsDbContext context)
    {
        var me = await Client.GetFromJsonAsync<JsonElement>("/api/me");
        var email = me.GetProperty("email").GetString();
        return (await context.Users.FirstAsync(u => u.Email == email)).Id;
    }

    private async Task SignInAsync()
    {
        var response = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"own-{Guid.NewGuid():N}@test.dev",
            password = "correct-horse-battery",
            displayName = "Learner",
            phoneCountryCode = "967",
            phoneNumber = "770000001",
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", body.GetProperty("token").GetString());
    }

    private async Task<string> SeedAsync(
        string text,
        string pos,
        string definitionEn,
        string meaningAr,
        int? rank = 1)
    {
        var senseId = $"own-{Guid.NewGuid():N}";

        await using var context = db.CreateContext();
        context.LexiconEntries.Add(LexiconEntry.Create(
            senseId, text, text, pos, definitionEn, meaningAr,
            CefrLevel.A1, rank, "en=wordos-test;ar=wordos-test",
            DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();

        return senseId;
    }
}
