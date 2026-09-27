using WordOs.LexiconImporter;

namespace WordOs.Api.Tests;

/// <summary>
/// The parsers, against the real exports rather than a fixture.
/// </summary>
/// <remarks>
/// Skipped — never silently passed — when the downloads are not present, the
/// same arrangement <see cref="ImportedLexiconTests"/> uses. The rules are
/// pinned deterministically in <see cref="WiktionaryLexiconTests"/>; what these
/// add is proof that the files actually parse, which a fixture cannot give.
/// </remarks>
public class WiktionaryDataTests
{
    private static string? DataDir()
    {
        var dir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "tools", "lexicon", "data"));
        return Directory.Exists(dir) ? dir : null;
    }

    private static Dictionary<string, WiktionarySource.ArabicVerb>? Verbs;
    private static readonly Lock Gate = new();

    private static Dictionary<string, WiktionarySource.ArabicVerb>? LoadVerbs()
    {
        var dir = DataDir();
        if (dir is null) return null;

        var path = Path.Combine(dir, "wiktionary-ar.jsonl");
        if (!File.Exists(path)) return null;

        lock (Gate)
        {
            // Half a gigabyte of JSON. Parsed once for the whole class.
            return Verbs ??= WiktionarySource.ReadArabicVerbs(path);
        }
    }

    [SkippableFact]
    public void The_Arabic_export_yields_a_paradigm_for_the_everyday_verbs()
    {
        var verbs = LoadVerbs();
        Skip.If(verbs is null, "wiktionary-ar.jsonl not downloaded — run ./download.sh");

        Assert.True(verbs!.Count > 5_000,
            $"only {verbs.Count:N0} Arabic verbs parsed — the export shape has changed");

        // The word that started the rebuild, and four more a learner meets in
        // their first week. Each must yield the non-past, because that is what
        // the English base form is shown as.
        var expected = new Dictionary<string, string>
        {
            ["باع"] = "يبيع",
            ["ذهب"] = "يذهب",
            ["أكل"] = "يأكل",
            ["كتب"] = "يكتب",
            ["قرأ"] = "يقرأ",
        };

        foreach (var (past, nonPast) in expected)
        {
            Assert.True(verbs.TryGetValue(past, out var paradigm),
                $"no Arabic entry found for {past}");
            Assert.NotNull(paradigm!.NonPast);
            Assert.Equal(
                nonPast,
                WiktionarySource.StripHarakat(paradigm.NonPast!).Trim());
        }
    }

    [SkippableFact]
    public void The_paradigm_carries_the_verbal_noun_an_ing_form_needs()
    {
        var verbs = LoadVerbs();
        Skip.If(verbs is null, "wiktionary-ar.jsonl not downloaded — run ./download.sh");

        Assert.True(verbs!.TryGetValue("باع", out var sell));
        Assert.Equal("بيع", WiktionarySource.StripHarakat(sell!.VerbalNoun ?? "").Trim());
    }

    [SkippableFact]
    public void Every_form_read_out_is_Arabic_script()
    {
        // The export mixes romanisation into the same `forms` array — `bāʕa`
        // sits beside `بَاعَ`. A romanisation stored as a meaning would be
        // shown to the learner as their Arabic.
        var verbs = LoadVerbs();
        Skip.If(verbs is null, "wiktionary-ar.jsonl not downloaded — run ./download.sh");

        foreach (var v in verbs!.Values.Take(20_000))
        {
            Assert.True(WiktionarySource.IsArabicScript(v.Past), v.Past);
            if (v.NonPast is not null)
                Assert.True(WiktionarySource.IsArabicScript(v.NonPast), v.NonPast);
            if (v.VerbalNoun is not null)
                Assert.True(WiktionarySource.IsArabicScript(v.VerbalNoun), v.VerbalNoun);
        }
    }
}
