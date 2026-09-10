using System.Security.Cryptography;
using System.Text;

namespace WordOs.Application.Words;

/// <summary>
/// Identity for a word whose meaning did not come from the lexicon (ADR-072).
/// </summary>
/// <remarks>
/// A word's identity in this system is its <b>sense</b>, not its spelling:
/// <c>book = كتاب</c> and <c>book = يحجز</c> are two vocabulary items with two
/// independent journeys, and the unique index on <c>(UserId, SenseId)</c> is
/// what enforces that (ADR-012). A learner-written meaning has no WordNet
/// synset to point at, so it needs a sense id of its own — and it needs the
/// same property the real ones have: <i>the same meaning must produce the same
/// id</i>, or "you already added this" stops working the moment the meaning is
/// not a lexicon row.
///
/// <para>So the id is derived from the word and the meaning rather than
/// generated. Typing <c>باع</c> for <c>sell</c> twice yields one id and the
/// second add is refused by the index, exactly as a duplicate synset would be —
/// including when two taps arrive together and both pass the application check.</para>
///
/// <para>The prefix keeps the two namespaces apart with no chance of collision:
/// a WordNet sense key contains <c>%</c> and never a colon in first position,
/// and every consumer that reads meaning from a sense id — <see cref="WordForms"/>
/// looking for a <c>#pst</c> suffix, the lexicon join — simply fails to match a
/// <c>custom:</c> id, which is the correct answer for one.</para>
/// </remarks>
public static class CustomSenses
{
    /// <summary>What a learner-written or passage-written sense id starts with.</summary>
    public const string Prefix = "custom:";

    /// <summary>Whether this id names a meaning the lexicon never supplied.</summary>
    public static bool IsCustom(string? senseId) =>
        senseId is not null && senseId.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// The sense id for <paramref name="text"/> meaning <paramref name="meaning"/>.
    /// </summary>
    /// <remarks>
    /// Truncated to 32 hex characters: the column holds 64 and the prefix takes
    /// seven, and 128 bits of SHA-256 is far past the point where two of one
    /// learner's own words could collide by accident. It is not a security
    /// boundary — the meaning is stored in the clear beside it — so shortening
    /// costs nothing.
    /// </remarks>
    public static string For(string text, string meaning)
    {
        // Normalised so that trailing whitespace and letter case do not create
        // a second "different" sense that reads identically on screen.
        var key = $"{text.Trim().ToLowerInvariant()}|{meaning.Trim()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));

        return Prefix + Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
