using WordOs.Domain.Analytics;

namespace WordOs.Api.Admin;

public sealed record Window(DateOnly From, DateOnly To)
{
    public int Days => To.DayNumber - From.DayNumber + 1;
}

/// <param name="Outcome">
/// <c>improved</c>, <c>worsened</c>, <c>flat</c>, <c>insufficient</c> (a side
/// below the minimum sample) or <c>unknown</c> (no value on a side).
/// </param>
public sealed record MeasuredMetric(
    string Key, string Label, string Format, string Direction, double? Target,
    double? Before, int BeforeSample, double? After, int AfterSample,
    double? Change, double? RelativeChange, string Outcome, bool? TargetMet,
    double? ComparisonBefore, double? ComparisonAfter, int? ComparisonSample, double? NetOfComparison);

public sealed record Overlap(Guid Id, string Title, string Kind, DateOnly ShippedOn);

/// <summary>
/// What the numbers did around a decision or a change (brief §12).
/// </summary>
/// <remarks>
/// Arithmetic and rules only. <see cref="Cautions"/> are the reasons a reader
/// should not yet believe the arithmetic, stated by fixed rules; whether the
/// decision <i>worked</i> is a verdict a person writes, and the page shows it
/// apart from this.
/// </remarks>
public sealed record Evaluation(
    DateTimeOffset ComputedAt,
    string Population,
    int PopulationSize,
    Window Before,
    Window After,
    bool AfterComplete,
    string? ComparisonPopulation,
    IReadOnlyList<MeasuredMetric> Targeted,
    IReadOnlyList<MeasuredMetric> Guardrails,
    IReadOnlyList<Overlap> OverlappingChanges,
    IReadOnlyList<string> Cautions,
    string Summary);

public static class IntelEvaluation
{
    /// <summary>
    /// Measure <paramref name="metrics"/> for <paramref name="segment"/> in
    /// <paramref name="before"/> and <paramref name="after"/>, and — when a
    /// comparison group is named — for that group in both windows too.
    /// </summary>
    public static Evaluation Evaluate(
        IntelDataset data, IReadOnlyList<MetricTarget> metrics, string? segment, string? comparisonSegment,
        Window before, Window after, IReadOnlyList<Overlap> overlapping)
    {
        var today = data.Day(data.Now);
        var afterComplete = after.To < today;
        var clippedAfter = after with { To = after.To > today ? today : after.To };
        var min = data.Options.MinimumSample;

        var population = new IntelScope(data, new IntelFilter(Segment: segment, Days: 3650));
        var populationLabel = string.IsNullOrWhiteSpace(segment) ? "كل المتعلمين" : Segments.LabelOf(segment);

        MeasuredMetric Measure(MetricTarget t)
        {
            var def = IntelMeasures.Find(t.Key)!;
            var up = t.Direction != "down";
            var b = IntelMeasures.Measure(data, t.Key, before.From, before.To, segment);
            var a = clippedAfter.From <= clippedAfter.To
                ? IntelMeasures.Measure(data, t.Key, clippedAfter.From, clippedAfter.To, segment)
                : new MeasureValue(null, 0);

            // Counts depend on the window's length; a shorter "after" so far
            // would read as a fall. Scale additive counts to the baseline's
            // length — never rates or distinct learners.
            var scale = def.Additive && clippedAfter.Days > 0 && clippedAfter.Days != before.Days
                ? (double)before.Days / clippedAfter.Days : 1;
            var afterValue = a.Value is { } av ? av * scale : (double?)null;

            double? cb = null, ca = null, net = null;
            int? cn = null;
            if (!string.IsNullOrWhiteSpace(comparisonSegment) && clippedAfter.From <= clippedAfter.To)
            {
                var cBefore = IntelMeasures.Measure(data, t.Key, before.From, before.To, comparisonSegment);
                var cAfter = IntelMeasures.Measure(data, t.Key, clippedAfter.From, clippedAfter.To, comparisonSegment);
                cb = cBefore.Value;
                ca = cAfter.Value is { } c ? c * scale : null;
                cn = cAfter.Sample;
                // Difference in differences: what moved for the group that got
                // the change beyond what moved for the group that did not.
                if (b.Value is { } bv2 && afterValue is { } av2 && cb is { } cbv && ca is { } cav)
                    net = (av2 - bv2) - (cav - cbv);
            }

            double? change = b.Value is { } bv && afterValue is { } avv ? avv - bv : null;
            double? relative = change is { } ch && b.Value is { } bb && bb != 0 ? ch / Math.Abs(bb) : null;

            string outcome;
            if (b.Value is null || afterValue is null) outcome = "unknown";
            else if (b.Sample < min || a.Sample < min) outcome = "insufficient";
            else if (relative is { } r ? Math.Abs(r) < data.Options.FlatChange : change == 0) outcome = "flat";
            else outcome = (change > 0) == up ? "improved" : "worsened";

            bool? met = t.Target is { } target && afterValue is { } v ? (up ? v >= target : v <= target) : null;

            return new MeasuredMetric(def.Key, def.Label, def.Format, up ? "up" : "down", t.Target,
                b.Value, b.Sample, afterValue, a.Sample, change, relative, outcome, met, cb, ca, cn, net);
        }

        var targeted = metrics.Where(m => IntelMeasures.Find(m.Key) is not null).Select(Measure).ToList();
        var guardrails = IntelMeasures.Guardrails
            .Where(k => targeted.All(t => t.Key != k))
            .Select(k => Measure(new MetricTarget(k, IntelMeasures.Find(k)!.GoodWhenUp ? "up" : "down")))
            .ToList();

        var cautions = new List<string>();
        if (!afterComplete)
            cautions.Add($"فترة ما بعد التنفيذ لم تكتمل بعد (حتى {after.To:yyyy-MM-dd}) — الأعداد التراكمية مُعايَرة على طول الفترة السابقة، والنتيجة مؤقتة.");
        if (targeted.Count == 0)
            cautions.Add("لم تُحدَّد مقاييس مستهدفة، فلا يمكن الحكم على النتيجة بالأرقام.");
        if (targeted.Any(t => t.Outcome == "insufficient"))
            cautions.Add($"بعض المقاييس دون الحد الأدنى للعينة ({min}) — لا تُستنتج منها نتيجة.");
        if (string.IsNullOrWhiteSpace(comparisonSegment))
            cautions.Add("لا توجد مجموعة مقارنة: التغير ارتباط زمني بالتنفيذ وليس دليلًا على أنه السبب. تجربة بمجموعة مقارنة تجيب عن ذلك.");
        if (overlapping.Count > 0)
            cautions.Add($"نُفذت {overlapping.Count} تغييرات أخرى في الفترة نفسها — قد تفسر جزءًا من التغير.");
        if (targeted.Any(t => t.Outcome == "improved") && guardrails.Any(g => g.Outcome == "worsened"))
            cautions.Add("تحسن مقياس مستهدف بينما تراجع مقياس عام — راجع المقاييس الأخرى قبل اعتبار القرار ناجحًا.");

        var improved = targeted.Count(t => t.Outcome == "improved");
        var worsened = targeted.Count(t => t.Outcome == "worsened");
        var summary = targeted.Count == 0
            ? "لا مقاييس مستهدفة."
            : $"من {targeted.Count} مقاييس مستهدفة: تحسن {improved}، تراجع {worsened}، "
              + $"دون تغير يُذكر {targeted.Count(t => t.Outcome == "flat")}، "
              + $"بيانات غير كافية {targeted.Count(t => t.Outcome is "insufficient" or "unknown")}.";

        return new Evaluation(data.Now, populationLabel, population.Learners.Count, before, after, afterComplete,
            string.IsNullOrWhiteSpace(comparisonSegment) ? null : Segments.LabelOf(comparisonSegment),
            targeted, guardrails, overlapping, cautions, summary);
    }

    /// <summary>The baseline and after windows of a decision, or null when it has not shipped.</summary>
    public static (Window Before, Window After)? WindowsOf(ProductDecision d, int defaultDays)
    {
        if (d.ImplementedOn is not { } shipped) return null;
        // A baseline taken from an investigation often ends on the day it was
        // asked — which may be the day it shipped. A day counted on both sides
        // would pull the two windows together, so the baseline stops the day
        // before; if nothing is left of it, the default window stands in.
        var before = d.BaselineFrom is { } bf && d.BaselineTo is { } bt && bf < shipped
            ? new Window(bf, bt < shipped ? bt : shipped.AddDays(-1))
            : new Window(shipped.AddDays(-defaultDays), shipped.AddDays(-1));
        var afterEnd = d.ReviewOn is { } r && r >= shipped ? r : shipped.AddDays(before.Days - 1);
        return (before, new Window(shipped, afterEnd));
    }

    public static (Window Before, Window After) WindowsOf(ProductChange c) =>
        (new Window(c.ShippedOn.AddDays(-c.WindowDays), c.ShippedOn.AddDays(-1)),
         new Window(c.ShippedOn, c.ShippedOn.AddDays(c.WindowDays - 1)));
}
