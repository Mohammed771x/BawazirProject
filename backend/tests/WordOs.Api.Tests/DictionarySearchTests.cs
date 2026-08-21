using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using WordOs.Domain.Common;
using WordOs.Domain.Lexicon;

namespace WordOs.Api.Tests;

/// <summary>
/// Finding a word to add: in English, in Arabic, and in whatever form the
/// learner happens to know it.
/// </summary>
/// <remarks>
/// The rule these serve is the one the product states plainly — any real
/// English word must be findable and addable. Three things stood between a
/// learner and that: the search was a prefix match only, so <c>went</c> found
/// nothing; it was English-only, so an Arabic speaker had to already know the
/// English word they were looking for; and it refused single letters, so
/// <c>a</c> and <c>I</c> were unreachable (ADR-033, ADR-034).
/// </remarks>
[Collection(PostgresCollection.Name)]
public class DictionarySearchTests(PostgresFixture db) : IAsyncLifetime
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

    [SkippableFact]
    public async Task A_closed_class_word_is_searchable_and_addable()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // Shaped exactly like the rows the importer authors: WordNet has no
        // auxiliaries, so without these a learner could not add "is" at all.
        var senseId = await SeedAsync(
            "is", "aux", "the form of \"be\" used with he, she or it",
            "يكون (للمفرد الغائب)", lemma: "be", rank: -1);

        var results = await LookupAsync("is");

        Assert.Contains(results, r =>
            r.GetProperty("senseId").GetString() == senseId);

        // And it goes into the pipeline like any other word — nothing filters
        // by part of speech on the way in.
        var added = await Client.PostAsJsonAsync("/api/words", new { senseId });
        added.EnsureSuccessStatusCode();
    }

    [SkippableFact]
    public async Task A_homograph_that_is_a_function_word_comes_first()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var word = $"ar{Guid.NewGuid():N}"[..6];

        // "are" exists in WordNet only as a unit of area. A learner typing it
        // means the verb, so the authored row has to outrank the noun.
        await SeedAsync(word, "n", "a unit of area", "آر", rank: 0);
        var auxiliary = await SeedAsync(
            word, "aux", "the form of \"be\" used with you, we and they",
            "تكون", lemma: "be", rank: -1);

        var results = await LookupAsync(word);

        Assert.Equal(auxiliary, results[0].GetProperty("senseId").GetString());
    }

    [SkippableFact]
    public async Task An_irregular_form_finds_the_word_it_belongs_to()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = await SeedAsync(
            "go", "v", "to move from one place to another", "يذهب");

        // No rule turns "went" into "go"; irregulars have to be known. They are
        // also the commonest verbs in the language, so a learner meets them
        // long before the regular ones.
        var results = await LookupAsync("went");

        Assert.Contains(results, r =>
            r.GetProperty("senseId").GetString() == senseId);
    }

    [SkippableTheory]
    [InlineData("children", "child")]
    [InlineData("studies", "study")]
    [InlineData("running", "run")]
    public async Task A_word_as_the_learner_met_it_still_resolves(
        string typed, string headword)
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = await SeedAsync(headword, "n", $"a {headword}", "معنى");

        var results = await LookupAsync(typed);

        Assert.Contains(results, r =>
            r.GetProperty("senseId").GetString() == senseId);
    }

    [SkippableFact]
    public async Task A_single_letter_is_matched_as_a_word_not_as_a_prefix()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var article = await SeedAsync(
            "a", "det", "one, when the thing is not a particular one",
            "أداة نكرة", rank: -1);
        var longer = await SeedAsync(
            $"a{Guid.NewGuid():N}"[..8], "n", "a long word", "كلمة طويلة");

        var results = await LookupAsync("a");
        var ids = results.Select(r => r.GetProperty("senseId").GetString()).ToList();

        Assert.Contains(article, ids);

        // The reason the minimum length existed in the first place: one letter
        // must not return everything that starts with it
        // (docs/07-SECURITY.md §6).
        Assert.DoesNotContain(longer, ids);
    }

    // ── Arabic in, English out ───────────────────────────────────────────────

    [SkippableFact]
    public async Task Typing_the_meaning_in_arabic_finds_the_english_word()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var senseId = await SeedAsync(
            $"go{Guid.NewGuid():N}"[..6], "v", "to move from one place to another",
            "يذهب");

        var results = await LookupAsync("يذهب");

        Assert.Contains(results, r =>
            r.GetProperty("senseId").GetString() == senseId);
    }

    [SkippableFact]
    public async Task A_vocalised_gloss_is_found_without_the_diacritics()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        // Arabic WordNet vocalises much of its data and nobody types the
        // marks, so both sides are folded to one plain form before comparing.
        var senseId = await SeedAsync(
            $"alter{Guid.NewGuid():N}"[..8], "v", "to change something",
            "عَدَّلَ");

        var results = await LookupAsync("عدل");

        Assert.Contains(results, r =>
            r.GetProperty("senseId").GetString() == senseId);
    }

    [SkippableFact]
    public async Task An_exact_arabic_meaning_outranks_one_that_merely_contains_it()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        var ns = $"{Guid.NewGuid():N}"[..6];
        var phrase = await SeedAsync($"{ns}gold", "n", "gold of 18 carats",
            $"ذهب عيار 18 {ns}", rank: 5);
        var exact = await SeedAsync($"{ns}go", "v", "to move away",
            "ذهب", rank: 5);

        var results = await LookupAsync("ذهب");
        var ids = results.Select(r => r.GetProperty("senseId").GetString()).ToList();

        // Otherwise a learner searching for a common verb is handed a list of
        // compounds that happen to mention it.
        Assert.True(ids.IndexOf(exact) < ids.IndexOf(phrase),
            $"expected the exact meaning first, got {string.Join(", ", ids.Take(5))}");
    }

    [SkippableFact]
    public async Task A_single_arabic_letter_returns_nothing()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SignInAsync();

        await SeedAsync($"w{Guid.NewGuid():N}"[..6], "n", "a word", "كلمة");

        // The Arabic side is a substring match, which makes it the easier of
        // the two to walk; one letter would match a large part of the lexicon.
        var results = await LookupAsync("ك");

        Assert.Empty(results);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task SignInAsync()
    {
        var response = await Client.PostAsJsonAsync("/api/auth/register", new
        {
            email = $"dict-{Guid.NewGuid():N}@test.dev",
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
        string? lemma = null,
        int? rank = 1)
    {
        var senseId = $"dict-{Guid.NewGuid():N}";

        await using var context = db.CreateContext();
        context.LexiconEntries.Add(LexiconEntry.Create(
            senseId, text, lemma ?? text, pos, definitionEn, meaningAr,
            CefrLevel.A1, rank, "en=wordos-test;ar=wordos-test",
            DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();

        return senseId;
    }

    private async Task<List<JsonElement>> LookupAsync(string query)
    {
        var response = await Client.GetFromJsonAsync<JsonElement>(
            "/api/words/lookup?q=" + Uri.EscapeDataString(query));

        return response.EnumerateArray().ToList();
    }
}
