using System.Text.RegularExpressions;

namespace WordOs.Domain.Placement;

/// <summary>
/// Scores a written or spoken placement answer as partial credit in
/// <c>[0, 1]</c>.
/// </summary>
/// <remarks>
/// This is the seam where the Python AI service plugs in (Phase 6). Keeping it
/// an interface means the placement algorithm never contains a prompt, and an
/// AI outage degrades the test rather than breaking it: the backend falls back
/// to <see cref="HeuristicFreeResponseScorer"/> and records that it did, so
/// analytics can show how many placements ran without AI.
/// </remarks>
public interface IFreeResponseScorer
{
    double Score(BankItem item, string response);
}

/// <summary>Offline fallback. Deliberately crude, and honest about it.</summary>
/// <remarks>
/// It measures whether the learner produced a response of roughly the length
/// and lexical range the band expects — which correlates with proficiency but
/// does not measure grammar, coherence or task achievement. It exists so the
/// test still yields <i>a</i> number when AI is unavailable; the level engine
/// then corrects it from real sessions (rule R6).
/// </remarks>
public sealed partial class HeuristicFreeResponseScorer : IFreeResponseScorer
{
    public double Score(BankItem item, string response)
    {
        var words = WhitespaceRegex()
            .Split(response.Trim())
            .Where(w => w.Length > 0)
            .ToList();

        if (words.Count == 0) return 0;

        var expected = item.ExpectedWords;
        if (expected <= 0) return words.Count >= 4 ? 1 : 0.5;

        // Length relative to what the band expects, capped at 1 — writing more
        // than asked is not evidence of a higher level.
        var lengthRatio = Math.Clamp((double)words.Count / expected, 0, 1);

        // Lexical variety discounts the length score rather than adding to it:
        // as an additive term a one-word answer would score full variety and
        // collect credit for saying almost nothing.
        var distinct = words
            .Select(w => NonWordRegex().Replace(w.ToLowerInvariant(), ""))
            .ToHashSet();
        var varietyRatio = Math.Clamp((double)distinct.Count / words.Count, 0, 1);
        var baseScore = lengthRatio * (0.6 + (0.4 * varietyRatio));

        // A clause boundary or connective suggests production beyond a single
        // memorised phrase.
        var connected = ConnectiveRegex().IsMatch(response);

        return Math.Clamp((baseScore * 0.85) + (connected ? 0.15 : 0), 0, 1);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\W")]
    private static partial Regex NonWordRegex();

    [GeneratedRegex(
        @"\b(and|but|because|so|although|however|which|that|when|if)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ConnectiveRegex();
}
