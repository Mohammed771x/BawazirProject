namespace WordOs.Application.Lexicon;

/// <summary>
/// Base-form candidates for a word as it appears in running text.
/// </summary>
/// <remarks>
/// A learner reading a passage taps <c>researching</c>, but the lexicon is keyed
/// on <c>research</c>. Something has to bridge that, and it is deliberately the
/// server: the client renders what it is told (rule R1), and a client that
/// guessed at base forms would produce a different dictionary per platform.
///
/// This is a candidate <i>generator</i>, not a stemmer. It proposes spellings
/// and the caller confirms each one against the lexicon, so a wrong guess costs
/// a miss rather than a wrong definition — <c>bring</c> never resolves to
/// <c>br</c>, because <c>br</c> is not a word.
///
/// Order matters: the exact spelling always comes first, so a word that really
/// exists as written is never replaced by a de-inflected neighbour
/// (<c>bus</c> must not become <c>bu</c>, <c>ring</c> must not become <c>r</c>).
/// </remarks>
public static class SurfaceForms
{
    private const string Vowels = "aeiou";

    /// <summary>
    /// Returns the spellings worth looking up, most likely first, without
    /// duplicates.
    /// </summary>
    public static IReadOnlyList<string> CandidatesFor(string word)
    {
        var w = (word ?? string.Empty).Trim().ToLowerInvariant();
        if (w.Length == 0) return [];

        var results = new List<string> { w };

        void Add(string candidate)
        {
            // Two letters is the shortest real English headword. Anything
            // shorter is stemming damage rather than a word.
            if (candidate.Length >= 2 && !results.Contains(candidate))
                results.Add(candidate);
        }

        // Rules cannot reach the irregular forms, and those are exactly the
        // commonest words in the language: a learner typing "went" or
        // "children" is not typing an edge case.
        if (Irregular.TryGetValue(w, out var baseForm)) Add(baseForm);

        if (w.EndsWith("ies") && w.Length > 4) Add(w[..^3] + "y");
        if (w.EndsWith("ied") && w.Length > 4) Add(w[..^3] + "y");

        if (w.EndsWith("es") && w.Length > 3)
        {
            Add(w[..^2]);   // boxes → box
            Add(w[..^1]);   // makes → make
        }
        else if (w.EndsWith('s') && !w.EndsWith("ss") && w.Length > 3)
        {
            Add(w[..^1]);   // words → word
        }

        if (w.EndsWith("ed") && w.Length > 3)
        {
            Add(w[..^2]);           // walked → walk
            Add(w[..^1]);           // liked  → like
            Add(Undouble(w[..^2])); // stopped → stop
        }

        if (w.EndsWith("ing") && w.Length > 4)
        {
            Add(w[..^3]);            // reading → read
            Add(w[..^3] + "e");      // making  → make
            Add(Undouble(w[..^3]));  // running → run
        }

        // Comparatives and adverbs, which read as ordinary vocabulary to a
        // learner even though they are inflections.
        if (w.EndsWith("er") && w.Length > 4) Add(w[..^2]);
        if (w.EndsWith("est") && w.Length > 5) Add(w[..^3]);
        if (w.EndsWith("ly") && w.Length > 4) Add(w[..^2]);

        return results;
    }

    /// <summary>
    /// Irregular forms and the word they belong to.
    /// </summary>
    /// <remarks>
    /// English irregulars are a closed list — the language stopped adding to it
    /// centuries ago — so it can simply be written down, and every entry here
    /// is a word a beginner meets in their first months. Regular forms are left
    /// to the rules above; nothing that a rule already handles is repeated.
    /// </remarks>
    private static readonly Dictionary<string, string> Irregular = new()
    {
        // Verbs: past and past participle, where they differ from the base.
        ["was"] = "be", ["were"] = "be", ["been"] = "be", ["am"] = "be",
        ["is"] = "be", ["are"] = "be",
        ["went"] = "go", ["gone"] = "go",
        ["did"] = "do", ["done"] = "do",
        ["had"] = "have", ["has"] = "have",
        ["said"] = "say", ["made"] = "make", ["came"] = "come",
        ["took"] = "take", ["taken"] = "take",
        ["saw"] = "see", ["seen"] = "see",
        ["knew"] = "know", ["known"] = "know",
        ["got"] = "get", ["gotten"] = "get",
        ["gave"] = "give", ["given"] = "give",
        ["found"] = "find", ["thought"] = "think", ["told"] = "tell",
        ["became"] = "become", ["left"] = "leave", ["felt"] = "feel",
        ["brought"] = "bring", ["began"] = "begin",
        ["begun"] = "begin", ["kept"] = "keep", ["held"] = "hold",
        ["wrote"] = "write", ["written"] = "write",
        ["stood"] = "stand", ["heard"] = "hear",
        ["meant"] = "mean", ["met"] = "meet",
        ["ran"] = "run", ["paid"] = "pay", ["sat"] = "sit",
        ["spoke"] = "speak", ["spoken"] = "speak",
        ["lay"] = "lie", ["led"] = "lead", ["grew"] = "grow",
        ["grown"] = "grow", ["lost"] = "lose", ["fell"] = "fall",
        ["fallen"] = "fall", ["sent"] = "send", ["built"] = "build",
        ["understood"] = "understand", ["drew"] = "draw", ["drawn"] = "draw",
        ["broke"] = "break", ["broken"] = "break", ["spent"] = "spend",
        ["rose"] = "rise", ["risen"] = "rise",
        ["drove"] = "drive", ["driven"] = "drive", ["bought"] = "buy",
        ["wore"] = "wear", ["worn"] = "wear", ["chose"] = "choose",
        ["chosen"] = "choose", ["ate"] = "eat", ["eaten"] = "eat",
        ["sold"] = "sell", ["taught"] = "teach", ["caught"] = "catch",
        ["fought"] = "fight", ["threw"] = "throw", ["thrown"] = "throw",
        ["flew"] = "fly", ["flown"] = "fly", ["slept"] = "sleep",
        ["swam"] = "swim",
        ["swum"] = "swim", ["sang"] = "sing", ["sung"] = "sing",
        ["drank"] = "drink", ["drunk"] = "drink", ["forgot"] = "forget",
        ["forgotten"] = "forget", ["knelt"] = "kneel", ["won"] = "win",
        ["shone"] = "shine", ["shot"] = "shoot", ["hid"] = "hide",
        ["hidden"] = "hide", ["woke"] = "wake", ["woken"] = "wake",

        // Plurals that are not formed with -s.
        ["children"] = "child", ["men"] = "man", ["women"] = "woman",
        ["people"] = "person", ["teeth"] = "tooth", ["feet"] = "foot",
        ["mice"] = "mouse", ["geese"] = "goose", ["lives"] = "life",
        ["knives"] = "knife", ["wives"] = "wife", ["leaves"] = "leaf",
        ["wolves"] = "wolf", ["shelves"] = "shelf", ["halves"] = "half",
        ["thieves"] = "thief", ["loaves"] = "loaf",

        // Comparatives with no shared stem.
        ["better"] = "good", ["best"] = "good",
        ["worse"] = "bad", ["worst"] = "bad",
    };

    /// <summary>
    /// Collapses a doubled final consonant: <c>stopp</c> → <c>stop</c>. Only
    /// applies after a vowel, so <c>fall</c> survives intact.
    /// </summary>
    private static string Undouble(string stem)
    {
        if (stem.Length < 3) return stem;
        var last = stem[^1];
        if (last != stem[^2]) return stem;
        if (Vowels.Contains(last)) return stem;
        if (!Vowels.Contains(stem[^3])) return stem;
        return stem[..^1];
    }
}
