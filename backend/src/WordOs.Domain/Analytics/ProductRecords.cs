namespace WordOs.Domain.Analytics;

/// <summary>Where a product decision stands (ADR-132).</summary>
/// <remarks>
/// <see cref="Proposed"/> is the only state anyone in the admin area can put a
/// decision in. Everything after it is the Owner's: a suggestion — the AI's or
/// an analyst's — is not a decision until the person accountable for the
/// product says so (brief §11).
/// </remarks>
public enum DecisionStatus
{
    Proposed,
    Approved,
    Implemented,
    Closed,
    Rejected,
}

/// <summary>What a person concluded once the results were in.</summary>
/// <remarks>
/// A judgement, written by a person. The evaluation beside it is arithmetic;
/// the two are kept apart so that "the metric went up" is never recorded as
/// "the decision worked" without someone having said so.
/// </remarks>
public enum DecisionVerdict
{
    Succeeded,
    Partial,
    NoEffect,
    Worse,
    Inconclusive,
}

/// <summary>
/// One metric a decision or a change promised to move (brief §11–§13).
/// </summary>
/// <param name="Key">A key from the metric catalogue (<c>IntelMeasures</c>).</param>
/// <param name="Direction"><c>up</c> or <c>down</c> — which way counts as better here.</param>
/// <param name="Target">The value hoped for, in the metric's own unit; optional.</param>
public sealed record MetricTarget(string Key, string Direction, double? Target = null);

/// <summary>
/// A product decision and what became of it (brief §11, ADR-132).
/// </summary>
/// <remarks>
/// <para>The audience, the analysed period and the metrics are stored as the
/// same filter vocabulary the rest of the admin area uses — a segment key, a
/// level, a date range, catalogue metric keys — so the evaluation is the same
/// query any page would run, over the decision's own windows.</para>
///
/// <para>The last evaluation is kept whole when the decision is closed, for
/// the reason an investigation is (ADR-125 §7): evidence that recomputes
/// differently next month is not evidence.</para>
/// </remarks>
public sealed class ProductDecision
{
    private ProductDecision() { }

    public Guid Id { get; private set; }
    public Guid AuthorId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    /// <summary>The investigation section it came from — <c>speaking</c>, <c>notifications</c>…</summary>
    public string Area { get; private set; } = string.Empty;

    public string Problem { get; private set; } = string.Empty;
    public string? Evidence { get; private set; }
    public string? Hypothesis { get; private set; }

    /// <summary>What was agreed to do.</summary>
    public string Action { get; private set; } = string.Empty;

    /// <summary>Who carries it out — a name, not an account: it may be someone outside the system.</summary>
    public string? Assignee { get; private set; }

    public string? ExpectedOutcome { get; private set; }

    /// <summary>The investigation that led here, when one did.</summary>
    public Guid? InquiryId { get; private set; }

    /// <summary>Who it is meant to affect: a cohort key (<c>level:B1</c>, <c>joined:30</c>), or everyone.</summary>
    public string? Segment { get; private set; }

    /// <summary>The period whose data the decision was made on — its baseline.</summary>
    public DateOnly? BaselineFrom { get; private set; }
    public DateOnly? BaselineTo { get; private set; }

    public IReadOnlyList<MetricTarget> Metrics { get; private set; } = [];

    public DecisionStatus Status { get; private set; }

    public Guid? ApprovedById { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }

    /// <summary>When it actually shipped — the day the "after" window starts.</summary>
    public DateOnly? ImplementedOn { get; private set; }

    /// <summary>When the results are to be looked at.</summary>
    public DateOnly? ReviewOn { get; private set; }

    public string? ActualOutcome { get; private set; }
    public DecisionVerdict? Verdict { get; private set; }

    /// <summary>The evaluation as it stood when the decision was closed.</summary>
    public string? EvaluationJson { get; private set; }

    /// <summary>
    /// The measurement taken by the system once the review date had passed —
    /// the whole "after" window, kept as it was then (ADR-132). Cleared when the
    /// review date moves, because it measured a window that no longer applies.
    /// </summary>
    public string? ReviewEvaluationJson { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static ProductDecision Propose(
        Guid id, Guid authorId, string title, string area, string problem, string action, DateTimeOffset now)
    {
        var d = new ProductDecision
        {
            Id = id,
            AuthorId = authorId,
            Status = DecisionStatus.Proposed,
            CreatedAt = now,
            UpdatedAt = now,
        };
        d.Describe(title, area, problem, action, now);
        return d;
    }

    public void Describe(string title, string area, string problem, string action, DateTimeOffset now)
    {
        Title = Required(title, nameof(title));
        Area = Required(area, nameof(area));
        Problem = Required(problem, nameof(problem));
        Action = Required(action, nameof(action));
        UpdatedAt = now;
    }

    public void Detail(
        string? evidence, string? hypothesis, string? assignee, string? expectedOutcome, Guid? inquiryId,
        string? segment, DateOnly? baselineFrom, DateOnly? baselineTo, IReadOnlyList<MetricTarget> metrics,
        DateOnly? reviewOn, DateTimeOffset now)
    {
        if (baselineFrom is not null && baselineTo is not null && baselineFrom > baselineTo)
            (baselineFrom, baselineTo) = (baselineTo, baselineFrom);

        // What was measured is no longer what the decision says.
        if (!Metrics.SequenceEqual(metrics) || Segment != Optional(segment) || ReviewOn != reviewOn
            || BaselineFrom != baselineFrom || BaselineTo != baselineTo)
        {
            ReviewEvaluationJson = null;
            ReviewedAt = null;
        }

        Evidence = Optional(evidence);
        Hypothesis = Optional(hypothesis);
        Assignee = Optional(assignee);
        ExpectedOutcome = Optional(expectedOutcome);
        InquiryId = inquiryId;
        Segment = Optional(segment);
        BaselineFrom = baselineFrom;
        BaselineTo = baselineTo;
        Metrics = metrics;
        ReviewOn = reviewOn;
        UpdatedAt = now;
    }

    public void Approve(Guid ownerId, DateTimeOffset now)
    {
        if (Status != DecisionStatus.Proposed)
            throw new InvalidOperationException("Only a proposal can be approved.");
        Status = DecisionStatus.Approved;
        ApprovedById = ownerId;
        ApprovedAt = now;
        UpdatedAt = now;
    }

    public void Reject(string? reason, DateTimeOffset now)
    {
        if (Status != DecisionStatus.Proposed)
            throw new InvalidOperationException("Only a proposal can be rejected.");
        Status = DecisionStatus.Rejected;
        ActualOutcome = Optional(reason);
        UpdatedAt = now;
    }

    public void MarkImplemented(DateOnly on, DateTimeOffset now)
    {
        if (Status is not (DecisionStatus.Approved or DecisionStatus.Implemented))
            throw new InvalidOperationException("Only an approved decision can be implemented.");
        Status = DecisionStatus.Implemented;
        ImplementedOn = on;
        UpdatedAt = now;
    }

    public void Reschedule(DateOnly? reviewOn, DateTimeOffset now)
    {
        if (reviewOn != ReviewOn)
        {
            ReviewEvaluationJson = null;
            ReviewedAt = null;
        }
        ReviewOn = reviewOn;
        UpdatedAt = now;
    }

    /// <summary>Due for its automatic measurement: shipped, not closed, the review day fully past.</summary>
    public bool AwaitsReview(DateOnly today) =>
        Status == DecisionStatus.Implemented && ReviewedAt is null && ReviewOn is { } r && r < today;

    public void RecordReview(string evaluationJson, DateTimeOffset now)
    {
        ReviewEvaluationJson = evaluationJson;
        ReviewedAt = now;
    }

    /// <summary>A result written before the decision is closed — an interim reading.</summary>
    public void RecordOutcome(string? outcome, DateTimeOffset now)
    {
        ActualOutcome = Optional(outcome);
        UpdatedAt = now;
    }

    public void Close(DecisionVerdict verdict, string? outcome, string? evaluationJson, DateTimeOffset now)
    {
        if (Status is DecisionStatus.Proposed or DecisionStatus.Rejected)
            throw new InvalidOperationException("A decision that was never approved cannot be closed.");
        Status = DecisionStatus.Closed;
        Verdict = verdict;
        ActualOutcome = Optional(outcome) ?? ActualOutcome;
        EvaluationJson = evaluationJson;
        UpdatedAt = now;
    }

    internal static string Required(string? value, string name)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) throw new ArgumentException($"{name} is required.", name);
        return text;
    }

    internal static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// A change to the product that may move what learners do — or an experiment,
/// which is a change with a hypothesis and, ideally, a group to compare against
/// (brief §12–§13, ADR-132).
/// </summary>
/// <remarks>
/// One record for both, because an experiment <i>is</i> a change, and a log
/// kept in two places is how the change that explains a dip ends up in the
/// other one. It is not a commit log: only changes with a plausible effect on
/// learners belong here.
/// </remarks>
public sealed class ProductChange
{
    private ProductChange() { }

    /// <summary>The closed list of kinds — brief §13's examples, plus "other".</summary>
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>
    {
        "content", "translation", "speaking", "notifications", "scheduling", "ai_feedback",
        "performance", "ux", "other",
    };

    public Guid Id { get; private set; }
    public Guid AuthorId { get; private set; }

    public string Title { get; private set; } = string.Empty;
    public string Kind { get; private set; } = "other";
    public bool IsExperiment { get; private set; }

    public string Description { get; private set; } = string.Empty;
    public string? Problem { get; private set; }
    public string? Hypothesis { get; private set; }

    /// <summary>App or content version that carried it.</summary>
    public string? Version { get; private set; }

    public DateOnly ShippedOn { get; private set; }

    /// <summary>Who received it — a cohort key, or everyone.</summary>
    public string? Segment { get; private set; }

    /// <summary>Who did not, for an experiment with a comparison group.</summary>
    public string? ComparisonSegment { get; private set; }

    public Guid? DecisionId { get; private set; }

    public IReadOnlyList<MetricTarget> Metrics { get; private set; } = [];

    /// <summary>Days measured on each side of <see cref="ShippedOn"/>.</summary>
    public int WindowDays { get; private set; }

    public string? SideEffects { get; private set; }
    public string? Result { get; private set; }
    public string? Recommendation { get; private set; }
    public DecisionVerdict? Verdict { get; private set; }
    public string? EvaluationJson { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static ProductChange Record(Guid id, Guid authorId, DateTimeOffset now) =>
        new() { Id = id, AuthorId = authorId, CreatedAt = now, UpdatedAt = now, WindowDays = 14 };

    public void Describe(
        string title, string kind, bool isExperiment, string description, string? problem, string? hypothesis,
        string? version, DateOnly shippedOn, string? segment, string? comparisonSegment, Guid? decisionId,
        IReadOnlyList<MetricTarget> metrics, int windowDays, DateTimeOffset now)
    {
        if (!Kinds.Contains(kind)) throw new ArgumentException($"Unknown change kind '{kind}'.", nameof(kind));

        Title = ProductDecision.Required(title, nameof(title));
        Kind = kind;
        IsExperiment = isExperiment;
        Description = ProductDecision.Required(description, nameof(description));
        Problem = ProductDecision.Optional(problem);
        Hypothesis = ProductDecision.Optional(hypothesis);
        Version = ProductDecision.Optional(version);
        ShippedOn = shippedOn;
        Segment = ProductDecision.Optional(segment);
        ComparisonSegment = ProductDecision.Optional(comparisonSegment);
        DecisionId = decisionId;
        Metrics = metrics;
        WindowDays = Math.Clamp(windowDays, 1, 180);
        UpdatedAt = now;
    }

    public void Conclude(
        string? result, string? sideEffects, string? recommendation, DecisionVerdict? verdict,
        string? evaluationJson, DateTimeOffset now)
    {
        Result = ProductDecision.Optional(result);
        SideEffects = ProductDecision.Optional(sideEffects);
        Recommendation = ProductDecision.Optional(recommendation);
        Verdict = verdict;
        if (evaluationJson is not null) EvaluationJson = evaluationJson;
        UpdatedAt = now;
    }
}
