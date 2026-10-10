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
    public async Task A_meaning_picked_from_the_dictionary_is_never_put_to_the_model()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // A lexicon gloss is curated, and picked it is the dictionary's in
        // every respect (ADR-129) — a call here would spend the learner's
        // money asking the model whether the dictionary is right.
        var senseId = await SeedAsync("quill", "n", "a pen", "ريشة كتابة");
        await AddAsync(new { senseId });

        Assert.Equal(0, Ai.MeaningChecks);
    }

    [SkippableFact]
    public async Task A_word_from_a_passage_costs_one_call_to_describe_it()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // The passage's meaning is the AI's, so is its description (ADR-130)
        // — asked once, when the learner adds it, rather than of every word
        // every passage glosses.
        await SeedAsync("quill", "n", "a pen", "ريشة كتابة");
        var sessionId = await SeedSessionWithGlossaryAsync(
            [("quill", "قلم ريشة", "noun")]);
        await AddAsync(new { text = "quill", fromSessionId = sessionId });

        Assert.Equal(1, Ai.MeaningChecks);
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
    public async Task Insisting_on_a_rejected_meaning_saves_nothing()
    {
        // ADR-112: there is no "add it anyway". An app that still sends the
        // old flag is refused exactly as one that does not.
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        await SeedAsync("bellows", "n", "a device for blowing air", "منفاخ");
        Ai.RejectMeanings = true;

        var response = await Client.PostAsJsonAsync("/api/words", new
        {
            text = "bellows",
            customMeaning = "قطة",
            acceptAnyway = true,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error");
        Assert.Equal("MEANING_REJECTED", error.GetProperty("code").GetString());
        // What to do instead: the meanings the checker would accept.
        Assert.NotEmpty(error.GetProperty("suggestions").EnumerateArray());

        await using var context = db.CreateContext();
        Assert.False(await context.Words.AnyAsync(w => w.Text == "bellows"));
    }

    [SkippableFact]
    public async Task A_meaning_the_checker_accepts_is_saved_as_written()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        await SeedAsync("bellows", "n", "a device for blowing air", "منفاخ");

        var body = await AddAndReadAsync(new
        {
            text = "bellows",
            customMeaning = "منفاخ الحداد",
        });

        // Their wording, untouched — the checker does not get to rewrite it.
        Assert.Equal("منفاخ الحداد", body.GetProperty("meaning").GetString());

        await using var context = db.CreateContext();
        var word = await context.Words.FirstAsync(w => w.Text == "bellows");
        Assert.Equal(MeaningCheckResult.Approved, word.MeaningCheck);
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

    // ── Changing a meaning (ADR-101) ──────────────────────────────────────

    [SkippableFact]
    public async Task Another_wording_of_the_same_word_is_simply_accepted()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = await SeedAsync(
            "create", "v", "bring into existence", "أنشأ");
        await SeedAsync("create", "v", "make or produce", "يصنع", rank: 2);

        var wordId = await AddAsync(new { senseId });

        var changed = await ChangeAsync(wordId, new { meaning = "يصنع" });
        changed.EnsureSuccessStatusCode();

        var word = await changed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("يصنع", word.GetProperty("meaning").GetString());
        Assert.Equal("create", word.GetProperty("text").GetString());

        // The English definition follows the meaning. Leaving the old sense id
        // behind would describe the meaning the learner has just rejected.
        Assert.Equal(
            "make or produce", word.GetProperty("definitionEn").GetString());
    }

    [SkippableFact]
    public async Task Changing_the_meaning_costs_the_learner_nothing()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = await SeedAsync(
            "create", "v", "bring into existence", "أنشأ");
        await SeedAsync("create", "v", "make or produce", "يصنع", rank: 2);
        var wordId = await AddAsync(new { senseId });

        JourneySnapshot before;
        await using (var context = db.CreateContext())
        {
            before = await SnapshotAsync(context, wordId);
        }

        (await ChangeAsync(wordId, new { meaning = "يصنع" }))
            .EnsureSuccessStatusCode();

        // Same word, same queue, same position — the whole promise the feature
        // makes. Only the meaning and the sense behind it moved.
        await using (var context = db.CreateContext())
        {
            Assert.Equal(before, await SnapshotAsync(context, wordId));
        }
    }

    [SkippableFact]
    public async Task A_meaning_that_belongs_to_another_word_is_refused_by_name()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = await SeedAsync(
            "create", "v", "bring into existence", "أنشأ");
        var bookSense = await SeedAsync(
            "book", "v", "reserve in advance", "يحجز");

        var wordId = await AddAsync(new { senseId });

        // The checker is asked first now (ADR-130), and `create` does not
        // mean يحجز. Only then is the word it does belong to named.
        Ai.RejectMeanings = true;
        var response = await ChangeAsync(wordId, new { meaning = "يحجز" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var error = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error");

        Assert.Equal("MEANING_IS_ANOTHER_WORD", error.GetProperty("code").GetString());

        // Naming the word is the point: the learner was not wrong, they were on
        // a different word — and the offer to swap needs the sense id.
        var candidate = error.GetProperty("candidates").EnumerateArray().First();
        Assert.Equal("book", candidate.GetProperty("text").GetString());
        Assert.Equal(bookSense, candidate.GetProperty("senseId").GetString());

        // And nothing was written: the word still means what it did.
        var unchanged = await Client.GetFromJsonAsync<JsonElement>(
            $"/api/words/{wordId}");
        Assert.Equal("أنشأ", unchanged.GetProperty("meaning").GetString());
    }

    [SkippableFact]
    public async Task Insisting_cannot_turn_one_word_into_another()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = await SeedAsync(
            "create", "v", "bring into existence", "أنشأ");
        await SeedAsync("book", "v", "reserve in advance", "يحجز");
        var wordId = await AddAsync(new { senseId });

        // The override exists for a checker that could not recognise a wording
        // (ADR-074). It has no business here: the dictionary knows whose
        // meaning this is, and insisting would silently make `create` mean
        // `book`.
        Ai.RejectMeanings = true;
        var response = await ChangeAsync(
            wordId, new { meaning = "يحجز", acceptAnyway = true });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [SkippableFact]
    public async Task Swapping_for_the_other_word_starts_that_one_from_nothing()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = await SeedAsync(
            "create", "v", "bring into existence", "أنشأ");
        var bookSense = await SeedAsync(
            "book", "v", "reserve in advance", "يحجز");

        var wordId = await AddAsync(new { senseId });

        var response = await ChangeAsync(wordId, new
        {
            meaning = "يحجز",
            replaceWithSenseId = bookSense,
        });
        response.EnsureSuccessStatusCode();

        var replacement = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("book", replacement.GetProperty("text").GetString());
        Assert.Equal("يحجز", replacement.GetProperty("meaning").GetString());

        // A different word, so it has been tested on nothing: Reading, no
        // passes, no attempts. That is not a penalty, it is the truth.
        Assert.Equal("READING", replacement.GetProperty("currentSkill").GetString());
        Assert.All(
            replacement.GetProperty("skills").EnumerateArray(),
            skill =>
            {
                Assert.Equal(0, skill.GetProperty("attempts").GetInt32());
                Assert.Equal(
                    JsonValueKind.Null,
                    skill.GetProperty("passedAt").ValueKind);
            });

        // One of them, never both: the learner agreed to a swap.
        var list = await Client.GetFromJsonAsync<JsonElement>("/api/words");
        Assert.Equal(1, list.GetProperty("total").GetInt32());

        var old = await Client.GetAsync($"/api/words/{wordId}");
        Assert.Equal(HttpStatusCode.NotFound, old.StatusCode);
    }

    [SkippableFact]
    public async Task A_wording_the_dictionary_lacks_is_put_to_the_checker()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = await SeedAsync(
            "create", "v", "bring into existence", "أنشأ");
        var wordId = await AddAsync(new { senseId });

        // Nothing in the lexicon says this, so the dictionary cannot name
        // another owner for it — only the checker can judge the pairing.
        Ai.RejectMeanings = true;
        var refused = await ChangeAsync(wordId, new { meaning = "يُكَوِّن" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var error = (await refused.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error");
        Assert.Equal("MEANING_REJECTED", error.GetProperty("code").GetString());

        // And insisting changes nothing (ADR-112): the word keeps the meaning
        // it had, which the learner can see is still correct.
        var insisted = await ChangeAsync(
            wordId, new { meaning = "يُكَوِّن", acceptAnyway = true });
        Assert.Equal(HttpStatusCode.Conflict, insisted.StatusCode);

        await using var context = db.CreateContext();
        var stored = await context.Words.SingleAsync(w => w.Id == wordId);
        Assert.Equal("أنشأ", stored.Meaning);
    }

    [SkippableFact]
    public async Task Another_learners_word_is_not_editable()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = await SeedAsync(
            "create", "v", "bring into existence", "أنشأ");
        await SeedAsync("create", "v", "make or produce", "يصنع", rank: 2);
        var wordId = await AddAsync(new { senseId });

        await SignInAsync();
        var response = await ChangeAsync(wordId, new { meaning = "يصنع" });

        // 404, not 403: the id names nothing as far as this learner is
        // concerned (docs/07-SECURITY.md §4).
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task Saving_the_meaning_it_already_has_is_not_a_change()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = await SeedAsync(
            "create", "v", "bring into existence", "أنشأ");
        var wordId = await AddAsync(new { senseId });

        var before = Ai.MeaningChecks;
        (await ChangeAsync(wordId, new { meaning = "أنشأ" }))
            .EnsureSuccessStatusCode();

        // Nothing asked, and nothing written: the learner opened the field and
        // pressed save.
        Assert.Equal(before, Ai.MeaningChecks);

        await using var context = db.CreateContext();
        var events = await context.WordEvents
            .Where(e => e.WordId == wordId
                        && e.Type == WordEventType.MeaningChanged)
            .CountAsync();
        Assert.Equal(0, events);
    }

    // ── The definition belongs to the learner's sense (ADR-105) ───────────
    //
    // Reported by a student: they added `habit` with their own meaning, عادة,
    // and a later skill taught it as رداء. It was not the model inventing
    // anything. The word had been stored with the Arabic they wrote beside the
    // English definition of the lexicon's *commonest* sense — "attire worn by
    // a member of a religious order" — and every generator reads the
    // definition. Found on production for eleven words, `sausage = نقانق`
    // beside "a small airship" among them.

    /// <summary>A word no other test seeds, letters only.</summary>
    private static string FreshWord() =>
        "hab" + new string(Guid.NewGuid().ToString("N")
            .Where(char.IsAsciiLetter).Take(8).ToArray());

    /// <summary>Two senses, the commonest being the one nobody means.</summary>
    private async Task<(string Word, string RobeSense, string CustomSense)>
        SeedHabitLikeAsync()
    {
        var word = FreshWord();
        var robe = await SeedAsync(word, "n",
            "a distinctive attire worn by a member of a religious order",
            "ثوب رهباني", rank: 0);
        var custom = await SeedAsync(word, "n", "an established custom",
            "ديدن", rank: 1);
        return (word, robe, custom);
    }

    [SkippableFact]
    public async Task A_learners_own_meaning_is_stored_beside_the_sense_it_names()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var (word, _, _) = await SeedHabitLikeAsync();

        // The checker says the learner meant the second listed sense.
        Ai.KnownWordSense = 2;

        var body = await AddAndReadAsync(new { text = word, customMeaning = "عادة" });

        Assert.Equal("عادة", body.GetProperty("meaning").GetString());
        Assert.Equal("an established custom",
            body.GetProperty("definitionEn").GetString());

        // The number means the same row on both sides: the checker was shown
        // this sense second.
        Assert.EndsWith("an established custom", Ai.LastCheckDefinitions[1]);
    }

    [SkippableFact]
    public async Task The_commonest_senses_definition_is_never_stored_for_another_meaning()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var (word, _, _) = await SeedHabitLikeAsync();

        // The checker accepted the meaning but named no sense and wrote no
        // definition. The old answer here was the robe.
        var body = await AddAndReadAsync(new { text = word, customMeaning = "عادة" });

        Assert.DoesNotContain("attire",
            body.GetProperty("definitionEn").GetString());
    }

    [SkippableFact]
    public async Task When_no_listed_sense_fits_the_checkers_definition_is_stored()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var (word, _, _) = await SeedHabitLikeAsync();

        Ai.KnownWordDefinition = "something a person does regularly";

        var body = await AddAndReadAsync(new { text = word, customMeaning = "عادة" });

        Assert.Equal("something a person does regularly",
            body.GetProperty("definitionEn").GetString());
    }

    [SkippableFact]
    public async Task A_sense_number_that_points_nowhere_is_not_trusted()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var (word, _, _) = await SeedHabitLikeAsync();

        Ai.KnownWordSense = 7; // two senses were listed

        var body = await AddAndReadAsync(new { text = word, customMeaning = "عادة" });

        Assert.DoesNotContain("attire",
            body.GetProperty("definitionEn").GetString());
    }

    [SkippableFact]
    public async Task Arabic_that_is_a_senses_own_is_matched_without_asking()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var (word, _, _) = await SeedHabitLikeAsync();

        // Typed rather than picked, and exactly the second sense's Arabic.
        // The stub names no sense: this is decided here, not by the model.
        var body = await AddAndReadAsync(new { text = word, customMeaning = "ديدن" });

        Assert.Equal("an established custom",
            body.GetProperty("definitionEn").GetString());
    }

    [SkippableFact]
    public async Task Rewriting_a_meaning_in_ones_own_words_replaces_the_definition()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var (_, robe, _) = await SeedHabitLikeAsync();

        // Picked the robe from the list, as two production learners did.
        var id = await AddAsync(new { senseId = robe });

        Ai.KnownWordSense = 2;
        var response = await ChangeAsync(id, new { meaning = "عادة" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Before ADR-105 the meaning changed and the robe definition stayed,
        // so the word went on being taught as a robe.
        Assert.Equal("عادة", body.GetProperty("meaning").GetString());
        Assert.Equal("an established custom",
            body.GetProperty("definitionEn").GetString());
    }

    // ── ADR-129: a meaning the learner wrote is described by the checker ──
    //
    // Reported by a student: `associated = مرتبط` was stored as a verb. The
    // lexicon holds `associated` only as the past forms of `associate`, the
    // checker was never asked what kind of word the learner meant, and the
    // part of speech came from the lexicon. The product owner's rule: picked
    // from the dictionary, the dictionary's; written by the learner, the AI's.

    /// <summary>A word the lexicon knows only as a verb's past forms.</summary>
    private async Task<(string Word, string PastTense)> SeedAssociatedLikeAsync()
    {
        var word = FreshWord();
        var past = await SeedAsync(word, "v",
            "past tense of \"associate\" — make a logical or causal connection",
            "رَبَطَ (الماضي)", rank: 0);
        await SeedAsync(word, "v",
            "past participle of \"associate\" — make a logical or causal connection",
            "رَبَطَ (التصريف الثالث)", rank: 1);
        return (word, past);
    }

    [SkippableFact]
    public async Task A_meaning_the_learner_wrote_takes_the_checkers_description()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var (word, _) = await SeedAssociatedLikeAsync();

        // The closest listed sense is the participle, and the checker names
        // it — but describes what the learner actually wrote.
        Ai.KnownWordSense = 2;
        Ai.KnownWordPartOfSpeech = "adjective";
        Ai.KnownWordDefinition = "connected with something else";
        Ai.KnownWordLevel = "B2";

        var body = await AddAndReadAsync(new { text = word, customMeaning = "مرتبط" });

        Assert.Equal("مرتبط", body.GetProperty("meaning").GetString());
        Assert.Equal("adjective", body.GetProperty("partOfSpeech").GetString());
        Assert.Equal("connected with something else",
            body.GetProperty("definitionEn").GetString());
        Assert.Equal("B2", body.GetProperty("cefrLevel").GetString());
    }

    [SkippableFact]
    public async Task A_meaning_picked_from_the_dictionary_is_the_dictionarys()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var (_, past) = await SeedAssociatedLikeAsync();

        // Whatever the checker would say, it is not asked.
        Ai.KnownWordPartOfSpeech = "adjective";
        Ai.KnownWordDefinition = "connected with something else";
        Ai.KnownWordLevel = "C2";
        var before = Ai.MeaningChecks;

        var body = await AddAndReadAsync(new { senseId = past });

        Assert.Equal(before, Ai.MeaningChecks);
        Assert.Equal("v", body.GetProperty("partOfSpeech").GetString());
        Assert.StartsWith("past tense of",
            body.GetProperty("definitionEn").GetString());
        Assert.Equal("A1", body.GetProperty("cefrLevel").GetString());
    }

    [SkippableFact]
    public async Task What_the_checker_leaves_out_the_dictionary_fills()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var (word, _, _) = await SeedHabitLikeAsync();

        // A checker that named the sense and nothing else.
        Ai.KnownWordSense = 2;

        var body = await AddAndReadAsync(new { text = word, customMeaning = "عادة" });

        Assert.Equal("n", body.GetProperty("partOfSpeech").GetString());
        Assert.Equal("an established custom",
            body.GetProperty("definitionEn").GetString());
        Assert.Equal("A1", body.GetProperty("cefrLevel").GetString());
    }

    [SkippableFact]
    public async Task A_part_of_speech_or_band_this_app_cannot_use_is_dropped()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var (word, _) = await SeedAssociatedLikeAsync();

        // The model reports and the backend decides (rule R2).
        Ai.KnownWordPartOfSpeech = "participle";
        Ai.KnownWordLevel = "Z9";
        Ai.KnownWordDefinition = "connected with something else";

        var body = await AddAndReadAsync(new { text = word, customMeaning = "مرتبط" });

        Assert.Equal("v", body.GetProperty("partOfSpeech").GetString());
        Assert.Equal("A1", body.GetProperty("cefrLevel").GetString());
        Assert.Equal("connected with something else",
            body.GetProperty("definitionEn").GetString());
    }

    [SkippableFact]
    public async Task A_rejected_meaning_stores_nothing_whatever_else_the_checker_said()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var (word, _) = await SeedAssociatedLikeAsync();

        Ai.RejectMeanings = true;
        Ai.KnownWordPartOfSpeech = "adjective";

        var response = await Client.PostAsJsonAsync("/api/words",
            new { text = word, customMeaning = "سيارة" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error");
        Assert.Equal("MEANING_REJECTED", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("suggestions").GetArrayLength() > 0);

        await using var context = db.CreateContext();
        Assert.False(await context.Words.AnyAsync(w => w.Text == word));
    }

    [SkippableFact]
    public async Task Rewriting_a_meaning_takes_the_checkers_description()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var (word, _) = await SeedAssociatedLikeAsync();

        Ai.KnownWordPartOfSpeech = "verb";
        Ai.KnownWordDefinition = "connect in the mind";
        var id = await AddAsync(new { text = word, customMeaning = "ربط" });

        Ai.KnownWordPartOfSpeech = "adjective";
        Ai.KnownWordDefinition = "connected with something else";
        var response = await ChangeAsync(id, new { meaning = "مرتبط" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("adjective", body.GetProperty("partOfSpeech").GetString());
        Assert.Equal("connected with something else",
            body.GetProperty("definitionEn").GetString());
    }

    // ── ADR-130: editing asks the checker first; a passage word is described ──

    [SkippableFact]
    public async Task A_wording_the_dictionary_files_under_another_word_is_put_to_the_checker()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var (word, _) = await SeedAssociatedLikeAsync();

        // The dictionary files مرتبط under a rare word. It used to refuse the
        // edit by naming it — "مرتبط is the meaning of relatum" — without
        // asking anyone whether it is also the meaning of this word.
        await SeedAsync("relat" + FreshWord(), "n", "a thing related", "مرتبط");

        Ai.KnownWordPartOfSpeech = "verb";
        var id = await AddAsync(new { text = word, customMeaning = "ربط" });

        Ai.KnownWordPartOfSpeech = "adjective";
        Ai.KnownWordDefinition = "connected with something else";
        var response = await ChangeAsync(id, new { meaning = "مرتبط" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("مرتبط", body.GetProperty("meaning").GetString());
        Assert.Equal("adjective", body.GetProperty("partOfSpeech").GetString());
    }

    [SkippableFact]
    public async Task A_refused_wording_with_no_other_owner_is_simply_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var (word, _) = await SeedAssociatedLikeAsync();
        var id = await AddAsync(new { text = word, customMeaning = "ربط" });

        Ai.RejectMeanings = true;
        var response = await ChangeAsync(id, new { meaning = "كلام فارغ تماما" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error");
        Assert.Equal("MEANING_REJECTED", error.GetProperty("code").GetString());
    }

    [SkippableFact]
    public async Task Rewriting_a_meaning_moves_its_band_and_nothing_scheduled()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();
        var (word, _) = await SeedAssociatedLikeAsync();

        Ai.KnownWordLevel = "A2";
        var id = await AddAsync(new { text = word, customMeaning = "ربط" });

        await using var before = db.CreateContext();
        var journey = await SnapshotAsync(before, id);

        Ai.KnownWordLevel = "C1";
        var response = await ChangeAsync(id, new { meaning = "مرتبط" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("C1", body.GetProperty("cefrLevel").GetString());

        // The band describes the meaning; the journey is untouched (ADR-101).
        await using var after = db.CreateContext();
        Assert.Equal(journey, await SnapshotAsync(after, id));
    }

    [SkippableFact]
    public async Task A_passage_word_is_described_by_the_checker_not_the_commonest_sense()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // Production: `hidden = مخفي` stored beside "past participle of hide".
        var word = FreshWord();
        await SeedAsync(word, "v", "past participle of \"hide\" — conceal", "أخفى", rank: 0);
        var sessionId = await SeedSessionWithGlossaryAsync([(word, "مخفي", "adjective")]);

        Ai.KnownWordDefinition = "kept out of sight";
        Ai.KnownWordLevel = "B1";
        Ai.KnownWordPartOfSpeech = "adjective";

        var body = await AddAndReadAsync(new { text = word, fromSessionId = sessionId });

        Assert.Equal("مخفي", body.GetProperty("meaning").GetString());
        Assert.Equal("adjective", body.GetProperty("partOfSpeech").GetString());
        Assert.Equal("kept out of sight", body.GetProperty("definitionEn").GetString());
        Assert.Equal("B1", body.GetProperty("cefrLevel").GetString());
    }

    [SkippableFact]
    public async Task A_passage_words_part_of_speech_agrees_with_its_definition()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // Found running it: the glossary gave `waiting` the role "verb" in its
        // sentence and the meaning انتظار, a noun. The type now comes from the
        // same answer as the definition.
        var word = FreshWord();
        await SeedAsync(word, "v", "-ing form of \"wait\" — stay", "انتظر", rank: 0);
        var sessionId = await SeedSessionWithGlossaryAsync([(word, "انتظار", "verb")]);

        Ai.KnownWordPartOfSpeech = "noun";
        Ai.KnownWordDefinition = "the act of staying until something happens";

        var body = await AddAndReadAsync(new { text = word, fromSessionId = sessionId });

        Assert.Equal("noun", body.GetProperty("partOfSpeech").GetString());
    }

    [SkippableFact]
    public async Task A_passage_word_the_checker_gives_no_type_keeps_the_glossarys()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var word = FreshWord();
        await SeedAsync(word, "n", "a thing", "شيء", rank: 0);
        var sessionId = await SeedSessionWithGlossaryAsync([(word, "سوف", "auxiliary")]);

        var body = await AddAndReadAsync(new { text = word, fromSessionId = sessionId });

        Assert.Equal("auxiliary", body.GetProperty("partOfSpeech").GetString());
    }

    [SkippableFact]
    public async Task A_passage_word_is_added_when_the_checker_is_down_without_a_wrong_definition()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var word = FreshWord();
        await SeedAsync(word, "v", "past participle of \"hide\" — conceal", "أخفى", rank: 0);
        var sessionId = await SeedSessionWithGlossaryAsync([(word, "مخفي", "adjective")]);

        Ai.Fail = true;
        var body = await AddAndReadAsync(new { text = word, fromSessionId = sessionId });

        // The passage's meaning stands; the description waits for another
        // day rather than borrowing the commonest sense's.
        Assert.Equal("مخفي", body.GetProperty("meaning").GetString());
        Assert.Equal("", body.GetProperty("definitionEn").GetString());
        Assert.Equal("A1", body.GetProperty("cefrLevel").GetString());
    }

    [SkippableFact]
    public async Task A_passage_meaning_the_checker_disputes_is_still_the_passages()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        await SeedAsync("bank", "n", "a financial institution", "مصرف", rank: 0);
        var sessionId = await SeedSessionWithGlossaryAsync(
            [("bank", "ضفة النهر", "noun")]);

        // It describes; it does not judge. The learner read this meaning.
        Ai.RejectMeanings = true;
        var body = await AddAndReadAsync(new { text = "bank", fromSessionId = sessionId });

        Assert.Equal("ضفة النهر", body.GetProperty("meaning").GetString());
        Assert.DoesNotContain("financial", body.GetProperty("definitionEn").GetString());
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private Task<HttpResponseMessage> ChangeAsync(Guid wordId, object body) =>
        Client.PatchAsJsonAsync($"/api/words/{wordId}/meaning", body);

    /// <summary>Everything a meaning change must leave exactly as it was.</summary>
    private sealed record JourneySnapshot(
        WordState State,
        SkillType? CurrentSkill,
        DateTimeOffset AddedAt,
        int ExposureCount,
        string Skills);

    private static async Task<JourneySnapshot> SnapshotAsync(
        Infrastructure.Persistence.WordOsDbContext context, Guid wordId)
    {
        var word = await context.Words
            .Include(w => w.Skills)
            .SingleAsync(w => w.Id == wordId);

        return new JourneySnapshot(
            word.State,
            word.CurrentSkill,
            word.AddedAt,
            word.ExposureCount,
            string.Join(
                ";",
                word.Skills
                    .OrderBy(x => x.Skill)
                    .Select(x =>
                        $"{x.Skill}:{x.Status}:{x.Attempts}:"
                        + $"{x.AvailableAt:O}:{x.PassedAt:O}")));
    }



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
