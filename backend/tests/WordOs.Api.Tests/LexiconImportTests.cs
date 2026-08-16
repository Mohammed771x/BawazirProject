using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Lexicon;

namespace WordOs.Api.Tests;

/// <summary>
/// Verifies the imported lexicon against a real PostgreSQL database.
/// </summary>
/// <remarks>
/// These run against the throwaway test database, seeded here with a small
/// hand-built fixture rather than the full 175k-row import — the point is to
/// pin the <i>rules</i> the importer must satisfy, deterministically and in
/// under a second. The real import is verified separately by
/// <see cref="ImportedLexiconTests"/>, which runs only when the development
/// database has actually been populated.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class LexiconImportTests(PostgresFixture db)
{
    private static readonly DateTimeOffset T0 =
        new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Seeds a small slice shaped exactly like the importer's output.</summary>
    private async Task<string> SeedAsync()
    {
        // Namespaced so parallel tests in the shared database cannot collide.
        var ns = $"t{Guid.NewGuid():N}"[..8];

        await using var context = db.CreateContext();
        context.LexiconEntries.AddRange(
            // Same word, same POS, genuinely different meanings → separate rows.
            LexiconEntry.Create($"{ns}-book%1:06:00::", "book", "book", "n",
                "a written work", "كتاب", CefrLevel.A1, null,
                "en=oewn-2025;ar=awn-4.0;cefr=cefrj-1.5", T0),
            LexiconEntry.Create($"{ns}-book%1:10:02::", "book", "book", "n",
                "a record of transactions", "سجل", CefrLevel.A1, null,
                "en=oewn-2025;ar=awn-4.0;cefr=cefrj-1.5", T0),
            // Same word, different POS, different meaning and level.
            LexiconEntry.Create($"{ns}-book%2:41:00::", "book", "book", "v",
                "to reserve in advance", "يحجز", CefrLevel.A2, null,
                "en=oewn-2025;ar=awn-4.0;cefr=cefrj-1.5", T0),
            // Shares the prefix but is a different word.
            LexiconEntry.Create($"{ns}-boot%1:06:00::", "boot", "boot", "n",
                "footwear covering the foot and ankle", "حذاء", CefrLevel.A2,
                null, "en=oewn-2025;ar=awn-4.0;cefr=cefrj-1.5", T0),
            // No CEFR band: the level is genuinely unknown, not defaulted.
            LexiconEntry.Create($"{ns}-bosk%1:06:00::", "bosk", "bosk", "n",
                "a small wooded area", "أيكة", null, null,
                "en=oewn-2025;ar=awn-4.0;cefr=none", T0),
            // Outside the prefix entirely.
            LexiconEntry.Create($"{ns}-cat%1:05:00::", "cat", "cat", "n",
                "a small domesticated feline", "قطة", CefrLevel.A1, null,
                "en=oewn-2025;ar=awn-4.0;cefr=cefrj-1.5", T0));

        await context.SaveChangesAsync();
        return ns;
    }

    // ── word / sense uniqueness ──────────────────────────────────────────────

    [SkippableFact]
    public async Task A_sense_id_is_unique_and_a_second_insert_is_refused()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var ns = await SeedAsync();

        await using var context = db.CreateContext();
        context.LexiconEntries.Add(LexiconEntry.Create(
            $"{ns}-book%1:06:00::", "book", "book", "n",
            "a duplicate", "كتاب", CefrLevel.A1, null, "x", T0));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => context.SaveChangesAsync());
    }

    // ── multiple meanings for the same word ──────────────────────────────────

    [SkippableFact]
    public async Task One_word_carries_several_independent_senses()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var ns = await SeedAsync();

        await using var context = db.CreateContext();
        var senses = await context.LexiconEntries
            .Where(l => l.TextNormalized == "book" && l.SenseId.StartsWith(ns))
            .OrderBy(l => l.SenseId)
            .ToListAsync();

        Assert.Equal(3, senses.Count);
        // Each is a distinct meaning — that is the identity the product needs.
        Assert.Equal(3, senses.Select(s => s.MeaningAr).Distinct().Count());
        Assert.Equal(3, senses.Select(s => s.SenseId).Distinct().Count());

        Assert.Contains(senses, s => s.MeaningAr == "كتاب" && s.PartOfSpeech == "n");
        Assert.Contains(senses, s => s.MeaningAr == "يحجز" && s.PartOfSpeech == "v");
    }

    [SkippableFact]
    public async Task The_noun_and_the_verb_may_carry_different_CEFR_levels()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var ns = await SeedAsync();

        await using var context = db.CreateContext();
        var noun = await context.LexiconEntries.SingleAsync(
            l => l.SenseId == $"{ns}-book%1:06:00::");
        var verb = await context.LexiconEntries.SingleAsync(
            l => l.SenseId == $"{ns}-book%2:41:00::");

        Assert.Equal(CefrLevel.A1, noun.CefrLevel);
        Assert.Equal(CefrLevel.A2, verb.CefrLevel);
    }

    // ── Arabic text integrity ────────────────────────────────────────────────

    [SkippableFact]
    public async Task Arabic_meanings_survive_the_round_trip_byte_for_byte()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var ns = await SeedAsync();

        await using var context = db.CreateContext();
        var entry = await context.LexiconEntries.SingleAsync(
            l => l.SenseId == $"{ns}-book%1:06:00::");

        Assert.Equal("كتاب", entry.MeaningAr);
        Assert.Equal(4, entry.MeaningAr.Length);
        // Arabic, not mojibake or a question-mark run.
        Assert.All(entry.MeaningAr, c => Assert.InRange(c, '؀', 'ۿ'));
    }

    [SkippableFact]
    public async Task Arabic_with_diacritics_and_spaces_is_preserved()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        const string meaning = "سِفر التكوين";
        var senseId = $"diacritic-{Guid.NewGuid():N}";

        await using var write = db.CreateContext();
        write.LexiconEntries.Add(LexiconEntry.Create(
            senseId, "genesis", "genesis", "n", "the first book", meaning,
            CefrLevel.C1, null, "en=oewn-2025;ar=awn-4.0", T0));
        await write.SaveChangesAsync();

        await using var read = db.CreateContext();
        var loaded = await read.LexiconEntries.SingleAsync(l => l.SenseId == senseId);

        Assert.Equal(meaning, loaded.MeaningAr);
        Assert.Contains('ِ', loaded.MeaningAr); // kasra survived
    }

    // ── CEFR level mapping ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task A_missing_CEFR_level_is_null_not_a_defaulted_A1()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var ns = await SeedAsync();

        await using var context = db.CreateContext();
        var unlevelled = await context.LexiconEntries.SingleAsync(
            l => l.SenseId == $"{ns}-bosk%1:06:00::");

        // A silent A1 default would tell learners a rare word is beginner
        // vocabulary. Unknown must stay unknown.
        Assert.Null(unlevelled.CefrLevel);
        Assert.Contains("cefr=none", unlevelled.SourceFlags);
    }

    [SkippableFact]
    public async Task CEFR_levels_round_trip_as_their_wire_values()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var senseId = $"levels-{Guid.NewGuid():N}";
        await using var write = db.CreateContext();
        write.LexiconEntries.Add(LexiconEntry.Create(
            senseId, "nuance", "nuance", "n", "a subtle difference", "فارق دقيق",
            CefrLevel.C1, null, "en=oewn-2025;ar=awn-4.0;cefr=cefrj-1.5", T0));
        await write.SaveChangesAsync();

        await using var read = db.CreateContext();
        var loaded = await read.LexiconEntries.SingleAsync(l => l.SenseId == senseId);
        Assert.Equal(CefrLevel.C1, loaded.CefrLevel);

        // Stored as the contract's string, not an ordinal — inserting a value
        // into the enum must never silently re-map existing rows.
        var raw = await read.Database
            .SqlQuery<string?>(
                $"""SELECT "CefrLevel" AS "Value" FROM lexicon_entries WHERE "SenseId" = {senseId}""")
            .SingleAsync();
        Assert.Equal("C1", raw);
    }

    // ── prefix autocomplete ──────────────────────────────────────────────────

    [SkippableFact]
    public async Task Typing_bo_returns_every_matching_sense_with_level_and_meaning()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var ns = await SeedAsync();

        await using var context = db.CreateContext();
        var matches = await context.LexiconEntries
            .Where(l => l.SenseId.StartsWith(ns) && l.TextNormalized.StartsWith("bo"))
            .OrderBy(l => l.TextNormalized).ThenBy(l => l.SenseId)
            .ToListAsync();

        // book ×3, boot, bosk — and never cat.
        Assert.Equal(5, matches.Count);
        Assert.All(matches, m => Assert.StartsWith("bo", m.TextNormalized));
        Assert.DoesNotContain(matches, m => m.TextNormalized == "cat");

        // Every row is directly renderable as "word · level · Arabic meaning".
        foreach (var match in matches)
        {
            Assert.False(string.IsNullOrWhiteSpace(match.Text));
            Assert.False(string.IsNullOrWhiteSpace(match.MeaningAr));
        }
    }

    [SkippableFact]
    public async Task Prefix_search_is_case_insensitive_via_the_normalised_column()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var senseId = $"caps-{Guid.NewGuid():N}";
        await using var write = db.CreateContext();
        // OEWN carries proper nouns capitalised; the learner types lowercase.
        write.LexiconEntries.Add(LexiconEntry.Create(
            senseId, "Bible", "Bible", "n", "the sacred writings", "الإنجيل",
            CefrLevel.B1, null, "en=oewn-2025;ar=awn-4.0", T0));
        await write.SaveChangesAsync();

        await using var read = db.CreateContext();
        var found = await read.LexiconEntries
            .Where(l => l.SenseId == senseId && l.TextNormalized.StartsWith("bib"))
            .SingleOrDefaultAsync();

        Assert.NotNull(found);
        Assert.Equal("Bible", found.Text);           // display form preserved
        Assert.Equal("bible", found.TextNormalized); // search form lowercased
    }

    [SkippableFact]
    public async Task A_string_that_is_not_a_word_matches_nothing()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        await SeedAsync();

        await using var context = db.CreateContext();
        var matches = await context.LexiconEntries
            .Where(l => l.TextNormalized.StartsWith("hch"))
            .ToListAsync();

        // `hch` must never resolve — this is what stops it entering a
        // learner's vocabulary (demo review §16).
        Assert.Empty(matches);
    }

    // ── invalid / missing mappings ───────────────────────────────────────────

    [SkippableFact]
    public void An_entry_without_an_Arabic_meaning_is_rejected_at_construction()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        // The importer drops these rather than storing a blank; the entity
        // refuses them too, so neither path can create one.
        Assert.ThrowsAny<ArgumentException>(() => LexiconEntry.Create(
            "x", "word", "word", "n", "def", "", CefrLevel.A1, null, "s", T0));
        Assert.ThrowsAny<ArgumentException>(() => LexiconEntry.Create(
            "x", "word", "word", "n", "def", "   ", CefrLevel.A1, null, "s", T0));
    }

    [SkippableFact]
    public void An_entry_without_a_sense_id_or_text_is_rejected()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        Assert.ThrowsAny<ArgumentException>(() => LexiconEntry.Create(
            "", "word", "word", "n", "def", "كلمة", CefrLevel.A1, null, "s", T0));
        Assert.ThrowsAny<ArgumentException>(() => LexiconEntry.Create(
            "x", "", "word", "n", "def", "كلمة", CefrLevel.A1, null, "s", T0));
    }

    [SkippableFact]
    public async Task Every_row_records_which_source_supplied_its_fields()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);
        var ns = await SeedAsync();

        await using var context = db.CreateContext();
        var entries = await context.LexiconEntries
            .Where(l => l.SenseId.StartsWith(ns))
            .ToListAsync();

        // Provenance is per row, so an entry whose Arabic came from the
        // machine-translated portion of AWN stays auditable (ADR-012).
        Assert.All(entries, e =>
        {
            Assert.Contains("en=oewn", e.SourceFlags);
            Assert.Contains("ar=awn", e.SourceFlags);
            Assert.Contains("cefr=", e.SourceFlags);
        });
    }

    // ── re-running the importer ──────────────────────────────────────────────

    [SkippableFact]
    public async Task Re_importing_the_same_sense_updates_it_without_duplicating()
    {
        Skip.IfNot(db.IsAvailable, db.SkipReason);

        var senseId = $"reimport-{Guid.NewGuid():N}";

        // Exactly the INSERT … ON CONFLICT the importer issues.
        async Task UpsertAsync(string meaning, string level)
        {
            await using var context = db.CreateContext();
            await context.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO lexicon_entries (
                     "SenseId","Text","TextNormalized","Lemma","PartOfSpeech",
                     "DefinitionEn","MeaningAr","CefrLevel","FrequencyRank",
                     "SourceFlags","UpdatedAt")
                 VALUES ({senseId}, 'book', 'book', 'book', 'n',
                     'a written work', {meaning}, {level}, NULL,
                     'en=oewn-2025;ar=awn-4.0', now())
                 ON CONFLICT ("SenseId") DO UPDATE SET
                     "MeaningAr" = EXCLUDED."MeaningAr",
                     "CefrLevel" = EXCLUDED."CefrLevel",
                     "UpdatedAt" = EXCLUDED."UpdatedAt"
                 """);
        }

        await UpsertAsync("كتاب", "A1");
        await UpsertAsync("كتاب", "A1");
        await UpsertAsync("مُؤلَّف", "A2"); // upstream corrected the gloss

        await using var context = db.CreateContext();
        var rows = await context.LexiconEntries
            .Where(l => l.SenseId == senseId)
            .ToListAsync();

        Assert.Single(rows);
        Assert.Equal("مُؤلَّف", rows[0].MeaningAr);
        Assert.Equal(CefrLevel.A2, rows[0].CefrLevel);
    }
}
