using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using Xunit.Abstractions;

namespace WordOs.Api.Tests.Qa;

/// <summary>
/// QA prompt 01: the journeys the existing suites leave open. These cover the
/// <c>alreadyAdded</c> matrix (ADR-136) against the real add path, and My Words
/// paging at 0/1/20/21/50/155 rows.
/// </summary>
/// <remarks>
/// Where the right answer is a product decision nobody has made yet, the test
/// records what the server does and says so (F04, F08). It does not pick an
/// expectation that happens to suit the code.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class FunctionalQaTests(PostgresFixture db, ITestOutputHelper output) : QaApiBase(db)
{
    private static string Unique(string stem) => $"{stem}{Guid.NewGuid():N}"[..(stem.Length + 6)];

    private async Task<string> SeedAsync(string text, string pos, string meaning)
    {
        var senseId = $"qa-{Guid.NewGuid():N}";
        await using var context = Db.CreateContext();
        context.LexiconEntries.Add(WordOs.Domain.Lexicon.LexiconEntry.Create(
            senseId, text, text, pos, $"def of {text} ({pos})", meaning,
            CefrLevel.A2, 1, "en=qa;ar=qa", DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();
        return senseId;
    }

    private static async Task<Dictionary<string, bool>> FlagsAsync(Learner l, string q)
    {
        var rows = await l.Client.GetFromJsonAsync<JsonElement>(
            "/api/words/lookup?q=" + Uri.EscapeDataString(q));
        return rows.EnumerateArray().ToDictionary(
            r => r.GetProperty("senseId").GetString()!,
            r => r.GetProperty("alreadyAdded").GetBoolean());
    }

    private async Task<int> WordCountAsync(Guid userId, string text)
    {
        await using var c = Db.CreateContext();
        return await c.Words.IgnoreQueryFilters()
            .CountAsync(w => w.UserId == userId && w.Text == text && w.State != WordState.Deleted);
    }

    [SkippableFact]
    public async Task F01_the_mark_belongs_to_the_learner_who_owns_the_sense()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var b = await RegisterAsync();
        var w = Unique("fa");
        var s = await SeedAsync(w, "n", "شيء");

        (await a.Client.PostAsJsonAsync("/api/words", new { senseId = s })).EnsureSuccessStatusCode();

        Assert.True((await FlagsAsync(a, w))[s]);
        Assert.False((await FlagsAsync(b, w))[s]);
    }

    [SkippableFact]
    public async Task F02_a_flagged_sense_sent_directly_is_refused_by_the_server()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var w = Unique("fb");
        var s = await SeedAsync(w, "n", "قلم");
        (await a.Client.PostAsJsonAsync("/api/words", new { senseId = s })).EnsureSuccessStatusCode();

        var again = await a.Client.PostAsJsonAsync("/api/words", new { senseId = s });
        output.WriteLine($"direct re-add: {(int)again.StatusCode} {await again.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("WORD_ALREADY_ADDED", await again.Content.ReadAsStringAsync());
        Assert.Equal(1, await WordCountAsync(a.Id, w));
    }

    [SkippableFact]
    public async Task F03_an_archived_word_is_still_marked()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var w = Unique("fc");
        var s = await SeedAsync(w, "n", "باب");
        var added = await JsonAsync(await a.Client.PostAsJsonAsync("/api/words", new { senseId = s }));
        var id = added.GetProperty("id").GetGuid();
        await using (var c = Db.CreateContext())
            await c.Database.ExecuteSqlAsync($"""UPDATE words SET "State" = 'Archived' WHERE "Id" = {id}""");

        Assert.True((await FlagsAsync(a, w))[s]);
        var again = await a.Client.PostAsJsonAsync("/api/words", new { senseId = s });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    /// <summary>
    /// The learner rewrites the meaning in their own words. The word keeps the
    /// sense id it was added with (Word.ChangeMeaning keeps it unless the new
    /// wording is a dictionary meaning of this word), so search goes on
    /// marking the <i>old</i> dictionary meaning as theirs. Recorded, not
    /// asserted either way: whether an edited word still "is" its old sense
    /// is a product decision (see report 01, O01-1).
    /// </summary>
    [SkippableFact]
    public async Task F04_after_a_free_wording_edit_the_old_sense_stays_marked()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var w = Unique("fd");
        var s1 = await SeedAsync(w, "n", "كتاب");
        var s2 = await SeedAsync(w, "v", "يحجز");
        var id = (await JsonAsync(await a.Client.PostAsJsonAsync("/api/words", new { senseId = s1 })))
            .GetProperty("id").GetGuid();

        // The checker's description of the new meaning, as the real one gives.
        Ai.KnownWordDefinition = "to arrange to have a seat";
        Ai.KnownWordPartOfSpeech = "verb";
        Ai.KnownWordLevel = "A2";
        var edit = await a.Client.PatchAsJsonAsync($"/api/words/{id}/meaning", new { meaning = "يحجز مقعدًا" });
        output.WriteLine($"edit: {(int)edit.StatusCode} {await edit.Content.ReadAsStringAsync()}");
        edit.EnsureSuccessStatusCode();

        var flags = await FlagsAsync(a, w);
        var readd = await a.Client.PostAsJsonAsync("/api/words", new { senseId = s1 });
        var addOther = await a.Client.PostAsJsonAsync("/api/words", new { senseId = s2 });
        output.WriteLine($"after edit: old sense marked={flags[s1]}, other sense marked={flags[s2]}; " +
                         $"add old sense -> {(int)readd.StatusCode}; add other sense -> {(int)addOther.StatusCode}");

        // What the server does today, pinned so a change is noticed.
        Assert.True(flags[s1]);
        Assert.False(flags[s2]);
        Assert.Equal(HttpStatusCode.Conflict, readd.StatusCode);
    }

    [SkippableFact]
    public async Task F05_a_written_meaning_marks_the_dictionary_sense_with_the_same_wording()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var w = Unique("fe");
        var custom = await a.Client.PostAsJsonAsync("/api/words", new { text = w, customMeaning = "يركض بسرعة" });
        output.WriteLine($"custom add: {(int)custom.StatusCode}");
        custom.EnsureSuccessStatusCode();

        // A dictionary sense with exactly that wording, seeded after.
        var s = await SeedAsync(w, "v", "يركض بسرعة");
        var flags = await FlagsAsync(a, w);
        output.WriteLine($"dictionary sense marked={flags.GetValueOrDefault(s)}");
        Assert.True(flags[s]);
    }

    /// <summary>
    /// The case F05 marks in search, sent straight to the server: a client
    /// without the mark (1.3.2 and older) or a crafted request. ADR-136 says
    /// the mark uses "the same identity the add path refuses a duplicate on".
    /// </summary>
    [SkippableFact]
    public async Task F06_the_server_refuses_what_search_marked_as_added()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var w = Unique("ff");
        (await a.Client.PostAsJsonAsync("/api/words", new { text = w, customMeaning = "يقفز عاليًا" }))
            .EnsureSuccessStatusCode();
        var s = await SeedAsync(w, "v", "يقفز عاليًا");
        Assert.True((await FlagsAsync(a, w))[s]);

        var direct = await a.Client.PostAsJsonAsync("/api/words", new { senseId = s });
        var count = await WordCountAsync(a.Id, w);
        output.WriteLine($"direct add of the marked sense: {(int)direct.StatusCode}; live rows for the word: {count}");
        Assert.Equal(HttpStatusCode.Conflict, direct.StatusCode);
        Assert.Equal(1, count);
    }

    /// <summary>
    /// The likelihood of F06 in normal use: the dictionary already has the
    /// sense, and the learner types that same wording as their own meaning.
    /// Then adds the dictionary sense (1.3.2 has no mark to stop them).
    /// </summary>
    [SkippableFact]
    public async Task F10_typing_a_dictionary_wording_then_adding_the_sense()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var w = Unique("fg");
        var s = await SeedAsync(w, "v", "يسبح في الماء");
        var custom = await a.Client.PostAsJsonAsync("/api/words", new { text = w, customMeaning = "يسبح في الماء" });
        var customBody = await custom.Content.ReadAsStringAsync();
        var sense = await a.Client.PostAsJsonAsync("/api/words", new { senseId = s });
        var count = await WordCountAsync(a.Id, w);
        output.WriteLine($"custom add with the dictionary's wording: {(int)custom.StatusCode} {customBody[..Math.Min(160, customBody.Length)]}; " +
                         $"then the sense: {(int)sense.StatusCode}; live rows: {count}");
        custom.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, sense.StatusCode);
        Assert.Equal(1, count);
    }

    [SkippableFact]
    public async Task A_dictionary_sense_and_written_meaning_racing_create_only_one_word()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var word = Unique("fh");
        const string meaning = "يسبح في الماء";
        var senseId = await SeedAsync(word, "v", meaning);

        var results = await Task.WhenAll(
            a.Client.PostAsJsonAsync("/api/words", new { text = word, customMeaning = meaning }),
            a.Client.PostAsJsonAsync("/api/words", new { senseId }));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await WordCountAsync(a.Id, word));
    }

    [SkippableFact]
    public async Task F07_my_words_pages_cover_every_row_exactly_once_at_each_size()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var ids = new List<Guid>();
        var stem = Unique("pg");

        foreach (var target in new[] { 0, 1, 20, 21, 50, 155 })
        {
            while (ids.Count < target)
                ids.Add(await AddWordAsync(a, $"{stem}{ids.Count:D3}", $"معنى {ids.Count}"));

            var seen = await WalkAsync(a, stem);
            output.WriteLine($"{target} words -> walked {seen.Count}, distinct {seen.Distinct().Count()}");
            Assert.Equal(target, seen.Count);
            Assert.Equal(ids.OrderBy(x => x), seen.OrderBy(x => x));
        }
    }

    [SkippableFact]
    public async Task F08_deleting_deep_in_the_list_then_continuing_loses_and_repeats_nothing()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var stem = Unique("dl");
        var ids = new List<Guid>();
        for (var i = 0; i < 60; i++) ids.Add(await AddWordAsync(a, $"{stem}{i:D3}", $"كلمة {i}"));

        // Load 20 then 10 + 10 + 10 (ADR-127), as the app does.
        var loaded = new List<Guid>();
        loaded.AddRange(await PageAsync(a, stem, 0, 20));
        for (var p = 0; p < 3; p++) loaded.AddRange(await PageAsync(a, stem, loaded.Count, 10));

        // Delete two rows the learner has already scrolled past.
        var gone = new[] { loaded[5], loaded[44] };
        foreach (var g in gone) (await a.Client.DeleteAsync($"/api/words/{g}")).EnsureSuccessStatusCode();

        // The client steps back by the rows it deleted itself.
        var offset = loaded.Count - gone.Length;
        while (true)
        {
            var next = await PageAsync(a, stem, offset, 10);
            if (next.Count == 0) break;
            loaded.AddRange(next);
            offset += next.Count;
        }

        var kept = loaded.Except(gone).ToList();
        output.WriteLine($"rows seen {kept.Count}, distinct {kept.Distinct().Count()}, expected {ids.Count - gone.Length}");
        Assert.Equal(ids.Count - gone.Length, kept.Distinct().Count());
        Assert.Equal(kept.Count, kept.Distinct().Count());
    }

    [SkippableFact]
    public async Task F09_my_words_search_finds_by_english_and_by_arabic()
    {
        Skip.IfNot(Db.IsAvailable, Db.SkipReason);
        var a = await RegisterAsync();
        var stem = Unique("sr");
        var target = await AddWordAsync(a, $"{stem}lighthouse", "منارة بحرية");
        await AddWordAsync(a, $"{stem}window", "نافذة");

        var en = await a.Client.GetFromJsonAsync<JsonElement>($"/api/words?q={stem}light");
        var ar = await a.Client.GetFromJsonAsync<JsonElement>("/api/words?q=" + Uri.EscapeDataString("منارة"));
        var tooLong = await a.Client.GetAsync("/api/words?q=" + new string('x', 65));

        Assert.Equal(1, en.GetProperty("total").GetInt32());
        Assert.Equal(target, en.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Contains(ar.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == target);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    private static async Task<List<Guid>> PageAsync(Learner a, string q, int offset, int size)
    {
        var body = await a.Client.GetFromJsonAsync<JsonElement>(
            $"/api/words?q={q}&offset={offset}&pageSize={size}");
        return body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
    }

    private static async Task<List<Guid>> WalkAsync(Learner a, string q)
    {
        var all = new List<Guid>();
        var first = await a.Client.GetFromJsonAsync<JsonElement>($"/api/words?q={q}&offset=0&pageSize=20");
        all.AddRange(first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        var more = first.GetProperty("hasMore").GetBoolean();
        while (more)
        {
            var next = await a.Client.GetFromJsonAsync<JsonElement>($"/api/words?q={q}&offset={all.Count}&pageSize=10");
            all.AddRange(next.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
            more = next.GetProperty("hasMore").GetBoolean();
        }
        return all;
    }
}
