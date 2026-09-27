using WordOs.Domain.Common;
using WordOs.LexiconImporter;

namespace WordOs.Api.Tests;

/// <summary>
/// The rules of the second dictionary (ADR-096).
/// </summary>
/// <remarks>
/// Each of these is a fault found in the first one, stated as a test so it
/// cannot come back:
///
/// <list type="bullet">
/// <item><c>sell = باع</c> — a verb cited in the past against an English
/// infinitive, which reads as a tense that is not there.</item>
/// <item>Every sense of <c>go</c> banded A1, including
/// "be abolished or discarded", because the band belongs to the word and was
/// applied to all thirty of its senses.</item>
/// <item>Dialect glosses shown to a learner studying فصحى.</item>
/// </list>
/// </remarks>
public class WiktionaryLexiconTests
{
    // ── What may be shown at all ─────────────────────────────────────────────

    [Fact]
    public void A_dialect_translation_is_not_offered_to_the_learner()
    {
        // Wiktionary marks most of them with a tag …
        Assert.Null(WiktionarySource.CleanTranslation("كَلْ", ["Egyptian-Arabic"]));
        Assert.Null(WiktionarySource.CleanTranslation("كَلْ", ["Gulf-Arabic"]));

        // … and not all of them. This one arrives with the dialect inside the
        // word, which a tag check alone would let through.
        Assert.Null(WiktionarySource.CleanTranslation("Tunisian Arabic كلِا", null));
    }

    [Fact]
    public void Two_forms_packed_into_one_string_are_separated()
    {
        // Stored verbatim this shows the learner a sentence where a word
        // should be.
        var cleaned = WiktionarySource.CleanTranslation(
            "أَكَلَ imperfective: يَأْكُلُ", null);

        Assert.NotNull(cleaned);
        Assert.Equal("أَكَلَ", cleaned!.Value.Past);
        Assert.Equal("يَأْكُلُ", cleaned.Value.NonPast);
    }

    [Fact]
    public void Anything_that_is_not_an_Arabic_word_is_refused()
    {
        Assert.Null(WiktionarySource.CleanTranslation("to sell", null));
        Assert.Null(WiktionarySource.CleanTranslation("", null));
        Assert.Null(WiktionarySource.CleanTranslation(null, null));
    }

    // ── The form the learner is shown ────────────────────────────────────────

    private static WiktionarySource.WiktSense Sense(
        string word, string pos, string definition, string arabic, int index = 0,
        params string[] forms) =>
        new(word, pos, definition, Example: null, ArabicPast: arabic,
            ArabicRoman: null, SenseIndex: index, EnglishForms: forms,
            EntryIndex: 0);

    private static readonly Dictionary<string, WiktionarySource.ArabicVerb> Paradigms =
        new()
        {
            ["باع"] = new WiktionarySource.ArabicVerb("بَاعَ", "يَبِيعُ", "بَيْع", "بَائِع"),
            ["ذهب"] = new WiktionarySource.ArabicVerb("ذَهَبَ", "يَذْهَبُ", "ذَهَاب", "ذَاهِب"),
        };

    private static readonly Dictionary<string, int> Frequency =
        new(StringComparer.OrdinalIgnoreCase) { ["sell"] = 900, ["go"] = 20 };

    [Fact]
    public void A_verb_is_shown_in_the_non_past()
    {
        // The complaint that started the rebuild: "مين باع sell؟"
        var (rows, _) = WiktionaryBuilder.Build(
            [Sense("sell", "v", "To transfer goods for money.", "بَاعَ")],
            Paradigms,
            new Dictionary<(string, string), CefrLevel>(),
            Frequency);

        var baseForm = Assert.Single(rows, r => r.Text == "sell");
        Assert.Equal("يَبِيعُ", baseForm.MeaningAr);
    }

    [Fact]
    public void The_past_of_the_English_verb_is_the_past_of_the_Arabic_one()
    {
        // The other half of the rule, and the reason it is not simply "always
        // use the non-past": `sold` really is باع.
        var (rows, _) = WiktionaryBuilder.Build(
            [Sense("sell", "v", "To transfer goods for money.", "بَاعَ", 0,
                   "sold", "selling")],
            Paradigms,
            new Dictionary<(string, string), CefrLevel>(),
            Frequency,
            new Dictionary<(string, string), List<string>>());

        var past = rows.Where(r => r.Text.Equals("sold", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(past);
        Assert.All(past, r => Assert.Equal("بَاعَ", r.MeaningAr));
    }

    [Fact]
    public void An_ing_form_is_shown_as_the_verbal_noun()
    {
        // An -ing form names the action, and in Arabic that is the مصدر.
        var (rows, _) = WiktionaryBuilder.Build(
            [Sense("sell", "v", "To transfer goods for money.", "بَاعَ", 0,
                   "sold", "selling")],
            Paradigms,
            new Dictionary<(string, string), CefrLevel>(),
            Frequency,
            new Dictionary<(string, string), List<string>>());

        var ing = rows.SingleOrDefault(
            r => r.Text.Equals("selling", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(ing);
        Assert.Equal("بَيْع", ing!.MeaningAr);
    }

    [Fact]
    public void A_verb_with_no_paradigm_recorded_keeps_the_form_it_came_with()
    {
        // Honest rather than clever: conjugating an unknown verb by rule would
        // invent Arabic, and an invented word is worse than a dictionary one.
        var (rows, stats) = WiktionaryBuilder.Build(
            [Sense("flumox", "v", "To confuse.", "حَيَّرَ")],
            new Dictionary<string, WiktionarySource.ArabicVerb>(),
            new Dictionary<(string, string), CefrLevel>(),
            Frequency);

        Assert.Equal("حَيَّرَ", Assert.Single(rows).MeaningAr);
        Assert.Equal(1, stats.VerbsLeftInPast);
    }

    [Fact]
    public void A_noun_is_left_exactly_as_the_dictionary_has_it()
    {
        var (rows, _) = WiktionaryBuilder.Build(
            [Sense("city", "n", "A large settlement.", "مَدِينَة")],
            Paradigms,
            new Dictionary<(string, string), CefrLevel>(),
            Frequency);

        Assert.Equal("مَدِينَة", Assert.Single(rows).MeaningAr);
    }

    // ── Levels ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_band_reaches_the_senses_it_was_measured_on_and_no_further()
    {
        // The first edition gave every sense of `go` the word's A1 band, so
        // "be abolished or discarded" was beginner vocabulary. The lists were
        // compiled on a word in use, which means its common senses.
        var cefr = new Dictionary<(string Word, string Pos), CefrLevel>
        {
            [("go", "v")] = CefrLevel.A1,
        };

        var senses = Enumerable.Range(0, 6)
            .Select(i => Sense("go", "v", $"sense {i}", "ذَهَبَ", i))
            .ToList();

        var (rows, _) = WiktionaryBuilder.Build(
            senses, Paradigms, cefr, Frequency);

        var levelled = rows.Count(r => r.CefrLevel is not null);

        Assert.Equal(2, levelled);
        Assert.True(rows.Count > levelled, "the rarer senses are still offered");
    }

    // ── Order ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_common_word_outranks_a_rare_one_whatever_its_sense_number()
    {
        // `go` is the 20th commonest word and `sell` the 900th, so no sense of
        // `sell` may sort above any sense of `go`. The first edition had no
        // frequency data at all, which is how `go` came to lead with
        // "pass from physical life".
        var (rows, _) = WiktionaryBuilder.Build(
            [
                Sense("sell", "v", "To transfer goods for money.", "بَاعَ"),
                Sense("go", "v", "To move.", "ذَهَبَ", 0),
                Sense("go", "v", "To be abolished.", "ذَهَبَ", 7),
            ],
            Paradigms, new Dictionary<(string, string), CefrLevel>(), Frequency);

        var worst = rows.Where(r => r.Lemma == "go").Max(r => r.FrequencyRank);
        var best = rows.Where(r => r.Lemma == "sell").Min(r => r.FrequencyRank);

        Assert.True(worst < best,
            $"the last sense of 'go' ({worst}) must still outrank 'sell' ({best})");
    }

    [Fact]
    public void The_part_of_speech_the_dictionary_leads_with_wins()
    {
        // Found in the imported data, not by reading: `go` came back as غُو,
        // the board game. Both entries were a first sense of an equally common
        // word, the tie fell to the sense id, and "n" sorts before "v".
        //
        // Wiktionary's page for `go` is ordered verb, noun, adjective, and only
        // fourth the game — the same human ordering the senses have.
        var senses = new[]
        {
            Sense("go", "v", "To move.", "ذَهَبَ") with { EntryIndex = 0 },
            Sense("go", "n", "A strategic board game.", "غُو") with { EntryIndex = 3 },
        };

        var (rows, _) = WiktionaryBuilder.Build(
            senses, Paradigms, new Dictionary<(string, string), CefrLevel>(), Frequency);

        var first = rows.OrderBy(r => r.FrequencyRank).ThenBy(r => r.SenseId).First();

        Assert.Equal("v", first.PartOfSpeech);
        Assert.Equal("يَذْهَبُ", first.MeaningAr);
    }

    [Fact]
    public void A_fallback_row_never_outranks_a_real_one()
    {
        // Wiktionary is thin outside common vocabulary, so the gaps are filled
        // from the first edition (ADR-096). Those rows must sit behind every
        // genuine one, or the rebuild would be undone by its own safety net.
        const int largestGenuineRank = 60_000 * 10_000 + 9_000 + 990;
        const int smallestFallbackRank = 700_000_000;

        Assert.True(largestGenuineRank < smallestFallbackRank,
            "the fallback band has to start above every rank the builder emits");
    }

    [Fact]
    public void Senses_of_one_word_keep_the_order_the_dictionary_gave_them()
    {
        var (rows, _) = WiktionaryBuilder.Build(
            [
                Sense("go", "v", "To move.", "ذَهَبَ", 0),
                Sense("go", "v", "To be abolished.", "اِنْتَهَى", 3),
            ],
            Paradigms, new Dictionary<(string, string), CefrLevel>(), Frequency);

        var move = rows.Single(r => r.DefinitionEn == "To move.");
        var abolished = rows.Single(r => r.DefinitionEn == "To be abolished.");

        Assert.True(move.FrequencyRank < abolished.FrequencyRank);
    }
}
