using WordOs.Domain.Common;

namespace WordOs.LexiconImporter;

public sealed record WiktBuildStats(
    int SensesRead,
    int Emitted,
    int InflectedRows,
    int VerbsGivenNonPast,
    int VerbsLeftInPast,
    int WithCefr,
    int DroppedNoFrequency);

/// <summary>
/// Turns Wiktionary senses into lexicon rows — the second edition (ADR-096).
/// </summary>
/// <remarks>
/// Three rules distinguish it from the first edition, each answering a fault
/// found in the data rather than a preference:
///
/// <list type="number">
/// <item><b>Order comes from Wiktionary.</b> Its senses are ordered by people,
/// so the everyday sense is first. The old rank was invented here, and it put
/// <c>تُوُفِّيَ</c> at the top of <c>go</c>.</item>
///
/// <item><b>The Arabic form follows the English inflection.</b>
/// <c>sell → يبيع</c>, <c>sold → باع</c>, <c>selling → بَيْع</c>. A dictionary
/// cites a verb in the past; a learner reading <c>sell = باع</c> reads a tense
/// that is not there.</item>
///
/// <item><b>CEFR reaches the senses it was measured on.</b> The bands are
/// published per (word, POS), so applying one to all thirty senses of
/// <c>go</c> — as the first edition did — claims that
/// "be abolished or discarded" is A1. Only the leading senses take the band;
/// the rest are left unlevelled, which is honest and is what R6 and the
/// placement engine already cope with.</item>
/// </list>
/// </remarks>
public static class WiktionaryBuilder
{
    public const string Edition = "wiktionary";

    /// <summary>
    /// How many senses of a word may inherit its published CEFR band.
    /// </summary>
    /// <remarks>
    /// The lists were compiled by looking at a word in use, which in practice
    /// means its common senses. Two is the number of senses a learner would
    /// recognise as "the word"; beyond that the band is an extrapolation
    /// nobody measured.
    /// </remarks>
    private const int CefrReachesSenses = 2;

    /// <summary>Words outside the frequency list still sort, just last.</summary>
    /// <remarks>
    /// The rank is <c>word × 10,000 + entry × 1,000 + sense × 10</c>, so the
    /// ceiling has to stay well inside <see cref="int"/>: 60,000 words leaves
    /// the largest rank at 600 million against a limit of 2.1 billion.
    /// </remarks>
    private const int UnrankedWord = 60_000;

    public static (List<LexiconRow> Rows, WiktBuildStats Stats) Build(
        IEnumerable<WiktionarySource.WiktSense> senses,
        IReadOnlyDictionary<string, WiktionarySource.ArabicVerb> arabicVerbs,
        IReadOnlyDictionary<(string Word, string Pos), CefrLevel> cefr,
        IReadOnlyDictionary<string, int> frequency,
        IReadOnlyDictionary<(string Word, string Pos), List<string>>? englishForms = null)
    {
        var rows = new List<LexiconRow>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        int read = 0, inflected = 0, gotNonPast = 0, leftPast = 0, withCefr = 0, unranked = 0;

        foreach (var sense in senses)
        {
            read++;

            var normalized = sense.Word.ToLowerInvariant();
            var isVerb = sense.PartOfSpeech == "v";

            WiktionarySource.ArabicVerb? paradigm = null;
            if (isVerb)
            {
                var key = WiktionarySource.StripHarakat(sense.ArabicPast).Trim();
                if (arabicVerbs.TryGetValue(key, out var found)) paradigm = found;
            }

            // Rule 2, at its narrowest point: what the learner is shown for the
            // *base* English form.
            var baseArabic = isVerb
                ? paradigm?.NonPast ?? sense.ArabicPast
                : sense.ArabicPast;

            if (isVerb)
            {
                if (paradigm?.NonPast is not null) gotNonPast++;
                else leftPast++;
            }

            if (!frequency.TryGetValue(normalized, out var wordRank))
            {
                wordRank = UnrankedWord;
                unranked++;
            }

            // Three orderings, nested, all of them measured or human-made:
            // how common the word is, then which part of speech the dictionary
            // leads with, then which sense. Nothing here is invented, which is
            // the difference from the first edition's rank.
            var rank = wordRank * 10_000
                       + Math.Min(sense.EntryIndex, 9) * 1_000
                       + Math.Min(sense.SenseIndex, 99) * 10;

            CefrLevel? level = null;
            if (sense.SenseIndex < CefrReachesSenses
                && cefr.TryGetValue((normalized, sense.PartOfSpeech), out var band))
            {
                level = band;
                withCefr++;
            }

            var senseId = SenseId(normalized, sense.PartOfSpeech, sense.SenseIndex);
            if (senseId is null || !ids.Add(senseId)) continue;

            rows.Add(new LexiconRow(
                SenseId: senseId,
                Text: sense.Word,
                TextNormalized: normalized,
                Lemma: normalized,
                PartOfSpeech: sense.PartOfSpeech,
                DefinitionEn: sense.Definition,
                MeaningAr: baseArabic,
                CefrLevel: level,
                FrequencyRank: rank,
                SourceFlags: SourceFlags(sense, paradigm, level)));

            // ── The inflected forms ──────────────────────────────────────────
            if (englishForms is null) continue;

            // From the entry itself when it listed any, and only then. An
            // empty list makes `Inflections` fall through to the regular
            // spelling rules, which is right for `walk` and wrong for `take`.
            var listed = sense.EnglishForms.Count > 0
                ? sense.EnglishForms
                : englishForms.TryGetValue(
                      (sense.Word, sense.PartOfSpeech), out var recorded)
                    ? recorded
                    : (IReadOnlyList<string>)[];

            foreach (var form in Inflections.For(
                         sense.Word, sense.PartOfSpeech, listed))
            {
                var arabic = ArabicFor(form.Form, sense, paradigm, baseArabic);
                if (arabic is null) continue;

                var id = $"{senseId}#{form.Form.ToString().ToLowerInvariant()}";
                if (id.Length > MaxSenseId || !ids.Add(id)) continue;

                var (labelEn, labelAr) = Inflections.Label(form.Form);

                rows.Add(new LexiconRow(
                    SenseId: id,
                    Text: form.Text,
                    TextNormalized: form.Text.ToLowerInvariant(),
                    Lemma: normalized,
                    PartOfSpeech: sense.PartOfSpeech,
                    DefinitionEn: $"{labelEn} of \"{sense.Word}\" — {sense.Definition}",
                    MeaningAr: arabic,
                    // An inflected form is not separately levelled: the band
                    // belongs to the word, and `sold` is not harder than `sell`.
                    CefrLevel: level,
                    // Just after its own base form, never before another word's
                    // and never before the next sense of this one.
                    FrequencyRank: rank + 5,
                    SourceFlags: SourceFlags(sense, paradigm, level) + ";form=" + form.Form));
                inflected++;
            }
        }

        return (rows, new WiktBuildStats(
            read, rows.Count, inflected, gotNonPast, leftPast, withCefr, unranked));
    }

    /// <summary>
    /// The Arabic that belongs to one English inflection.
    /// </summary>
    /// <remarks>
    /// The mapping the product owner asked for, stated once:
    /// <code>
    ///   sell     → يَبِيع   (non-past)
    ///   sold     → بَاعَ     (past — the dictionary's own citation form)
    ///   selling  → بَيْع     (the مصدر, which is what an -ing form *is*)
    ///   books    → كِتَاب    (unchanged; Arabic plurals are broken and are not
    ///                        derivable, so the singular is shown with a label)
    /// </code>
    /// A form with nothing honest to show returns null and is not emitted.
    /// </remarks>
    private static string? ArabicFor(
        Inflections.Form form,
        WiktionarySource.WiktSense sense,
        WiktionarySource.ArabicVerb? paradigm,
        string baseArabic) => form switch
        {
            // The past of the English verb is the past of the Arabic one.
            Inflections.Form.Past or Inflections.Form.PastParticiple =>
                paradigm?.Past ?? sense.ArabicPast,

            // An -ing form names the action, which in Arabic is the مصدر. With
            // no مصدر recorded there is nothing to show but the verb itself.
            Inflections.Form.Progressive =>
                paradigm?.VerbalNoun ?? baseArabic,

            Inflections.Form.Plural => baseArabic,

            _ => null,
        };

    /// <summary>The column is <c>varchar(64)</c>.</summary>
    private const int MaxSenseId = 64;

    /// <summary>
    /// The id for one sense, or null when it would not fit.
    /// </summary>
    /// <remarks>
    /// Truncating would be worse than dropping: two long headwords sharing a
    /// prefix would be cut to the same id, and the second would silently
    /// overwrite the first's meaning. A word nobody can add is visible; a word
    /// showing another word's Arabic is not.
    /// </remarks>
    private static string? SenseId(string word, string pos, int index)
    {
        var id = $"wikt:{word}:{pos}:{index}";
        return id.Length <= MaxSenseId ? id : null;
    }

    private static string SourceFlags(
        WiktionarySource.WiktSense sense,
        WiktionarySource.ArabicVerb? paradigm,
        CefrLevel? level)
    {
        var flags = "en=wiktionary-2026;ar=wiktionary-2026";
        // Deliberately not "arform": the dry-run report splits provenance on
        // "form=" to list a word's inflections, and "arform=" matched it — so
        // every base row was printed as though it were an inflected one.
        if (paradigm?.NonPast is not null) flags += ";arverb=nonpast";
        flags += level is null ? ";cefr=none" : ";cefr=cefrj-1.5";
        return flags;
    }
}
