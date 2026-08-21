using System.Text.Json;
using System.Xml;
using WordOs.Domain.Common;

namespace WordOs.LexiconImporter;

/// <summary>
/// Reads the three raw datasets. Parsing only — no database, no joining, so
/// each source can be tested in isolation.
/// </summary>
public static class LexiconSources
{
    /// <summary>One synset from Open English WordNet.</summary>
    public sealed record OewnSynset(
        string SynsetId,
        string PartOfSpeech,
        string Definition,
        IReadOnlyList<string> Members,
        string? Ili);

    /// <summary>One (word, sense) pair — the grain of the lexicon.</summary>
    public sealed record OewnSense(
        string Word,
        string PartOfSpeech,
        string SenseId,
        string SynsetId);

    // ── CEFR-J + Octanove ────────────────────────────────────────────────────

    /// <summary>
    /// Maps <c>(headword, pos)</c> to a CEFR band.
    /// </summary>
    /// <remarks>
    /// A headword can appear under several parts of speech at different levels
    /// (<c>book</c> the noun is A1, the verb A2), which is exactly the
    /// distinction the product needs, so the key includes the POS. Entries like
    /// <c>a.m./A.M./am/AM</c> list variants separated by <c>/</c>; each variant
    /// is indexed so a learner typing any of them is recognised.
    /// </remarks>
    public static Dictionary<(string Word, string Pos), CefrLevel> ReadCefr(
        string cefrjCsv,
        string octanoveCsv)
    {
        var map = new Dictionary<(string, string), CefrLevel>();

        void Load(string path, bool required)
        {
            if (!File.Exists(path))
            {
                if (required) throw new FileNotFoundException($"Missing {path}");
                return;
            }

            foreach (var row in ReadCsv(path))
            {
                if (!row.TryGetValue("headword", out var headword)) continue;
                if (!row.TryGetValue("CEFR", out var band)) continue;
                row.TryGetValue("pos", out var pos);

                var level = ParseCefr(band);
                if (level is null) continue;

                foreach (var variant in headword.Split('/',
                             StringSplitOptions.RemoveEmptyEntries |
                             StringSplitOptions.TrimEntries))
                {
                    var key = (variant.ToLowerInvariant(), NormalisePos(pos ?? ""));
                    // Keep the lowest band seen: if a word is introduced at A1
                    // it is an A1 word, whatever a later list says.
                    if (!map.TryGetValue(key, out var existing) ||
                        level.Value.Rank() < existing.Rank())
                    {
                        map[key] = level.Value;
                    }
                }
            }
        }

        Load(cefrjCsv, required: true);
        Load(octanoveCsv, required: false);
        return map;
    }

    private static CefrLevel? ParseCefr(string raw) =>
        raw.Trim().ToUpperInvariant() switch
        {
            "A1" => CefrLevel.A1,
            "A2" => CefrLevel.A2,
            "B1" => CefrLevel.B1,
            "B2" => CefrLevel.B2,
            "C1" => CefrLevel.C1,
            "C2" => CefrLevel.C2,
            _ => null,
        };

    /// <summary>Maps both vocabularies' POS names onto WordNet's letters.</summary>
    public static string NormalisePos(string pos) =>
        pos.Trim().ToLowerInvariant() switch
        {
            "noun" or "n" => "n",
            "verb" or "v" => "v",
            "adjective" or "adj" or "a" or "s" => "a",
            "adverb" or "adv" or "r" => "r",
            _ => "",
        };

    /// <summary>
    /// A minimal RFC-4180 reader: quoted fields, embedded commas, doubled
    /// quotes. Written rather than pulled in as a dependency because the two
    /// files are small and the format they use is this narrow.
    /// </summary>
    private static IEnumerable<Dictionary<string, string>> ReadCsv(string path)
    {
        using var reader = new StreamReader(path);
        var header = ParseLine(reader);
        if (header is null) yield break;

        while (true)
        {
            var fields = ParseLine(reader);
            if (fields is null) break;

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Count && i < fields.Count; i++)
                row[header[i].Trim()] = fields[i].Trim();
            yield return row;
        }
    }

    private static List<string>? ParseLine(StreamReader reader)
    {
        var line = reader.ReadLine();
        if (line is null) return null;

        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }

        fields.Add(current.ToString());
        return fields;
    }

    // ── Open English WordNet ─────────────────────────────────────────────────

    /// <summary>Reads every synset from the <c>&lt;pos&gt;.&lt;class&gt;.json</c> files.</summary>
    public static Dictionary<string, OewnSynset> ReadOewnSynsets(string oewnDir)
    {
        var synsets = new Dictionary<string, OewnSynset>();

        foreach (var path in Directory.EnumerateFiles(oewnDir, "*.json"))
        {
            if (Path.GetFileName(path).StartsWith("entries-", StringComparison.Ordinal))
                continue;

            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var value = entry.Value;
                if (value.ValueKind != JsonValueKind.Object) continue;

                var definition = value.TryGetProperty("definition", out var defs)
                                 && defs.ValueKind == JsonValueKind.Array
                                 && defs.GetArrayLength() > 0
                    ? defs[0].GetString() ?? ""
                    : "";

                var members = new List<string>();
                if (value.TryGetProperty("members", out var mem)
                    && mem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in mem.EnumerateArray())
                        if (m.GetString() is { } s) members.Add(s);
                }

                var pos = value.TryGetProperty("partOfSpeech", out var p)
                    ? p.GetString() ?? ""
                    : "";

                var ili = value.TryGetProperty("ili", out var i) ? i.GetString() : null;

                synsets[entry.Name] =
                    new OewnSynset(entry.Name, pos, definition, members, ili);
            }
        }

        return synsets;
    }

    /// <summary>
    /// Reads every (word, sense, synset) triple from the
    /// <c>entries-*.json</c> files.
    /// </summary>
    /// <summary>
    /// The inflected forms Open English WordNet records, by word and part of
    /// speech.
    /// </summary>
    /// <remarks>
    /// It lists a form only when its spelling is not the plain rule —
    /// <c>went</c>, <c>swimming</c>, <c>mice</c> — and lists nothing for
    /// <c>walk</c> or <c>book</c>. That silence is information: it says the
    /// forms are regular, which is exactly the distinction the product draws
    /// between a form worth learning and a word with an <c>s</c> on the end
    /// (ADR-045).
    /// </remarks>
    public static Dictionary<(string Word, string Pos), List<string>> ReadOewnForms(
        string oewnDir)
    {
        var forms = new Dictionary<(string, string), List<string>>();

        foreach (var path in Directory.EnumerateFiles(oewnDir, "entries-*.json"))
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);

            foreach (var wordEntry in doc.RootElement.EnumerateObject())
            {
                if (wordEntry.Value.ValueKind != JsonValueKind.Object) continue;

                foreach (var posEntry in wordEntry.Value.EnumerateObject())
                {
                    if (posEntry.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!posEntry.Value.TryGetProperty("form", out var formArray)
                        || formArray.ValueKind != JsonValueKind.Array) continue;

                    var listed = formArray.EnumerateArray()
                        .Select(f => f.GetString())
                        .Where(f => !string.IsNullOrWhiteSpace(f))
                        .Select(f => f!.Replace('_', ' ').Trim())
                        .ToList();

                    if (listed.Count > 0)
                        forms[(wordEntry.Name, posEntry.Name)] = listed;
                }
            }
        }

        return forms;
    }

    public static List<OewnSense> ReadOewnSenses(string oewnDir)
    {
        var senses = new List<OewnSense>();

        foreach (var path in Directory.EnumerateFiles(oewnDir, "entries-*.json"))
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);

            // Shape: { "word": { "pos": { "sense": [ { id, synset } ] } } }
            foreach (var wordEntry in doc.RootElement.EnumerateObject())
            {
                if (wordEntry.Value.ValueKind != JsonValueKind.Object) continue;

                foreach (var posEntry in wordEntry.Value.EnumerateObject())
                {
                    if (posEntry.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!posEntry.Value.TryGetProperty("sense", out var senseArray)
                        || senseArray.ValueKind != JsonValueKind.Array) continue;

                    foreach (var sense in senseArray.EnumerateArray())
                    {
                        var senseId = sense.TryGetProperty("id", out var sid)
                            ? sid.GetString() : null;
                        var synsetId = sense.TryGetProperty("synset", out var syn)
                            ? syn.GetString() : null;

                        if (senseId is null || synsetId is null) continue;

                        senses.Add(new OewnSense(
                            wordEntry.Name, posEntry.Name, senseId, synsetId));
                    }
                }
            }
        }

        return senses;
    }

    // ── Arabic WordNet ───────────────────────────────────────────────────────

    /// <summary>
    /// Maps an OEWN synset id to its Arabic lemmas.
    /// </summary>
    /// <remarks>
    /// AWN is a WN-LMF file of ~75 MB, so it is read with a streaming
    /// <see cref="XmlReader"/> rather than loaded into a DOM. Its synset ids
    /// carry an <c>awn4-</c> prefix over the OEWN id, which is the join key.
    /// </remarks>
    public static Dictionary<string, List<string>> ReadArabicBySynset(string awnXml)
    {
        var bySynset = new Dictionary<string, List<string>>();

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,   // never fetch the external DTD
            XmlResolver = null,                      // and never resolve entities
            IgnoreWhitespace = true,
            IgnoreComments = true,
        };

        using var reader = XmlReader.Create(awnXml, settings);

        string? currentLemma = null;

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;

            switch (reader.Name)
            {
                case "LexicalEntry":
                    currentLemma = null;
                    break;

                case "Lemma":
                    currentLemma = reader.GetAttribute("writtenForm");
                    break;

                case "Sense":
                {
                    var synset = reader.GetAttribute("synset");
                    if (currentLemma is null || synset is null) break;

                    var key = synset.StartsWith("awn4-", StringComparison.Ordinal)
                        ? synset["awn4-".Length..]
                        : synset;

                    var lemma = currentLemma.Trim();
                    if (lemma.Length == 0) break;

                    if (!bySynset.TryGetValue(key, out var list))
                        bySynset[key] = list = [];
                    if (!list.Contains(lemma)) list.Add(lemma);
                    break;
                }
            }
        }

        return bySynset;
    }
}
