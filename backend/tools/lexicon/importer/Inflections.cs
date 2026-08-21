namespace WordOs.LexiconImporter;

/// <summary>
/// The forms of a word a learner meets in real English.
/// </summary>
/// <remarks>
/// A learner who has added <c>go</c> still has to recognise <c>went</c>,
/// <c>gone</c> and <c>going</c>, and they are not the same thing to learn: the
/// vocabulary is one word, the forms are four. WordNet is a lexicon of
/// *lemmas*, so it holds only <c>go</c> — which is why searching for
/// <c>went</c> found nothing to add (ADR-045).
///
/// Three sources of truth, in this order:
///
/// <list type="number">
/// <item><b>Open English WordNet's own <c>form</c> lists.</b> It records every
/// form whose spelling is not the plain rule — <c>went</c>, <c>gone</c>,
/// <c>swimming</c>, <c>studied</c>, <c>mice</c>, <c>children</c> — and records
/// nothing for the ones that are (<c>walk</c>, <c>book</c>). That distinction is
/// exactly the product rule: add the forms that look different, not the ones
/// that are the word with an <c>s</c> on the end.</item>
/// <item><b>Rules</b>, for what the dataset leaves out because it is regular:
/// <c>walked</c>, <c>walking</c>, <c>going</c>, <c>lunged</c>.</item>
/// <item><b>Two short authored lists</b>, for what neither covers: the verbs
/// whose past *is* the base (<c>read</c>, <c>cost</c>) — where a rule would
/// invent "readed" — and the handful of irregular plurals the dataset omits
/// (<c>women</c>, <c>people</c>).</item>
/// </list>
/// </remarks>
public static class Inflections
{
    /// <summary>Which form this is, for the label the learner sees.</summary>
    public enum Form
    {
        Past,
        PastParticiple,
        Progressive,
        Plural,
    }

    public sealed record Inflected(string Text, Form Form);

    /// <summary>
    /// Verbs whose past and participle are the word itself.
    /// </summary>
    /// <remarks>
    /// WordNet lists nothing for these, because there is no irregular spelling
    /// to record — and "nothing listed" otherwise means "regular", which would
    /// produce <c>readed</c> and <c>costed</c>. Ten words, and the class is
    /// closed.
    /// </remarks>
    private static readonly HashSet<string> Invariant = new(StringComparer.OrdinalIgnoreCase)
    {
        "broadcast", "burst", "cast", "cost", "forecast",
        "hurt", "read", "spread", "sweat", "thrust",
    };

    /// <summary>
    /// Verbs whose single listed form is the participle, the past being the
    /// word itself: <c>beat / beat / beaten</c>.
    /// </summary>
    /// <remarks>
    /// One listed form usually does both jobs — <c>said</c>, <c>felt</c>,
    /// <c>bought</c>, <c>walked</c>. These are where it does not, and the data
    /// cannot say so: the other form is the base, so there is nothing to list.
    /// Thirty-five verbs list a single form ending in <c>n</c>; these are the
    /// ones among them where that form is the participle alone.
    /// </remarks>
    private static readonly HashSet<string> ParticipleOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "beat", "browbeat", "hew", "rough-hew", "prove", "saw", "whipsaw",
        "sew", "oversew", "resew", "shew", "sow", "foreshow", "show", "mow",
        "gnaw", "grave", "lade", "melt", "rive", "shave", "strew", "bestrew",
        "swell", "outbid", "overbid", "overflow",
    };

    /// <summary>
    /// Verbs whose single listed form is the past, the participle being the
    /// word itself: <c>run / ran / run</c>.
    /// </summary>
    private static readonly HashSet<string> PastOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "run", "outrun", "overrun", "rerun",
    };

    /// <summary>Irregular plurals Open English WordNet does not carry.</summary>
    private static readonly Dictionary<string, string> MissingPlurals =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["woman"] = "women",
            ["person"] = "people",
            ["die"] = "dice",
        };

    /// <summary>
    /// Forms that already exist as authored closed-class words.
    /// </summary>
    /// <remarks>
    /// <c>is</c>, <c>was</c>, <c>had</c> and their family are written by hand
    /// with the meaning a learner needs (ADR-033). Emitting them again as
    /// inflections of <c>be</c>, <c>have</c> and <c>do</c> would put the same
    /// word on the screen twice with two different explanations.
    /// </remarks>
    private static readonly HashSet<string> AlreadyAuthored =
        FunctionWords.All
            .Select(e => e.Text)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The forms to add for one word, or none.
    /// </summary>
    /// <param name="text">The lemma, as it appears in the lexicon.</param>
    /// <param name="pos">Its part of speech — only verbs and nouns inflect here.</param>
    /// <param name="listed">
    /// What Open English WordNet records for this word, which is empty for a
    /// word whose forms are all regular.
    /// </param>
    public static List<Inflected> For(
        string text,
        string pos,
        IReadOnlyList<string> listed)
    {
        var word = text.Trim();

        // A phrase inflects on its head verb ("give up" → "gave up"), which is
        // more than a spelling rule can do honestly. Left alone.
        if (word.Length == 0 || word.Contains(' ')) return [];

        return pos switch
        {
            "v" => Verb(word, listed),
            "n" => Noun(word, listed),
            _ => [],
        };
    }

    private static List<Inflected> Verb(string word, IReadOnlyList<string> listed)
    {
        var forms = new List<Inflected>();

        void Add(string form, Form kind)
        {
            var candidate = form.Trim();
            if (candidate.Length == 0) return;
            if (string.Equals(candidate, word, StringComparison.OrdinalIgnoreCase)) return;
            if (AlreadyAuthored.Contains(candidate)) return;
            // Keyed on the form as well as the spelling: `walked` is both the
            // past and the participle, and the learner is entitled to practise
            // either (ADR-046).
            if (forms.Any(f => f.Form == kind &&
                               string.Equals(f.Text, candidate, StringComparison.OrdinalIgnoreCase)))
                return;

            forms.Add(new Inflected(candidate, kind));
        }

        var progressive = listed.FirstOrDefault(f =>
            f.EndsWith("ing", StringComparison.OrdinalIgnoreCase));

        // What is left after the -ing form is the past and the participle:
        // went and gone, swam and swum, studied on its own.
        //
        // Anything ending in `s` is the third-person present — `has`,
        // `degasses`, `quizzes`. WordNet lists those beside the pasts, and no
        // English past tense ends in `s`, so they go.
        var pastForms = listed
            .Where(f => !f.EndsWith("ing", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (pastForms.Count >= 2)
        {
            var participle = ParticipleAmong(pastForms);

            // Labelled only when it can be told which is which. The list
            // arrives alphabetically — `gone, went` — so taking the first as
            // the past would have taught that "gone" is the past tense of
            // "go", which is worse than not saying.
            foreach (var form in pastForms)
                Add(form, form == participle ? Form.PastParticiple : Form.Past);
        }
        else if (pastForms.Count == 1)
        {
            AddBothRoles(Add, word, pastForms[0]);
        }
        else if (listed.Count == 0 && !Invariant.Contains(word))
        {
            // Nothing listed at all means the forms are regular: `walked` is
            // the past and the participle both.
            AddBothRoles(Add, word, RegularPast(word));
        }

        Add(progressive ?? RegularProgressive(word), Form.Progressive);

        return forms;
    }

    /// <summary>
    /// Adds one spelling under both roles, or the single role it really has.
    /// </summary>
    /// <remarks>
    /// `played` is the past *and* the participle, and a learner is entitled to
    /// practise either — "I played" and "I have played" are two things to
    /// learn, even though they are one spelling (ADR-046). So the same word
    /// becomes two entries, each saying which it is.
    ///
    /// The exceptions are the verbs whose other form is the base itself, where
    /// claiming both would teach something false: `beaten` is not the past of
    /// `beat`, and `ran` is not the participle of `run`.
    /// </remarks>
    private static void AddBothRoles(
        Action<string, Form> add,
        string word,
        string form)
    {
        if (ParticipleOnly.Contains(word))
        {
            add(form, Form.PastParticiple);
            return;
        }

        if (PastOnly.Contains(word))
        {
            add(form, Form.Past);
            return;
        }

        add(form, Form.Past);
        add(form, Form.PastParticiple);
    }

    /// <summary>
    /// Which of two past forms is the participle, when that is knowable.
    /// </summary>
    /// <remarks>
    /// Two patterns cover almost all of English's irregular verbs:
    ///
    /// <list type="bullet">
    /// <item>the participle ends in <c>n</c> — <c>gone</c>, <c>taken</c>,
    /// <c>written</c>, <c>done</c>, <c>borne</c> — beside a past that does
    /// not (<c>went</c>, <c>took</c>, <c>wrote</c>, <c>did</c>);</item>
    /// <item>the ablaut pair, where the past has <c>a</c> and the participle
    /// <c>u</c>: <c>drank</c>/<c>drunk</c>, <c>began</c>/<c>begun</c>,
    /// <c>swam</c>/<c>swum</c>, <c>sang</c>/<c>sung</c>.</item>
    /// </list>
    ///
    /// Measured over the whole dataset: 100 of the 132 verbs with two past
    /// forms are settled by these. For the rest — <c>penned</c>/<c>pent</c> —
    /// nothing here guesses: both are labelled simply as the past, which is
    /// true of both.
    /// </remarks>
    private static string? ParticipleAmong(IReadOnlyList<string> pastForms)
    {
        if (pastForms.Count != 2) return null;

        var ending = pastForms
            .Where(f => f.EndsWith("n", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith("ne", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (ending.Count == 1) return ending[0];

        var (first, second) = (pastForms[0], pastForms[1]);
        if (first.Length != second.Length) return null;

        var differing = first
            .Zip(second, (a, b) => (a, b))
            .Where(pair => char.ToLowerInvariant(pair.a) != char.ToLowerInvariant(pair.b))
            .ToList();

        if (differing.Count != 1) return null;

        var (x, y) = differing[0];
        if (char.ToLowerInvariant(x) == 'a' && char.ToLowerInvariant(y) == 'u') return second;
        if (char.ToLowerInvariant(x) == 'u' && char.ToLowerInvariant(y) == 'a') return first;

        return null;
    }

    private static List<Inflected> Noun(string word, IReadOnlyList<string> listed)
    {
        var plural = listed.FirstOrDefault()
                     ?? (MissingPlurals.TryGetValue(word, out var missing) ? missing : null);

        if (plural is null) return [];

        // The product rule, and the reason this is not simply "add every
        // plural": a plural that is the word with an s on the end is not a new
        // word to learn. `mice` is; `books` is not.
        if (IsRegularS(word, plural)) return [];
        if (AlreadyAuthored.Contains(plural)) return [];
        if (string.Equals(plural, word, StringComparison.OrdinalIgnoreCase)) return [];

        return [new Inflected(plural, Form.Plural)];
    }

    /// <summary>Whether a form is just the word with <c>s</c> or <c>es</c>.</summary>
    private static bool IsRegularS(string word, string form) =>
        string.Equals(form, word + "s", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(form, word + "es", StringComparison.OrdinalIgnoreCase) ||
        (word.EndsWith('y') &&
         string.Equals(form, word[..^1] + "ies", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <c>walk → walked</c>, <c>love → loved</c>, <c>carry → carried</c>.
    /// </summary>
    /// <remarks>
    /// No doubled consonants here (<c>stop → stopped</c>): a doubling is an
    /// irregular spelling, so WordNet lists it, so this is never asked.
    /// </remarks>
    private static string RegularPast(string word)
    {
        if (word.EndsWith('e')) return word + "d";

        if (word.EndsWith('y') && word.Length > 1 && !IsVowel(word[^2]))
            return word[..^1] + "ied";

        return word + "ed";
    }

    /// <summary><c>walk → walking</c>, <c>love → loving</c>, <c>see → seeing</c>.</summary>
    private static string RegularProgressive(string word)
    {
        // A silent final e goes; a doubled vowel keeps it, or "seeing" becomes
        // "seing".
        if (word.EndsWith('e') && word.Length > 2 && !IsVowel(word[^2]))
            return word[..^1] + "ing";

        return word + "ing";
    }

    private static bool IsVowel(char c) => "aeiou".Contains(char.ToLowerInvariant(c));

    /// <summary>How the form is named to the learner, in each language.</summary>
    public static (string English, string Arabic) Label(Form form) => form switch
    {
        Form.Past => ("past tense", "الماضي"),
        Form.PastParticiple => ("past participle", "التصريف الثالث"),
        Form.Progressive => ("-ing form", "صيغة الاستمرار"),
        Form.Plural => ("plural", "الجمع"),
        _ => ("form", "صيغة"),
    };
}
