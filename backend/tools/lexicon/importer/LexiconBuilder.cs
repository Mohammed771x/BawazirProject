using WordOs.Domain.Common;

namespace WordOs.LexiconImporter;

/// <summary>One row destined for <c>lexicon_entries</c>.</summary>
public sealed record LexiconRow(
    string SenseId,
    string Text,
    string TextNormalized,
    string Lemma,
    string PartOfSpeech,
    string DefinitionEn,
    string MeaningAr,
    CefrLevel? CefrLevel,
    int? FrequencyRank,
    string SourceFlags);

public sealed record BuildStats(
    int OewnSenses,
    int SynsetsWithArabic,
    int Emitted,
    int SkippedNoArabic,
    int SkippedNoSynset,
    int SkippedMultiword,
    int WithCefr,
    int CollapsedSynonymousSenses = 0);

/// <summary>
/// Joins the three sources into lexicon rows.
/// </summary>
/// <remarks>
/// The pipeline the product specified:
/// <code>
/// English word → synset → Arabic meaning (AWN) → CEFR level → PostgreSQL
/// </code>
///
/// The grain is the <b>sense</b>, not the word. <c>book</c> yields one row per
/// synset it belongs to, so <c>book = كتاب</c> and <c>book = يحجز</c> are
/// separate rows with separate ids — which is what makes them independent
/// vocabulary items downstream (ADR-012).
///
/// Nothing here calls an AI service. Arabic comes from Arabic WordNet as a
/// lexical dataset; the importer only reads and joins.
/// </remarks>
public static class LexiconBuilder
{
    public static (List<LexiconRow> Rows, BuildStats Stats) Build(
        IReadOnlyList<LexiconSources.OewnSense> senses,
        IReadOnlyDictionary<string, LexiconSources.OewnSynset> synsets,
        IReadOnlyDictionary<string, List<string>> arabicBySynset,
        IReadOnlyDictionary<(string Word, string Pos), CefrLevel> cefr,
        IReadOnlyDictionary<string, int>? frequencyRanks = null)
    {
        var rows = new List<LexiconRow>(senses.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        int noArabic = 0, noSynset = 0, multiword = 0, withCefr = 0;

        foreach (var sense in senses)
        {
            if (!synsets.TryGetValue(sense.SynsetId, out var synset))
            {
                noSynset++;
                continue;
            }

            if (!arabicBySynset.TryGetValue(sense.SynsetId, out var arabic)
                || arabic.Count == 0)
            {
                // No Arabic meaning means the learner could never be shown what
                // the word means, so the row is useless — dropped rather than
                // stored empty.
                noArabic++;
                continue;
            }

            // WordNet writes multi-word entries with underscores.
            var text = sense.Word.Replace('_', ' ').Trim();
            if (text.Length == 0) continue;

            // Keep single words and short phrases; a five-word idiom is not
            // something a learner adds as one vocabulary item.
            if (text.Count(c => c == ' ') > 2)
            {
                multiword++;
                continue;
            }

            // The sense id from OEWN is already unique per (word, sense).
            if (!seen.Add(sense.SenseId)) continue;

            var pos = NormalisePos(sense.PartOfSpeech, synset.PartOfSpeech);
            var normalized = text.ToLowerInvariant();

            // Level is looked up by (word, pos) so the noun and the verb can
            // legitimately differ; a POS-less match is the fallback.
            CefrLevel? level = null;
            if (cefr.TryGetValue((normalized, pos), out var exact)) level = exact;
            else if (cefr.TryGetValue((normalized, ""), out var any)) level = any;
            if (level is not null) withCefr++;

            int? rank = frequencyRanks?.TryGetValue(normalized, out var r) == true
                ? r
                : RankFor(level, senseOrder: SenseOrdinal(sense.SenseId));

            rows.Add(new LexiconRow(
                SenseId: sense.SenseId,
                Text: text,
                TextNormalized: normalized,
                Lemma: text,
                PartOfSpeech: pos,
                DefinitionEn: Truncate(synset.Definition, 2048),
                // The first Arabic lemma is the primary gloss; alternatives are
                // deliberately not concatenated, because a learner picks one
                // meaning and that choice must be unambiguous.
                MeaningAr: Truncate(arabic[0], 512),
                CefrLevel: level,
                FrequencyRank: rank,
                SourceFlags: BuildSourceFlags(level is not null)));
        }

        var deduped = Deduplicate(rows, out var collapsed);

        var stats = new BuildStats(
            OewnSenses: senses.Count,
            SynsetsWithArabic: arabicBySynset.Count,
            Emitted: deduped.Count,
            SkippedNoArabic: noArabic,
            SkippedNoSynset: noSynset,
            SkippedMultiword: multiword,
            WithCefr: deduped.Count(r => r.CefrLevel is not null),
            CollapsedSynonymousSenses: collapsed);

        return (deduped, stats);
    }

    /// <summary>
    /// Collapses senses that a learner could not tell apart.
    /// </summary>
    /// <remarks>
    /// English draws finer distinctions than the Arabic gloss preserves, so
    /// several synsets of <c>book</c> all come back as <c>كتاب</c>. Presenting
    /// them as separate options would ask the learner to choose between
    /// identical-looking rows, and then treat the choices as independent
    /// vocabulary items — which is worse than useless.
    ///
    /// One row is kept per <c>(word, part of speech, Arabic meaning)</c>,
    /// preferring the sense that carries a CEFR level, then the one with a
    /// definition, then the lowest sense id for determinism. Genuinely
    /// different meanings — <c>book = كتاب</c> vs <c>book = يحجز</c> — keep
    /// their own rows, which is the identity the product depends on.
    /// </remarks>
    private static List<LexiconRow> Deduplicate(
        List<LexiconRow> rows,
        out int collapsed)
    {
        var best = new Dictionary<(string, string, string), LexiconRow>();

        foreach (var row in rows)
        {
            var key = (row.TextNormalized, row.PartOfSpeech, row.MeaningAr);
            if (!best.TryGetValue(key, out var current))
            {
                best[key] = row;
                continue;
            }

            if (Preference(row) > Preference(current) ||
                (Preference(row) == Preference(current) &&
                 string.CompareOrdinal(row.SenseId, current.SenseId) < 0))
            {
                best[key] = row;
            }
        }

        collapsed = rows.Count - best.Count;

        return best.Values
            .OrderBy(r => r.TextNormalized, StringComparer.Ordinal)
            .ThenBy(r => r.SenseId, StringComparer.Ordinal)
            .ToList();
    }

    private static int Preference(LexiconRow row)
    {
        var score = 0;
        if (row.CefrLevel is not null) score += 2;
        if (row.DefinitionEn.Length > 0) score += 1;
        return score;
    }

    /// <summary>
    /// A sort key for autocomplete: lower surfaces first.
    /// </summary>
    /// <remarks>
    /// No source here ships a corpus frequency list, so this is a deliberate
    /// proxy built from two signals that are available:
    ///
    /// <list type="number">
    /// <item>the CEFR band — a word taught at A1 is, by construction, one a
    /// learner meets early; unlevelled senses sort after every levelled one;</item>
    /// <item>WordNet's sense order within a word, which is already ordered by
    /// frequency of use.</item>
    /// </list>
    ///
    /// Without it, typing <c>bo</c> buries <c>book</c> under twenty senses of
    /// <c>board</c> in whatever order the table returns. Swap in a real
    /// frequency list later by passing <c>frequencyRanks</c>; nothing else
    /// changes.
    /// </remarks>
    private static int RankFor(CefrLevel? level, int senseOrder)
    {
        // Unlevelled senses start well beyond the levelled ones.
        var band = level?.Rank() ?? 20;
        return (band * 1000) + Math.Min(senseOrder, 999);
    }

    /// <summary>
    /// Extracts WordNet's sense number from a sense key such as
    /// <c>book%1:06:02::</c> — the trailing group is the lexical id, which
    /// increases with decreasing frequency.
    /// </summary>
    private static int SenseOrdinal(string senseId)
    {
        var percent = senseId.IndexOf('%');
        if (percent < 0) return 0;

        var parts = senseId[(percent + 1)..].Split(':');
        return parts.Length >= 3 && int.TryParse(parts[2], out var lexId)
            ? lexId
            : 0;
    }

    /// <summary>
    /// Which source supplied each field, recorded per row so an entry whose
    /// Arabic came from the machine-translated portion of AWN stays auditable
    /// and replaceable (ADR-012).
    /// </summary>
    private static string BuildSourceFlags(bool hasCefr) =>
        hasCefr
            ? "en=oewn-2025;ar=awn-4.0;cefr=cefrj-1.5"
            : "en=oewn-2025;ar=awn-4.0;cefr=none";

    private static string NormalisePos(string sensePos, string synsetPos)
    {
        var pos = LexiconSources.NormalisePos(sensePos);
        return pos.Length > 0 ? pos : LexiconSources.NormalisePos(synsetPos);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
