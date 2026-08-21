using WordOs.Domain.Words;

namespace WordOs.Application.Words;

/// <summary>
/// What the generator needs to know about a word's shape.
/// </summary>
/// <remarks>
/// Two questions, both answered here so every skill answers them the same way
/// (ADR-047):
///
/// <list type="bullet">
/// <item><b>Which form is this?</b> A learner who added <c>played</c> as the
/// past participle is practising "I have played". The lexicon says so in the
/// sense id it was copied from — <c>play%2:29:00::#pp</c> — so the form is read
/// back from there rather than stored again.</item>
/// <item><b>May the passage pluralise it?</b> Only when the plural is the word
/// plus <c>s</c> or <c>es</c>. <c>book</c> and <c>books</c> are one word to a
/// learner; <c>mouse</c> and <c>mice</c> are two, and the second is a
/// vocabulary item they have not been taught.</item>
/// </list>
/// </remarks>
public static class WordForms
{
    /// <summary>The suffix the lexicon gives an inflected sense.</summary>
    private const string Marker = "#";

    /// <summary>
    /// Which form a word is, in the words the prompt uses, or null for a lemma.
    /// </summary>
    public static string? FormOf(Word word)
    {
        var index = word.SenseId.LastIndexOf(Marker, StringComparison.Ordinal);
        if (index < 0) return null;

        return word.SenseId[(index + 1)..] switch
        {
            "pst" => "past tense",
            "pp" => "past participle",
            "ing" => "-ing form",
            "pl" => "plural",
            _ => null,
        };
    }

    /// <summary>
    /// Which form this entry is, as a stable key for the client (ADR-056).
    /// </summary>
    /// <remarks>
    /// A key rather than a sentence, because the learner reads it in their own
    /// language and the server does not know which that is at this point
    /// (ADR-035). Null for a word in its plain form — a learner looking at
    /// <c>book</c> needs no label saying it is <c>book</c>.
    /// </remarks>
    public static string? FormKey(Word word)
    {
        var index = word.SenseId.LastIndexOf(Marker, StringComparison.Ordinal);
        if (index < 0) return null;

        return word.SenseId[(index + 1)..] switch
        {
            "pst" => "past",
            "pp" => "pastParticiple",
            "ing" => "ing",
            "pl" => "plural",
            _ => null,
        };
    }

    /// <summary>
    /// Whether the passage may write this word in the plural.
    /// </summary>
    /// <param name="word">The learner's word.</param>
    /// <param name="hasIrregularPlural">
    /// Whether the lexicon carries a differently-spelled plural for it —
    /// <c>mice</c> for <c>mouse</c>. Looked up once per session, because it is
    /// a fact about the dictionary rather than about the learner.
    /// </param>
    public static bool MayPluralise(Word word, bool hasIrregularPlural)
    {
        // Only nouns, and only in their own right: a plural is not pluralised
        // again, and a verb form is a verb form.
        if (!string.Equals(word.PartOfSpeech, "n", StringComparison.OrdinalIgnoreCase))
            return false;

        if (FormOf(word) is not null) return false;

        return !hasIrregularPlural;
    }
}
