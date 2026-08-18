using System.Text.RegularExpressions;

namespace WordOs.Application.Words;

/// <summary>
/// Finds which Active words a piece of generated content actually reused.
/// </summary>
/// <remarks>
/// The AI is <i>asked</i> to weave Active vocabulary into a passage, but its
/// report of what it used is never trusted — a model that says "I used
/// 'interface'" while the passage never contains it would inflate the exposure
/// counts that archiving depends on (rule R2, rule R8). So the server reads the
/// text it received and decides for itself.
///
/// The match is deliberately conservative:
///
/// <list type="bullet">
/// <item>word boundaries on both sides, so <c>art</c> does not match
/// <c>started</c>;</item>
/// <item>common inflections count — <c>research</c> is met in <c>researched</c>
/// and <c>researching</c>, because the learner met the word;</item>
/// <item>derivations do not — <c>researcher</c> is a different word, and the
/// alternation plus the trailing boundary excludes it;</item>
/// <item>multi-word entries tolerate any run of whitespace, since the generator
/// may break a line between them.</item>
/// </list>
///
/// Being pure and deterministic, the same content always yields the same
/// result — which is what makes an exposure event reproducible rather than a
/// side effect of when it happened to run.
/// </remarks>
public static class ActiveWordReuseDetector
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Returns the candidates that appear in <paramref name="text"/>, each at
    /// most once regardless of how often it occurs.
    /// </summary>
    public static IReadOnlyList<T> Detect<T>(
        string? text,
        IEnumerable<T> candidates,
        Func<T, string> word)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var found = new List<T>();
        foreach (var candidate in candidates)
        {
            if (Contains(text, word(candidate))) found.Add(candidate);
        }
        return found;
    }

    public static bool Contains(string text, string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return false;

        try
        {
            return Regex.IsMatch(text, PatternFor(word),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                MatchTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological passage must not fail a learner's session; the
            // word simply does not count as met.
            return false;
        }
    }

    /// <summary>
    /// Every place a word occurs in <paramref name="text"/>, as
    /// <c>(start, end)</c> offsets.
    /// </summary>
    /// <remarks>
    /// The reading screen underlines the session's words inside the generated
    /// passage (Part 2 §16) and must not offer their meaning when tapped (§19).
    /// Both need to know *where* the words are, and finding that out is the
    /// server's job — a client scanning for its own target words would go
    /// wrong on exactly the inflected forms this matcher exists to handle.
    /// </remarks>
    public static IReadOnlyList<(int Start, int End)> Locate(
        string? text, string word)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(word))
            return [];

        try
        {
            return Regex.Matches(text, PatternFor(word),
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    MatchTimeout)
                .Select(m => (m.Index, m.Index + m.Length))
                .ToList();
        }
        catch (RegexMatchTimeoutException)
        {
            // The passage still reads; it just loses its underlines.
            return [];
        }
    }

    private static string PatternFor(string word)
    {
        var parts = word.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(Regex.Escape);

        var stem = string.Join(@"\s+", parts);

        // A trailing `e` is dropped before -ed/-ing (`use` → `used`, `using`),
        // so the stem is allowed to lose it.
        if (stem.EndsWith('e')) stem = stem[..^1] + "e?";

        return $@"\b{stem}(s|es|ed|ing|d)?\b";
    }
}
