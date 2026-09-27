using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Lexicon;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Tests;

/// <summary>
/// Two dictionaries in one table, and the setting that chooses between them
/// (ADR-096).
/// </summary>
/// <remarks>
/// This is the property the rebuild rests on: the first dictionary was not
/// deleted, so if the second turns out worse in a learner's hands, going back
/// is a configuration change rather than a re-import. A test is the only thing
/// that keeps that true.
/// </remarks>
[Collection(PostgresCollection.Name)]
public class LexiconEditionTests(PostgresFixture db)
{
    private static LexiconEntry Row(string ns, string word, string meaning, string edition) =>
        LexiconEntry.Create(
            senseId: $"{ns}-{edition}-{word}",
            text: word,
            lemma: word,
            partOfSpeech: "v",
            definitionEn: $"to {word}",
            meaningAr: meaning,
            cefrLevel: CefrLevel.A1,
            frequencyRank: 1,
            sourceFlags: "en=test;ar=test;cefr=test",
            now: DateTimeOffset.UtcNow,
            edition: edition);

    private async Task<(WordOsDbContext Db, string Word)> SeedBothAsync(
        WordOsConfiguration configuration)
    {
        var ns = Guid.NewGuid().ToString("N")[..8];
        var word = $"zz{ns}";

        var options = new DbContextOptionsBuilder<WordOsDbContext>()
            .UseNpgsql(db.ConnectionString)
            .Options;

        await using (var seed = new WordOsDbContext(options))
        {
            seed.LexiconEntries.Add(Row(ns, word, "القديم", LexiconEditions.OewnAwn));
            seed.LexiconEntries.Add(Row(ns, word, "الجديد", LexiconEditions.Wiktionary));
            await seed.SaveChangesAsync();
        }

        return (new WordOsDbContext(options, configuration), word);
    }

    [Fact]
    public async Task A_search_reads_only_the_edition_the_setting_names()
    {
        var (context, word) = await SeedBothAsync(
            new WordOsConfiguration { LexiconEdition = LexiconEditions.Wiktionary });

        await using (context)
        {
            var found = await context.ActiveLexicon
                .Where(l => l.TextNormalized == word)
                .ToListAsync();

            Assert.Equal("الجديد", Assert.Single(found).MeaningAr);
        }
    }

    [Fact]
    public async Task Moving_the_setting_back_restores_the_first_dictionary()
    {
        // The whole point of keeping it. No re-import, no deploy.
        var (context, word) = await SeedBothAsync(
            new WordOsConfiguration { LexiconEdition = LexiconEditions.OewnAwn });

        await using (context)
        {
            var found = await context.ActiveLexicon
                .Where(l => l.TextNormalized == word)
                .ToListAsync();

            Assert.Equal("القديم", Assert.Single(found).MeaningAr);
        }
    }

    [Fact]
    public async Task A_word_a_learner_owns_resolves_whichever_edition_it_came_from()
    {
        // The sharp one. Resolution is deliberately edition-blind: a learner
        // who added a word under the first dictionary must not find it broken
        // because the setting moved. Search is scoped; this is not.
        var (context, word) = await SeedBothAsync(
            new WordOsConfiguration { LexiconEdition = LexiconEditions.Wiktionary });

        await using (context)
        {
            var bothEditions = await context.LexiconEntries
                .Where(l => l.TextNormalized == word)
                .ToListAsync();

            Assert.Equal(2, bothEditions.Count);
        }
    }
}
