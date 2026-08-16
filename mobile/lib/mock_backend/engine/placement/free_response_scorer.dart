import 'placement_item_bank.dart';

/// Scores a written or spoken placement answer as partial credit in `[0, 1]`.
///
/// This is the seam where the Python AI service plugs in (Phase 6). Keeping it
/// an interface means the placement algorithm never contains a prompt, and an
/// AI outage degrades the test rather than breaking it: the backend falls back
/// to [HeuristicFreeResponseScorer] and records that it did, so the analytics
/// can show how many placements ran without AI.
abstract class FreeResponseScorer {
  double score({required BankItem item, required String response});
}

/// Offline fallback. Deliberately crude, and honest about it.
///
/// It measures whether the learner produced a response of roughly the length and
/// lexical range the band expects — which correlates with proficiency but does
/// not measure grammar, coherence or task achievement. It exists so the test
/// still yields *a* number when AI is unavailable; the level engine then
/// corrects it from real sessions (rule R6).
class HeuristicFreeResponseScorer implements FreeResponseScorer {
  const HeuristicFreeResponseScorer();

  @override
  double score({required BankItem item, required String response}) {
    final words = response
        .trim()
        .split(RegExp(r'\s+'))
        .where((w) => w.isNotEmpty)
        .toList();
    if (words.isEmpty) return 0;

    final expected = item.expectedWords;
    if (expected <= 0) return words.length >= 4 ? 1 : 0.5;

    // Length relative to what the band expects, capped at 1 — writing more than
    // asked is not evidence of a higher level.
    final lengthRatio = (words.length / expected).clamp(0.0, 1.0);

    // Lexical variety: repeating one word many times should not read as
    // fluency. It *discounts* the length score rather than adding to it —
    // as an additive term a one-word answer would score full variety and
    // collect credit for saying almost nothing.
    final distinct =
        words.map((w) => w.toLowerCase().replaceAll(RegExp(r'\W'), '')).toSet();
    final varietyRatio = (distinct.length / words.length).clamp(0.0, 1.0);
    final base = lengthRatio * (0.6 + 0.4 * varietyRatio);

    // Sentence structure: a clause boundary or connective suggests production
    // beyond a single memorised phrase.
    final connected = RegExp(
      r'\b(and|but|because|so|although|however|which|that|when|if)\b',
      caseSensitive: false,
    ).hasMatch(response);

    return (base * 0.85 + (connected ? 0.15 : 0)).clamp(0.0, 1.0);
  }
}
