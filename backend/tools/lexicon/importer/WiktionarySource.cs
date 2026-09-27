using System.Text;
using System.Text.Json;

namespace WordOs.LexiconImporter;

/// <summary>
/// Reads the two wiktextract exports: English (senses, definitions, examples,
/// Arabic translations) and Arabic (the verb forms).
/// </summary>
/// <remarks>
/// Why this exists at all is ADR-096. The short version: the old lexicon put
/// <c>تُوُفِّيَ</c> at the top of <c>go</c> and <c>أَقْنَعَ بِـ</c> at the top of
/// <c>sell</c>, because WordNet's sense order is not a frequency order and the
/// rank column was a proxy invented here. Wiktionary's senses are ordered by
/// people, and its translations are attached to the sense they belong to.
///
/// <para>Both files are streamed. The English export is 3 GB and is never held
/// in memory — each line is a complete JSON object, filtered and discarded.</para>
/// </remarks>
public static class WiktionarySource
{
    /// <summary>One sense of one English word, with the Arabic that belongs to it.</summary>
    public sealed record WiktSense(
        string Word,
        string PartOfSpeech,
        string Definition,
        string? Example,
        string ArabicPast,
        string? ArabicRoman,
        int SenseIndex,
        IReadOnlyList<string> EnglishForms,
        int EntryIndex);

    /// <summary>
    /// The forms of an Arabic verb, from its own Wiktionary entry.
    /// </summary>
    /// <remarks>
    /// This is what answers the product owner's complaint. Arabic dictionaries
    /// cite a verb in the past — <c>باع</c> — which against the English
    /// infinitive <c>to sell</c> reads as "he sold". The Arabic entry carries
    /// the rest of the paradigm, so the form shown can follow the *English*
    /// inflection instead: <c>sell → يبيع</c>, <c>sold → باع</c>,
    /// <c>selling → بَيْع</c>.
    /// </remarks>
    public sealed record ArabicVerb(
        string Past,
        string? NonPast,
        string? VerbalNoun,
        string? ActiveParticiple);

    // Arabic letters, and the marks that sit on them.
    private const string Harakat = "ًٌٍَُِّْٰ";

    /// <summary>
    /// Dialect names, wherever they appear.
    /// </summary>
    /// <remarks>
    /// A learner studying for a CEFR band is studying فصحى. Wiktionary marks
    /// most dialect translations with a tag, but not all of them — one entry
    /// for <c>eat</c> arrives as the bare string
    /// <c>"Tunisian Arabic كلِا"</c>, with the dialect inside the word — so the
    /// name is matched in both places.
    /// </remarks>
    private static readonly string[] Dialects =
    [
        "Egyptian", "Gulf", "Tunisian", "Moroccan", "Levantine", "Hijazi",
        "Iraqi", "Algerian", "Libyan", "Sudanese", "Yemeni", "Najdi",
        "South Levantine", "North Levantine", "Maltese", "Andalusi", "Chadian",
    ];

    public static bool IsArabicScript(string s) =>
        s.Any(c => c >= '؀' && c <= 'ۿ');

    private static bool HasLatin(string s) =>
        s.Any(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));

    public static string StripHarakat(string s) =>
        new(s.Where(c => !Harakat.Contains(c)).ToArray());

    private static bool NamesADialect(string s) =>
        Dialects.Any(d => s.Contains(d, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Cleans one Arabic translation, or rejects it.
    /// </summary>
    /// <remarks>
    /// Returns the past form and, when Wiktionary happened to inline it, the
    /// non-past: some entries arrive as
    /// <c>"أَكَلَ imperfective: يَأْكُلُ"</c> — two forms in one string, which
    /// stored verbatim would show the learner a sentence instead of a word.
    /// </remarks>
    public static (string Past, string? NonPast)? CleanTranslation(
        string? word,
        IEnumerable<string>? tags)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        if (tags is not null && tags.Any(NamesADialect)) return null;

        var text = word.Trim();
        if (NamesADialect(text)) return null;
        if (!IsArabicScript(text)) return null;

        string? nonPast = null;
        var marker = text.IndexOf("imperfective:", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            nonPast = text[(marker + "imperfective:".Length)..].Trim();
            text = text[..marker].Trim();
            if (!IsArabicScript(nonPast) || HasLatin(nonPast)) nonPast = null;
        }

        // Anything still carrying Latin script is an annotation that leaked
        // into the field, not a word a learner should be shown.
        if (text.Length == 0 || HasLatin(text)) return null;

        return (text, nonPast);
    }

    // ── The Arabic export: verb paradigms ────────────────────────────────────

    /// <summary>
    /// Every Arabic verb, keyed by its past form with the vowel marks removed.
    /// </summary>
    /// <remarks>
    /// Undiacritised because the two exports do not agree on marks: the English
    /// side gives <c>بَاعَ</c> and the Arabic side's headword is <c>باع</c>.
    /// Matching on the bare letters is what makes the join hit at all.
    /// </remarks>
    public static Dictionary<string, ArabicVerb> ReadArabicVerbs(string arabicJsonl)
    {
        var verbs = new Dictionary<string, ArabicVerb>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(arabicJsonl))
        {
            if (line.Length == 0) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("pos", out var pos)
                    || pos.GetString() != "verb") continue;
                if (!root.TryGetProperty("word", out var w)) continue;

                var headword = w.GetString();
                if (string.IsNullOrWhiteSpace(headword)) continue;

                string? past = null, nonPast = null, verbalNoun = null, participle = null;

                if (root.TryGetProperty("forms", out var forms)
                    && forms.ValueKind == JsonValueKind.Array)
                {
                    foreach (var form in forms.EnumerateArray())
                    {
                        var text = form.TryGetProperty("form", out var f)
                            ? f.GetString() : null;
                        if (string.IsNullOrWhiteSpace(text) || !IsArabicScript(text))
                            continue;

                        var tags = form.TryGetProperty("tags", out var t)
                                   && t.ValueKind == JsonValueKind.Array
                            ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                            : [];

                        if (tags.Contains("canonical")) past ??= text;
                        else if (tags.Contains("non-past")) nonPast ??= text;
                        else if (tags.Contains("noun-from-verb")) verbalNoun ??= text;
                        else if (tags.Contains("participle") && tags.Contains("active"))
                            participle ??= text;
                    }
                }

                // The headword is the fallback, and it is not always Arabic:
                // the Arabic export carries entries whose headword is an
                // English phrase — "have sex" is one — and unchecked that
                // becomes a learner's Arabic meaning. Found by a test against
                // the real file, not by reading the format.
                past ??= headword;
                if (!IsArabicScript(past) || HasLatin(past)) continue;

                var key = StripHarakat(past).Trim();
                if (key.Length == 0) continue;

                // First entry wins: Wiktionary lists the commonest sense of a
                // spelling first, and a later homograph must not overwrite it.
                if (!verbs.ContainsKey(key))
                    verbs[key] = new ArabicVerb(past, nonPast, verbalNoun, participle);
            }
        }

        return verbs;
    }

    // ── The English export: senses and their Arabic ──────────────────────────

    /// <summary>Parts of speech a learner adds as vocabulary.</summary>
    private static readonly Dictionary<string, string> KeptPos = new(StringComparer.Ordinal)
    {
        ["noun"] = "n",
        ["verb"] = "v",
        ["adj"] = "a",
        ["adv"] = "r",
    };

    /// <summary>
    /// Streams the English export, yielding the senses that carry Arabic.
    /// </summary>
    /// <remarks>
    /// A sense without an Arabic gloss is dropped rather than stored empty —
    /// the same rule the first lexicon used, and for the same reason: a row the
    /// learner can never be shown the meaning of is not a vocabulary item.
    ///
    /// <para>Because only the senses worth translating carry translations, this
    /// filter is also what keeps <c>run</c> from arriving with 118 senses.</para>
    /// </remarks>
    public static IEnumerable<WiktSense> ReadEnglishSenses(string englishJsonl)
    {
        // Which block of the word's page this is. Wiktionary puts the part of
        // speech people mean first: `go` is the verb, then the noun, then the
        // adjective, and only fourth the board game. Without this the board
        // game won on a tie, and `go` meant غُو.
        var lastWord = "";
        var entryIndex = 0;

        foreach (var line in File.ReadLines(englishJsonl))
        {
            if (line.Length == 0) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (doc)
            {
                var root = doc.RootElement;

                if (!root.TryGetProperty("pos", out var posEl)) continue;
                var posRaw = posEl.GetString() ?? "";
                if (!KeptPos.TryGetValue(posRaw, out var pos)) continue;

                var word = root.TryGetProperty("word", out var w) ? w.GetString() : null;
                if (string.IsNullOrWhiteSpace(word)) continue;
                if (HasLatin(word) is false) continue;          // not an English headword
                if (word.Count(c => c == ' ') > 2) continue;    // not one vocabulary item

                // Entry-level translations are the fallback now, not the
                // source, so an entry without them is still worth reading.
                var hasEntryTranslations =
                    root.TryGetProperty("translations", out var translations)
                    && translations.ValueKind == JsonValueKind.Array;

                // Arabic, in the order Wiktionary lists it, keyed by the sense
                // label it was filed under.
                var arabicBySense = new List<(string Sense, string Past, string? NonPast, string? Roman)>();
                if (hasEntryTranslations)
                {
                    foreach (var t in translations.EnumerateArray())
                    {
                        if (!t.TryGetProperty("lang_code", out var lc)
                            || lc.GetString() != "ar") continue;

                        var tags = t.TryGetProperty("tags", out var tg)
                                   && tg.ValueKind == JsonValueKind.Array
                            ? tg.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                            : [];

                        var cleaned = CleanTranslation(
                            t.TryGetProperty("word", out var tw) ? tw.GetString() : null,
                            tags);
                        if (cleaned is null) continue;

                        arabicBySense.Add((
                            t.TryGetProperty("sense", out var s) ? s.GetString() ?? "" : "",
                            cleaned.Value.Past,
                            cleaned.Value.NonPast,
                            t.TryGetProperty("roman", out var r) ? r.GetString() : null));
                    }
                }

                if (!root.TryGetProperty("senses", out var senses)
                    || senses.ValueKind != JsonValueKind.Array) continue;

                if (word.Equals(lastWord, StringComparison.Ordinal))
                {
                    entryIndex++;
                }
                else
                {
                    lastWord = word;
                    entryIndex = 0;
                }

                // The word's own inflections, from the same entry.
                //
                // The first run of this importer had no form list at all and
                // fell through to the regular spelling rules, which produced
                // "taked", "runed" and "wining". Wiktionary records the real
                // ones; `Inflections` works out which is which, exactly as it
                // did from WordNet's list.
                var englishForms = ReadEnglishForms(root, word);

                var index = 0;
                var used = new HashSet<string>(StringComparer.Ordinal);

                foreach (var sense in senses.EnumerateArray())
                {
                    var gloss = sense.TryGetProperty("glosses", out var g)
                                && g.ValueKind == JsonValueKind.Array
                                && g.GetArrayLength() > 0
                        ? g[0].GetString() : null;
                    if (string.IsNullOrWhiteSpace(gloss)) continue;

                    // The sense's own translations first. Most of this export
                    // files them here rather than on the entry — by an order of
                    // magnitude — and when it does they are already attached to
                    // the meaning they belong to, so nothing has to be matched
                    // and nothing can be mismatched.
                    var match = ArabicOn(sense) ?? BestArabicFor(gloss, arabicBySense, index);
                    if (match is null) continue;
                    // One Arabic gloss per word: two senses that translate the
                    // same way are one vocabulary item, not a choice between
                    // identical-looking rows.
                    if (!used.Add(match.Value.Past)) continue;

                    string? example = null;
                    if (sense.TryGetProperty("examples", out var ex)
                        && ex.ValueKind == JsonValueKind.Array && ex.GetArrayLength() > 0
                        && ex[0].TryGetProperty("text", out var exText))
                    {
                        example = exText.GetString();
                    }

                    yield return new WiktSense(
                        word.Trim(), pos, gloss.Trim(), example,
                        match.Value.Past, match.Value.Roman, index, englishForms,
                        entryIndex);

                    index++;
                }
            }
        }
    }

    /// <summary>Forms Wiktionary records but nobody should be taught.</summary>
    private static readonly HashSet<string> NotTaught = new(StringComparer.OrdinalIgnoreCase)
    {
        "archaic", "obsolete", "rare", "dialectal", "nonstandard", "informal",
        "dated", "poetic", "humorous", "proscribed", "colloquial", "slang",
    };

    /// <summary>Tags that mark a form as an inflection worth teaching.</summary>
    private static readonly string[] InflectionTags =
        ["past", "participle", "plural", "present"];

    /// <summary>
    /// The inflected spellings listed on an English entry.
    /// </summary>
    /// <remarks>
    /// Comparatives, superlatives and the entry's own headword are left out:
    /// the first two are not what <see cref="Inflections"/> is reading the list
    /// for, and the third would be offered to the learner as a form of itself.
    /// </remarks>
    private static List<string> ReadEnglishForms(JsonElement root, string word)
    {
        var forms = new List<string>();

        if (!root.TryGetProperty("forms", out var listed)
            || listed.ValueKind != JsonValueKind.Array) return forms;

        foreach (var f in listed.EnumerateArray())
        {
            var text = f.TryGetProperty("form", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (text.Equals(word, StringComparison.OrdinalIgnoreCase)) continue;
            if (!text.All(c => char.IsLetter(c) || c == '-' || c == '\'')) continue;

            if (!f.TryGetProperty("tags", out var tags)
                || tags.ValueKind != JsonValueKind.Array) continue;

            var names = tags.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            if (names.Contains("comparative") || names.Contains("superlative")) continue;
            // `readen`, `putten`, `costed` — all real, all listed, none of them
            // what anyone writes. Found in the dry-run report.
            if (names.Any(n => NotTaught.Contains(n))) continue;
            if (!names.Any(n => InflectionTags.Contains(n))) continue;

            if (!forms.Contains(text, StringComparer.OrdinalIgnoreCase))
                forms.Add(text.Trim());
        }

        return forms;
    }

    /// <summary>
    /// The Arabic attached directly to one sense, if it carries any.
    /// </summary>
    /// <remarks>
    /// The reliable path. Reading only the entry-level table cost nine tenths
    /// of the dictionary and forced every remaining row through a similarity
    /// match — found by the dry run reporting 1,951 senses where tens of
    /// thousands were expected.
    /// </remarks>
    private static (string Past, string? NonPast, string? Roman)? ArabicOn(
        JsonElement sense)
    {
        if (!sense.TryGetProperty("translations", out var translations)
            || translations.ValueKind != JsonValueKind.Array) return null;

        foreach (var t in translations.EnumerateArray())
        {
            if (!t.TryGetProperty("lang_code", out var lc)
                || lc.GetString() != "ar") continue;

            var tags = t.TryGetProperty("tags", out var tg)
                       && tg.ValueKind == JsonValueKind.Array
                ? tg.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                : [];

            var cleaned = CleanTranslation(
                t.TryGetProperty("word", out var tw) ? tw.GetString() : null, tags);
            if (cleaned is null) continue;

            // The first one listed: Wiktionary puts the ordinary word before
            // the regional and the literary.
            return (cleaned.Value.Past, cleaned.Value.NonPast,
                    t.TryGetProperty("roman", out var r) ? r.GetString() : null);
        }

        return null;
    }

    /// <summary>
    /// Picks the Arabic filed under this sense.
    /// </summary>
    /// <remarks>
    /// Wiktionary's translation tables are labelled with a short paraphrase of
    /// the sense rather than the gloss itself — <c>sell</c>'s first gloss is
    /// "To transfer goods or provide services in exchange for money" and its
    /// translation sits under "to agree to transfer goods or provide services
    /// for payment". So the label is matched on shared content words, and the
    /// position is used only to break a tie.
    /// </remarks>
    private static (string Past, string? NonPast, string? Roman)? BestArabicFor(
        string gloss,
        List<(string Sense, string Past, string? NonPast, string? Roman)> candidates,
        int senseIndex)
    {
        if (candidates.Count == 0) return null;

        var glossWords = ContentWords(gloss);
        var bestScore = 0.0;
        (string, string?, string?)? best = null;

        foreach (var c in candidates)
        {
            var senseWords = ContentWords(c.Sense);
            if (senseWords.Count == 0) continue;

            var shared = glossWords.Count(senseWords.Contains);
            var score = shared / (double)Math.Max(glossWords.Count, senseWords.Count);
            if (score > bestScore)
            {
                bestScore = score;
                best = (c.Past, c.NonPast, c.Roman);
            }
        }

        // Nothing overlapped. For the first sense that is still safe — the
        // first translation block belongs to the first sense — but for a later
        // one it would be a guess, and a wrong meaning is worse than no row.
        if (best is null)
        {
            if (senseIndex > 0) return null;
            var first = candidates[0];
            return (first.Past, first.NonPast, first.Roman);
        }

        return bestScore < 0.15 && senseIndex > 0 ? null : best;
    }

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "to", "a", "an", "the", "of", "or", "and", "in", "on", "for", "with",
        "as", "by", "that", "which", "be", "is", "are", "it", "its", "something",
        "someone", "somebody", "see", "also", "especially", "esp",
    };

    private static List<string> ContentWords(string s)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsLetter(c)) current.Append(char.ToLowerInvariant(c));
            else if (current.Length > 0) { Take(); }
        }
        if (current.Length > 0) Take();
        return words;

        void Take()
        {
            var w = current.ToString();
            current.Clear();
            if (w.Length > 2 && !Stop.Contains(w)) words.Add(w);
        }
    }
}
