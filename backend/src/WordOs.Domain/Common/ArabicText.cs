using System.Text;

namespace WordOs.Domain.Common;

/// <summary>
/// Normalises Arabic so that what a learner types matches what the lexicon
/// stores.
/// </summary>
/// <remarks>
/// Arabic WordNet vocalises a large part of its glosses — <c>اِسْتَغْرَقَ</c> —
/// while nobody types the diacritics. It also spells the hamza carriers however
/// the source did: <c>إذا</c> and <c>اذا</c> are the same word to a reader and
/// different strings to a database.
///
/// So both sides are folded to one plain form: diacritics and tatweel removed,
/// the alef and ya families collapsed, ta marbuta written as ha. This is
/// deliberately search-only — the stored gloss is never changed, because that
/// is what the learner is shown.
/// </remarks>
public static class ArabicText
{
    /// <summary>Whether the text contains at least one Arabic letter.</summary>
    /// <remarks>
    /// This is what decides which side of the dictionary a query is searching:
    /// a learner typing Arabic is looking for an English word, not a prefix.
    /// </remarks>
    public static bool ContainsArabic(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var c in text)
            if (c >= '؀' && c <= 'ۿ') return true;

        return false;
    }

    /// <summary>
    /// The comparable form: unvocalised, with the letter families folded.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);

        foreach (var c in text.Trim())
        {
            switch (c)
            {
                // Harakat, tanwin, shadda, sukun and the superscript alef —
                // written in dictionaries, typed by nobody.
                case >= 'ً' and <= 'ْ':
                case 'ٰ':
                case 'ٓ':
                case 'ٔ':
                case 'ٕ':
                // Tatweel is a typographic stretch, not a letter.
                case 'ـ':
                    continue;

                // The alef family: hamza placement varies by source and by
                // typist, and gets in the way of matching either.
                case 'آ':
                case 'أ':
                case 'إ':
                case 'ٱ':
                    builder.Append('ا');
                    break;

                // Alef maqsura is written for ya at the end of a word.
                case 'ى':
                    builder.Append('ي');
                    break;

                // Ta marbuta is heard as ha and is often typed as one.
                case 'ة':
                    builder.Append('ه');
                    break;

                default:
                    builder.Append(char.ToLowerInvariant(c));
                    break;
            }
        }

        // Collapse the whitespace a phrase gloss may carry.
        return string.Join(' ', builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
