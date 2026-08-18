using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WordOs.Domain.Common;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Tests;

/// <summary>
/// Asserts against the <b>real imported lexicon</b> in the development
/// database, not a fixture.
/// </summary>
/// <remarks>
/// <see cref="LexiconImportTests"/> pins the rules on hand-built rows;
/// this pins that the actual import produced sane data. It skips when the
/// lexicon has not been imported, so a fresh clone does not fail — but it can
/// never pass by accident, because every assertion needs real rows.
///
/// Run the import first:
/// <code>
/// cd tools/lexicon &amp;&amp; ./download.sh
/// dotnet run --project tools/lexicon/importer -- tools/lexicon/data
/// </code>
/// </remarks>
public class ImportedLexiconTests : IAsyncLifetime
{
    private const int MinimumExpectedRows = 50_000;

    private WordOsDbContext? _context;
    private string? _skipReason;
    private int _rowCount;

    public async Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<ImportedLexiconTests>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString =
            configuration.GetConnectionString("WordOs")
            ?? configuration.GetConnectionString("WordOsMigrations");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _skipReason = "No development connection string configured.";
            return;
        }

        var options = new DbContextOptionsBuilder<WordOsDbContext>()
            .UseNpgsql(connectionString).Options;
        _context = new WordOsDbContext(options);

        try
        {
            _rowCount = await _context.LexiconEntries.CountAsync();
        }
        catch (Exception ex)
        {
            _skipReason = $"Development database unreachable: {ex.GetType().Name}";
            return;
        }

        if (_rowCount < MinimumExpectedRows)
        {
            _skipReason =
                $"Lexicon holds {_rowCount:N0} rows (< {MinimumExpectedRows:N0}). " +
                "Run tools/lexicon/download.sh then the importer.";
        }
    }

    public async Task DisposeAsync()
    {
        if (_context is not null) await _context.DisposeAsync();
    }

    private WordOsDbContext Db => _context!;

    [SkippableFact]
    public void The_import_produced_a_substantial_lexicon()
    {
        Skip.IfNot(_skipReason is null, _skipReason);
        Assert.True(_rowCount >= MinimumExpectedRows);
    }

    [SkippableFact]
    public async Task Every_sense_id_is_unique()
    {
        Skip.IfNot(_skipReason is null, _skipReason);

        var distinct = await Db.LexiconEntries
            .Select(l => l.SenseId).Distinct().CountAsync();

        Assert.Equal(_rowCount, distinct);
    }

    [SkippableFact]
    public async Task Every_row_has_an_Arabic_meaning()
    {
        Skip.IfNot(_skipReason is null, _skipReason);

        // A row without one could never be shown to a learner.
        var blank = await Db.LexiconEntries
            .CountAsync(l => l.MeaningAr == "" || l.MeaningAr == null);

        Assert.Equal(0, blank);
    }

    [SkippableFact]
    public async Task Arabic_meanings_actually_contain_Arabic_script()
    {
        Skip.IfNot(_skipReason is null, _skipReason);

        var sample = await Db.LexiconEntries
            .OrderBy(l => l.SenseId).Take(500)
            .Select(l => l.MeaningAr).ToListAsync();

        // Guards against an encoding failure that silently stored '?' runs.
        var withArabic = sample.Count(m => m.Any(c => c >= '؀' && c <= 'ۿ'));
        Assert.True(withArabic > sample.Count * 0.9,
            $"only {withArabic}/{sample.Count} sampled meanings contained Arabic");
    }

    [SkippableFact]
    public async Task A_common_word_resolves_to_several_distinct_meanings()
    {
        Skip.IfNot(_skipReason is null, _skipReason);

        var senses = await Db.LexiconEntries
            .Where(l => l.TextNormalized == "book")
            .ToListAsync();

        Assert.True(senses.Count >= 3, $"'book' produced {senses.Count} senses");
        // Distinct glosses — the deduplication collapsed synonymous ones.
        Assert.Equal(
            senses.Select(s => (s.PartOfSpeech, s.MeaningAr)).Distinct().Count(),
            senses.Count);
        Assert.Contains(senses, s => s.MeaningAr.Contains("كتاب"));
    }

    [SkippableFact]
    public async Task Prefix_autocomplete_returns_word_level_and_meaning()
    {
        Skip.IfNot(_skipReason is null, _skipReason);

        var matches = await Db.LexiconEntries
            .Where(l => l.TextNormalized.StartsWith("bo") && l.CefrLevel != null)
            .OrderBy(l => l.FrequencyRank).ThenBy(l => l.TextNormalized)
            .Take(20)
            .ToListAsync();

        Assert.NotEmpty(matches);
        Assert.All(matches, m =>
        {
            Assert.StartsWith("bo", m.TextNormalized);
            Assert.False(string.IsNullOrWhiteSpace(m.MeaningAr));
            Assert.NotNull(m.CefrLevel);
        });
        Assert.Contains(matches, m => m.TextNormalized == "book");
    }

    [SkippableFact]
    public async Task A_non_word_resolves_to_nothing()
    {
        Skip.IfNot(_skipReason is null, _skipReason);

        foreach (var junk in new[] { "hch", "zzzqq", "asdfgh" })
        {
            var count = await Db.LexiconEntries
                .CountAsync(l => l.TextNormalized == junk);
            Assert.Equal(0, count);
        }
    }

    [SkippableFact]
    public async Task CEFR_levels_only_ever_hold_valid_bands()
    {
        Skip.IfNot(_skipReason is null, _skipReason);

        var levels = await Db.LexiconEntries
            .Where(l => l.CefrLevel != null)
            .Select(l => l.CefrLevel!.Value)
            .Distinct()
            .ToListAsync();

        Assert.NotEmpty(levels);
        Assert.All(levels, l => Assert.True(Enum.IsDefined(l)));
    }

    [SkippableFact]
    public async Task Levelled_words_span_beginner_through_advanced()
    {
        Skip.IfNot(_skipReason is null, _skipReason);

        var byLevel = await Db.LexiconEntries
            .Where(l => l.CefrLevel != null)
            .GroupBy(l => l.CefrLevel!.Value)
            .Select(g => new { Level = g.Key, Count = g.Count() })
            .ToListAsync();

        // A distribution collapsed onto one band would mean the CEFR join
        // silently failed.
        Assert.Contains(byLevel, x => x.Level == CefrLevel.A1 && x.Count > 100);
        Assert.Contains(byLevel, x => x.Level == CefrLevel.B2 && x.Count > 100);
        Assert.Contains(byLevel, x => x.Level == CefrLevel.C1 && x.Count > 50);
    }

    [SkippableFact]
    public async Task Unlevelled_rows_are_null_rather_than_defaulted()
    {
        Skip.IfNot(_skipReason is null, _skipReason);

        var unlevelled = await Db.LexiconEntries
            .CountAsync(l => l.CefrLevel == null);

        // Most of WordNet is beyond the CEFR lists; those must stay unknown,
        // not be silently labelled A1.
        Assert.True(unlevelled > 0);

        var mislabelled = await Db.LexiconEntries
            .CountAsync(l => l.CefrLevel == null && l.SourceFlags.Contains("cefr=cefrj"));
        Assert.Equal(0, mislabelled);
    }

    [SkippableFact]
    public async Task Every_row_carries_provenance_for_all_three_sources()
    {
        Skip.IfNot(_skipReason is null, _skipReason);

        // Two provenances, and a row must declare one of them: joined from the
        // three datasets, or authored here because no dataset carries it
        // (ADR-033 — WordNet has no pronouns, articles or auxiliaries).
        var missing = await Db.LexiconEntries
            .CountAsync(l => !l.SourceFlags.Contains("wordos-closed-class")
                             && (!l.SourceFlags.Contains("en=oewn")
                                 || !l.SourceFlags.Contains("ar=awn")
                                 || !l.SourceFlags.Contains("cefr=")));

        Assert.Equal(0, missing);
    }

    [SkippableTheory]
    // The words a learner reported not being able to add, and the classes they
    // stand for: auxiliary, question word, article, preposition, conjunction,
    // pronoun (ADR-033).
    [InlineData("is", "aux")]
    [InlineData("are", "aux")]
    [InlineData("what", "pron")]
    [InlineData("the", "det")]
    [InlineData("with", "prep")]
    [InlineData("because", "conj")]
    [InlineData("they", "pron")]
    [InlineData("can", "modal")]
    public async Task The_closed_class_words_are_in_the_lexicon(
        string word, string pos)
    {
        Skip.IfNot(_skipReason is null, _skipReason);

        var entry = await Db.LexiconEntries.FirstOrDefaultAsync(
            l => l.TextNormalized == word && l.PartOfSpeech == pos);

        Assert.NotNull(entry);
        Assert.False(string.IsNullOrWhiteSpace(entry!.MeaningAr));
        Assert.False(string.IsNullOrWhiteSpace(entry.DefinitionEn));
        Assert.NotNull(entry.CefrLevel);

        // Ahead of every WordNet sense, so the homograph cannot bury it.
        Assert.Equal(-1, entry.FrequencyRank);
    }

    [SkippableFact]
    public async Task Every_arabic_gloss_has_a_searchable_form()
    {
        Skip.IfNot(_skipReason is null, _skipReason);

        // Searching in Arabic is a match against the folded column; a row that
        // never got one is a word no Arabic speaker can find (ADR-034).
        var unsearchable = await Db.LexiconEntries
            .CountAsync(l => l.MeaningArNormalized == "");

        Assert.Equal(0, unsearchable);
    }

    [SkippableFact]
    public async Task The_normalised_column_is_genuinely_lowercased()
    {
        Skip.IfNot(_skipReason is null, _skipReason);

        // Prefix search depends on this; a stray uppercase row would be
        // invisible to a learner typing lowercase.
        var broken = await Db.LexiconEntries
            .CountAsync(l => l.TextNormalized != l.TextNormalized.ToLower());

        Assert.Equal(0, broken);
    }
}
