using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WordOs.Api.Endpoints;
using WordOs.Application.Abstractions;
using WordOs.Domain.Analytics;
using WordOs.Domain.Common;
using WordOs.Domain.Users;
using WordOs.Infrastructure.Persistence;
using static WordOs.Api.Admin.AdminIntelEndpoints;

namespace WordOs.Api.Admin;

/// <summary>
/// Decide → Implement → Measure → Learn (ADR-132): the decision log, the change
/// and experiment log, the notification journey, the metric dictionary, and
/// investigations that continue a thread or are looked at again.
/// </summary>
/// <remarks>
/// Still read-only over the learning pipeline: the writes are the admin area's
/// own records. A decision records what the team chose; it changes nothing in
/// WordOS by being approved. Every write lands in the audit trail, which is
/// also each record's change history (brief §11).
/// </remarks>
public static class AdminProductEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public sealed record DecisionRequest(
        [property: Required, StringLength(200, MinimumLength = 3)] string Title,
        [property: Required, StringLength(32)] string Area,
        [property: Required, StringLength(4000, MinimumLength = 3)] string Problem,
        [property: Required, StringLength(4000, MinimumLength = 3)] string Action,
        [property: StringLength(4000)] string? Evidence = null,
        [property: StringLength(4000)] string? Hypothesis = null,
        [property: StringLength(100)] string? Assignee = null,
        [property: StringLength(2000)] string? ExpectedOutcome = null,
        Guid? InquiryId = null,
        [property: StringLength(64)] string? Segment = null,
        DateOnly? BaselineFrom = null,
        DateOnly? BaselineTo = null,
        IReadOnlyList<MetricTarget>? Metrics = null,
        DateOnly? ReviewOn = null);

    /// <param name="Action"><c>approve</c>, <c>reject</c>, <c>implement</c>, <c>reschedule</c>, <c>outcome</c>, <c>close</c>.</param>
    public sealed record DecisionTransition(
        [property: Required, StringLength(16)] string Action,
        DateOnly? On = null,
        DateOnly? ReviewOn = null,
        string? Verdict = null,
        [property: StringLength(4000)] string? Note = null);

    public sealed record ChangeRequest(
        [property: Required, StringLength(200, MinimumLength = 3)] string Title,
        [property: Required, StringLength(24)] string Kind,
        bool IsExperiment,
        [property: Required, StringLength(4000, MinimumLength = 3)] string Description,
        DateOnly ShippedOn,
        [property: StringLength(4000)] string? Problem = null,
        [property: StringLength(4000)] string? Hypothesis = null,
        [property: StringLength(64)] string? Version = null,
        [property: StringLength(64)] string? Segment = null,
        [property: StringLength(64)] string? ComparisonSegment = null,
        Guid? DecisionId = null,
        IReadOnlyList<MetricTarget>? Metrics = null,
        int? WindowDays = null);

    public sealed record ChangeConclusion(
        [property: StringLength(4000)] string? Result = null,
        [property: StringLength(2000)] string? SideEffects = null,
        [property: StringLength(2000)] string? Recommendation = null,
        string? Verdict = null,
        bool FreezeEvaluation = false);

    public sealed record FollowUpRequest(DateOnly? On);

    public static IEndpointRouteBuilder MapAdminProductEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/intel")
            .WithTags("Admin Intelligence")
            .RequireAuthorization(Policies.AdminArea);

        group.MapGet("/definitions", Definitions);
        group.MapGet("/users/{id:guid}/days/{date}", DayAsync);
        group.MapGet("/notifications", NotificationsAsync);

        group.MapGet("/decisions", DecisionsAsync);
        group.MapGet("/decisions/{id:guid}", DecisionAsync);
        group.MapPost("/decisions", ProposeAsync);
        group.MapPut("/decisions/{id:guid}", EditDecisionAsync);
        group.MapPost("/decisions/{id:guid}/transition", TransitionAsync);

        group.MapGet("/changes", ChangesAsync);
        group.MapGet("/changes/{id:guid}", ChangeAsync);
        group.MapPost("/changes", RecordChangeAsync);
        group.MapPut("/changes/{id:guid}", EditChangeAsync);
        group.MapPost("/changes/{id:guid}/conclude", ConcludeChangeAsync);

        group.MapPatch("/inquiries/{id:guid}", FollowUpAsync);
        group.MapPost("/inquiries/{id:guid}/rerun", RerunAsync).RequireRateLimiting(RateLimitPolicies.Expensive);

        return app;
    }

    // ── Dictionary (brief §15) ───────────────────────────────────────────────

    private static IResult Definitions(ClaimsPrincipal principal, IOptions<AdminIntelOptions> options)
    {
        if (Deny(principal) is { } denied) return denied;
        return Results.Ok(new
        {
            metrics = IntelMeasures.Catalogue,
            events = EventDictionary.Events,
            brief = EventDictionary.BriefCoverage,
            thresholds = options.Value,
        });
    }

    // ── One learner's day (brief page 3) ─────────────────────────────────────

    private static async Task<IResult> DayAsync(
        Guid id, string date, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db,
        WordOsConfiguration config, IOptions<AdminIntelOptions> options, IConfiguration configuration,
        TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return Problems.BadRequest("BAD_DATE", "The day must be yyyy-MM-dd.");
        var data = await LoadAsync(db, config, options, clock, ct);
        var user = data.LearnerById(id);
        if (user is null) return Problems.NotFound("USER_NOT_FOUND", "No such learner.");

        // A day of one person's answers is the same kind of reading as their 360.
        await AuditAsync(db, principal, http, configuration, clock, "user.viewed", id.ToString(), $"day {date}");
        return Results.Ok(IntelDay.For(data, user, day));
    }

    // ── Notifications (brief page 8) ─────────────────────────────────────────

    private static async Task<IResult> NotificationsAsync(
        HttpRequest request, ClaimsPrincipal principal, WordOsDbContext db, WordOsConfiguration config,
        IOptions<AdminIntelOptions> options, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var data = await LoadAsync(db, config, options, clock, ct);
        var scope = new IntelScope(data, FilterFrom(request));
        return Results.Ok(IntelNotifications.For(scope, PrivacyFor(principal), PeriodOf(scope)));
    }

    // ── Decisions (brief §11–§12) ────────────────────────────────────────────

    private static string ReviewState(ProductDecision d, DateOnly today) => d switch
    {
        { Status: DecisionStatus.Closed or DecisionStatus.Rejected } => "done",
        { ReviewOn: null } => "unscheduled",
        { ReviewedAt: not null } => "measured",
        { ReviewOn: { } r } when r <= today && d.Status != DecisionStatus.Proposed => "due",
        _ => "scheduled",
    };

    private static object Row(ProductDecision d, DateOnly today, string? author) => new
    {
        id = d.Id,
        title = d.Title,
        area = d.Area,
        status = d.Status.ToString().ToUpperInvariant(),
        verdict = d.Verdict?.ToString().ToUpperInvariant(),
        assignee = d.Assignee,
        approvedAt = d.ApprovedAt,
        implementedOn = d.ImplementedOn,
        reviewOn = d.ReviewOn,
        reviewState = ReviewState(d, today),
        reviewedAt = d.ReviewedAt,
        metrics = d.Metrics.Select(m => m.Key),
        inquiryId = d.InquiryId,
        author,
        createdAt = d.CreatedAt,
        updatedAt = d.UpdatedAt,
    };

    private static async Task<IResult> DecisionsAsync(
        string? status, string? area, string? q, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db,
        WordOsConfiguration config, IOptions<AdminIntelOptions> options, IConfiguration configuration,
        TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        await ReviewDueAsync(db, principal, http, config, options, configuration, clock, ct);
        var query = db.ProductDecisions.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(area)) query = query.Where(d => d.Area == area);
        var today = Today(config, clock);
        // Two groupings the log's cards ask for, besides the plain statuses.
        if (status == "active")
            query = query.Where(d => d.Status == DecisionStatus.Approved || d.Status == DecisionStatus.Implemented);
        else if (status == "due")
            query = query.Where(d => (d.Status == DecisionStatus.Approved || d.Status == DecisionStatus.Implemented)
                                     && d.ReviewOn != null && d.ReviewOn <= today);
        else if (Enum.TryParse<DecisionStatus>(status, true, out var st)) query = query.Where(d => d.Status == st);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            query = query.Where(d => EF.Functions.ILike(d.Title, term) || EF.Functions.ILike(d.Problem, term)
                                     || EF.Functions.ILike(d.Action, term));
        }

        var list = await query.OrderByDescending(d => d.UpdatedAt).Take(300).ToListAsync(ct);
        var names = await NamesAsync(db, list.Select(d => d.AuthorId), ct);

        var rows = list.Select(d => Row(d, today, names.GetValueOrDefault(d.AuthorId))).ToList();
        return Results.Ok(new
        {
            items = rows,
            counts = new
            {
                proposed = list.Count(d => d.Status == DecisionStatus.Proposed),
                active = list.Count(d => d.Status is DecisionStatus.Approved or DecisionStatus.Implemented),
                due = list.Count(d => ReviewState(d, today) is "due" or "measured"),
                closed = list.Count(d => d.Status == DecisionStatus.Closed),
            },
        });
    }

    private static async Task<IResult> DecisionAsync(
        Guid id, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db, WordOsConfiguration config,
        IOptions<AdminIntelOptions> options, IConfiguration configuration, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        await ReviewDueAsync(db, principal, http, config, options, configuration, clock, ct, only: id);
        var d = await db.ProductDecisions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return Problems.NotFound("NOT_FOUND", "No such decision.");

        var today = Today(config, clock);
        var names = await NamesAsync(db, new[] { d.AuthorId, d.ApprovedById ?? Guid.Empty }, ct);
        var inquiry = d.InquiryId is null ? null : await db.AdminInquiries.AsNoTracking()
            .Where(i => i.Id == d.InquiryId)
            .Select(i => new { id = i.Id, section = i.Section, question = i.Question, summary = i.Summary, createdAt = i.CreatedAt })
            .FirstOrDefaultAsync(ct);
        var changes = await db.ProductChanges.AsNoTracking().Where(c => c.DecisionId == id)
            .OrderBy(c => c.ShippedOn)
            .Select(c => new { id = c.Id, title = c.Title, kind = c.Kind, isExperiment = c.IsExperiment, shippedOn = c.ShippedOn })
            .ToListAsync(ct);

        object? evaluation = null;
        string? evaluationNote = null;
        if (d.Status == DecisionStatus.Closed && d.EvaluationJson is not null)
        {
            evaluation = JsonSerializer.Deserialize<JsonElement>(d.EvaluationJson);
            evaluationNote = "القياس كما كان عند إغلاق القرار.";
        }
        else if (IntelEvaluation.WindowsOf(d, options.Value.EvaluationWindowDays) is { } w)
        {
            var data = await LoadAsync(db, config, options, clock, ct);
            evaluation = IntelEvaluation.Evaluate(data, d.Metrics, d.Segment, null, w.Before, w.After,
                await OverlapsAsync(db, w.After, exceptDecision: d.Id, exceptChange: null, ct));
            evaluationNote = "قياس حي — يُعاد حسابه عند كل فتح حتى يُغلق القرار.";
        }
        else evaluationNote = "لم يُنفَّذ القرار بعد، فلا فترة «بعد» لقياسها.";

        return Results.Ok(new
        {
            decision = new
            {
                id = d.Id, title = d.Title, area = d.Area, problem = d.Problem, evidence = d.Evidence,
                hypothesis = d.Hypothesis, action = d.Action, assignee = d.Assignee,
                expectedOutcome = d.ExpectedOutcome, inquiryId = d.InquiryId, segment = d.Segment,
                segmentLabel = d.Segment is null ? null : Segments.LabelOf(d.Segment),
                baselineFrom = d.BaselineFrom, baselineTo = d.BaselineTo, metrics = d.Metrics,
                status = d.Status.ToString().ToUpperInvariant(), verdict = d.Verdict?.ToString().ToUpperInvariant(),
                approvedBy = d.ApprovedById is { } a ? names.GetValueOrDefault(a) : null, approvedAt = d.ApprovedAt,
                implementedOn = d.ImplementedOn, reviewOn = d.ReviewOn, reviewState = ReviewState(d, today),
                actualOutcome = d.ActualOutcome, author = names.GetValueOrDefault(d.AuthorId),
                createdAt = d.CreatedAt, updatedAt = d.UpdatedAt,
            },
            inquiry,
            changes,
            evaluation,
            evaluationNote,
            review = d.ReviewEvaluationJson is null ? (object?)null : JsonSerializer.Deserialize<JsonElement>(d.ReviewEvaluationJson),
            reviewedAt = d.ReviewedAt,
            history = await HistoryAsync(db, id, ct),
        });
    }

    /// <summary>
    /// Measure every decision whose review date has fully passed, once, and
    /// keep it (brief §12, ADR-132).
    /// </summary>
    /// <remarks>
    /// <para>There is no scheduler in this API, and adding one would be a
    /// process to deploy, keep alive across Render's sleeps, and watch. It is
    /// not needed: once the review day is over, the "after" window is closed,
    /// so measuring it when the log is next opened gives the same figures as
    /// measuring it at midnight — only phone events that arrive late could
    /// differ. So the first read after the date takes the measurement and
    /// keeps it, and the list says "measured — awaiting a verdict".</para>
    ///
    /// <para>It is a measurement, not a verdict: the decision stays open until
    /// the Owner closes it.</para>
    /// </remarks>
    private static async Task ReviewDueAsync(
        WordOsDbContext db, ClaimsPrincipal principal, HttpContext http, WordOsConfiguration config,
        IOptions<AdminIntelOptions> options, IConfiguration configuration, TimeProvider clock, CancellationToken ct,
        Guid? only = null)
    {
        var today = Today(config, clock);
        var due = await db.ProductDecisions
            .Where(d => d.Status == DecisionStatus.Implemented && d.ReviewedAt == null
                        && d.ReviewOn != null && d.ReviewOn < today && (only == null || d.Id == only))
            .ToListAsync(ct);
        if (due.Count == 0) return;

        var data = await LoadAsync(db, config, options, clock, ct);
        foreach (var d in due)
        {
            if (!d.AwaitsReview(today) || IntelEvaluation.WindowsOf(d, options.Value.EvaluationWindowDays) is not { } w) continue;
            var evaluation = IntelEvaluation.Evaluate(data, d.Metrics, d.Segment, null, w.Before, w.After,
                await OverlapsAsync(db, w.After, exceptDecision: d.Id, exceptChange: null, ct));
            d.RecordReview(JsonSerializer.Serialize(evaluation, Web), clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            await AuditAsync(db, principal, http, configuration, clock, "decision.reviewed", d.Id.ToString(), "automatic, review date passed");
        }
    }

    private static async Task<IResult> ProposeAsync(
        DecisionRequest request, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db,
        IConfiguration configuration, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        if (Invalid(request, out var problem)) return problem!;
        if (request.InquiryId is { } iq && !await db.AdminInquiries.AnyAsync(i => i.Id == iq, ct))
            return Problems.BadRequest("UNKNOWN_INQUIRY", "No such investigation.");

        var now = clock.GetUtcNow();
        var d = ProductDecision.Propose(Guid.CreateVersion7(now), principal.UserId()!.Value,
            request.Title, request.Area, request.Problem, request.Action, now);
        Detail(d, request, now);
        db.ProductDecisions.Add(d);
        await db.SaveChangesAsync(ct);
        await AuditAsync(db, principal, http, configuration, clock, "decision.proposed", d.Id.ToString(), d.Title);
        return Results.Created($"/api/admin/intel/decisions/{d.Id}", new { id = d.Id });
    }

    private static async Task<IResult> EditDecisionAsync(
        Guid id, DecisionRequest request, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db,
        IConfiguration configuration, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        if (Invalid(request, out var problem)) return problem!;
        var d = await db.ProductDecisions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return Problems.NotFound("NOT_FOUND", "No such decision.");
        if (d.Status is DecisionStatus.Closed or DecisionStatus.Rejected)
            return Problems.Conflict("DECISION_FINAL", "A closed or rejected decision is kept as it was.");
        // What an approved decision says it will do is the Owner's to change.
        if (d.Status != DecisionStatus.Proposed && !principal.IsOwner())
            return Problems.Forbidden("OWNER_ONLY", "Only the Owner can change an approved decision.");

        var now = clock.GetUtcNow();
        d.Describe(request.Title, request.Area, request.Problem, request.Action, now);
        Detail(d, request, now);
        await db.SaveChangesAsync(ct);
        await AuditAsync(db, principal, http, configuration, clock, "decision.edited", id.ToString(), d.Title);
        return Results.NoContent();
    }

    private static async Task<IResult> TransitionAsync(
        Guid id, DecisionTransition request, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db,
        WordOsConfiguration config, IOptions<AdminIntelOptions> options, IConfiguration configuration,
        TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        if (!MiniValidator.TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        var d = await db.ProductDecisions.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null) return Problems.NotFound("NOT_FOUND", "No such decision.");

        var action = request.Action.Trim().ToLowerInvariant();
        // Approving, rejecting and closing are judgements the person
        // accountable for the product makes (brief §11: a proposal is not a
        // decision until the authorised user approves it).
        if (action is "approve" or "reject" or "close" && !principal.IsOwner())
            return Problems.Forbidden("OWNER_ONLY", "Only the Owner can approve, reject or close a decision.");

        var now = clock.GetUtcNow();
        string detail;
        try
        {
            switch (action)
            {
                case "approve":
                    d.Approve(principal.UserId()!.Value, now);
                    detail = "PROPOSED → APPROVED";
                    break;
                case "reject":
                    d.Reject(request.Note, now);
                    detail = "PROPOSED → REJECTED";
                    break;
                case "implement":
                    var on = request.On ?? Today(config, clock);
                    d.MarkImplemented(on, now);
                    detail = $"implemented {on:yyyy-MM-dd}";
                    break;
                case "reschedule":
                    var before = d.ReviewOn;
                    d.Reschedule(request.ReviewOn, now);
                    detail = $"review {before:yyyy-MM-dd} → {request.ReviewOn:yyyy-MM-dd}";
                    break;
                case "outcome":
                    d.RecordOutcome(request.Note, now);
                    detail = "outcome recorded";
                    break;
                case "close":
                    if (!Enum.TryParse<DecisionVerdict>(request.Verdict, true, out var verdict))
                        return Problems.BadRequest("VERDICT_REQUIRED", "Closing needs a verdict.");
                    string? snapshot = null;
                    if (IntelEvaluation.WindowsOf(d, options.Value.EvaluationWindowDays) is { } w)
                    {
                        var data = await LoadAsync(db, config, options, clock, ct);
                        var evaluation = IntelEvaluation.Evaluate(data, d.Metrics, d.Segment, null, w.Before, w.After,
                            await OverlapsAsync(db, w.After, exceptDecision: d.Id, exceptChange: null, ct));
                        snapshot = JsonSerializer.Serialize(evaluation, Web);
                    }
                    d.Close(verdict, request.Note, snapshot, now);
                    detail = $"closed: {verdict}";
                    break;
                default:
                    return Problems.BadRequest("UNKNOWN_ACTION", "Unknown action.");
            }
        }
        catch (InvalidOperationException e)
        {
            return Problems.Conflict("INVALID_TRANSITION", e.Message);
        }

        await db.SaveChangesAsync(ct);
        await AuditAsync(db, principal, http, configuration, clock, $"decision.{action}", id.ToString(), detail);
        return Results.NoContent();
    }

    private static void Detail(ProductDecision d, DecisionRequest r, DateTimeOffset now) =>
        d.Detail(r.Evidence, r.Hypothesis, r.Assignee, r.ExpectedOutcome, r.InquiryId, r.Segment,
            r.BaselineFrom, r.BaselineTo, Normalise(r.Metrics), r.ReviewOn, now);

    private static bool Invalid(DecisionRequest r, out IResult? problem)
    {
        problem = null;
        if (!MiniValidator.TryValidate(r, out var errors)) { problem = Results.ValidationProblem(errors); return true; }
        if (!Areas.Contains(r.Area)) { problem = Problems.BadRequest("UNKNOWN_AREA", "Choose one of the listed areas."); return true; }
        if (BadMetrics(r.Metrics) is { } m) { problem = m; return true; }
        return false;
    }

    // ── Changes and experiments (brief §12–§13) ──────────────────────────────

    private static async Task<IResult> ChangesAsync(
        string? kind, bool? experiments, ClaimsPrincipal principal, WordOsDbContext db, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var query = db.ProductChanges.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(c => c.Kind == kind);
        if (experiments is { } e) query = query.Where(c => c.IsExperiment == e);

        var list = await query.OrderByDescending(c => c.ShippedOn).ThenByDescending(c => c.CreatedAt).Take(300).ToListAsync(ct);
        var names = await NamesAsync(db, list.Select(c => c.AuthorId), ct);
        return Results.Ok(new
        {
            items = list.Select(c => new
            {
                id = c.Id, title = c.Title, kind = c.Kind, isExperiment = c.IsExperiment, version = c.Version,
                shippedOn = c.ShippedOn, segment = c.Segment, comparisonSegment = c.ComparisonSegment,
                decisionId = c.DecisionId, verdict = c.Verdict?.ToString().ToUpperInvariant(),
                metrics = c.Metrics.Select(m => m.Key), author = names.GetValueOrDefault(c.AuthorId),
                concluded = c.Verdict is not null,
            }),
            kinds = ProductChange.Kinds,
        });
    }

    private static async Task<IResult> ChangeAsync(
        Guid id, ClaimsPrincipal principal, WordOsDbContext db, WordOsConfiguration config,
        IOptions<AdminIntelOptions> options, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var c = await db.ProductChanges.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return Problems.NotFound("NOT_FOUND", "No such change.");

        var names = await NamesAsync(db, new[] { c.AuthorId }, ct);
        var decision = c.DecisionId is null ? null : await db.ProductDecisions.AsNoTracking()
            .Where(d => d.Id == c.DecisionId).Select(d => new { id = d.Id, title = d.Title, status = d.Status.ToString().ToUpperInvariant() })
            .FirstOrDefaultAsync(ct);

        var data = await LoadAsync(db, config, options, clock, ct);
        var (before, after) = IntelEvaluation.WindowsOf(c);
        var live = IntelEvaluation.Evaluate(data, c.Metrics, c.Segment, c.ComparisonSegment, before, after,
            await OverlapsAsync(db, after, exceptDecision: null, exceptChange: c.Id, ct));

        return Results.Ok(new
        {
            change = new
            {
                id = c.Id, title = c.Title, kind = c.Kind, isExperiment = c.IsExperiment,
                description = c.Description, problem = c.Problem, hypothesis = c.Hypothesis, version = c.Version,
                shippedOn = c.ShippedOn, segment = c.Segment, comparisonSegment = c.ComparisonSegment,
                segmentLabel = c.Segment is null ? null : Segments.LabelOf(c.Segment),
                comparisonLabel = c.ComparisonSegment is null ? null : Segments.LabelOf(c.ComparisonSegment),
                decisionId = c.DecisionId, metrics = c.Metrics, windowDays = c.WindowDays,
                sideEffects = c.SideEffects, result = c.Result, recommendation = c.Recommendation,
                verdict = c.Verdict?.ToString().ToUpperInvariant(), author = names.GetValueOrDefault(c.AuthorId),
                createdAt = c.CreatedAt, updatedAt = c.UpdatedAt,
            },
            decision,
            evaluation = live,
            frozen = c.EvaluationJson is null ? (object?)null : JsonSerializer.Deserialize<JsonElement>(c.EvaluationJson),
            history = await HistoryAsync(db, id, ct),
        });
    }

    private static async Task<IResult> RecordChangeAsync(
        ChangeRequest request, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db,
        IConfiguration configuration, IOptions<AdminIntelOptions> options, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        if (await InvalidAsync(request, db, ct) is { } problem) return problem;

        var now = clock.GetUtcNow();
        var c = ProductChange.Record(Guid.CreateVersion7(now), principal.UserId()!.Value, now);
        Describe(c, request, options.Value, now);
        db.ProductChanges.Add(c);
        await db.SaveChangesAsync(ct);
        await AuditAsync(db, principal, http, configuration, clock, "change.recorded", c.Id.ToString(), c.Title);
        return Results.Created($"/api/admin/intel/changes/{c.Id}", new { id = c.Id });
    }

    private static async Task<IResult> EditChangeAsync(
        Guid id, ChangeRequest request, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db,
        IConfiguration configuration, IOptions<AdminIntelOptions> options, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        if (await InvalidAsync(request, db, ct) is { } problem) return problem;
        var c = await db.ProductChanges.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return Problems.NotFound("NOT_FOUND", "No such change.");

        Describe(c, request, options.Value, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await AuditAsync(db, principal, http, configuration, clock, "change.edited", id.ToString(), c.Title);
        return Results.NoContent();
    }

    private static async Task<IResult> ConcludeChangeAsync(
        Guid id, ChangeConclusion request, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db,
        WordOsConfiguration config, IOptions<AdminIntelOptions> options, IConfiguration configuration,
        TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        if (!MiniValidator.TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        DecisionVerdict? verdict = null;
        if (!string.IsNullOrWhiteSpace(request.Verdict))
        {
            if (!Enum.TryParse<DecisionVerdict>(request.Verdict, true, out var v))
                return Problems.BadRequest("UNKNOWN_VERDICT", "Unknown verdict.");
            verdict = v;
        }
        var c = await db.ProductChanges.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return Problems.NotFound("NOT_FOUND", "No such change.");

        string? snapshot = null;
        if (request.FreezeEvaluation)
        {
            var data = await LoadAsync(db, config, options, clock, ct);
            var (before, after) = IntelEvaluation.WindowsOf(c);
            snapshot = JsonSerializer.Serialize(IntelEvaluation.Evaluate(data, c.Metrics, c.Segment, c.ComparisonSegment,
                before, after, await OverlapsAsync(db, after, null, c.Id, ct)), Web);
        }
        c.Conclude(request.Result, request.SideEffects, request.Recommendation, verdict, snapshot, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await AuditAsync(db, principal, http, configuration, clock, "change.concluded", id.ToString(), verdict?.ToString() ?? "notes");
        return Results.NoContent();
    }

    private static void Describe(ProductChange c, ChangeRequest r, AdminIntelOptions options, DateTimeOffset now) =>
        c.Describe(r.Title, r.Kind, r.IsExperiment, r.Description, r.Problem, r.Hypothesis, r.Version, r.ShippedOn,
            r.Segment, r.ComparisonSegment, r.DecisionId, Normalise(r.Metrics), r.WindowDays ?? options.EvaluationWindowDays, now);

    private static async Task<IResult?> InvalidAsync(ChangeRequest r, WordOsDbContext db, CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(r, out var errors)) return Results.ValidationProblem(errors);
        if (!ProductChange.Kinds.Contains(r.Kind)) return Problems.BadRequest("UNKNOWN_KIND", "Choose one of the listed kinds.");
        if (BadMetrics(r.Metrics) is { } m) return m;
        if (r.DecisionId is { } d && !await db.ProductDecisions.AnyAsync(x => x.Id == d, ct))
            return Problems.BadRequest("UNKNOWN_DECISION", "No such decision.");
        return null;
    }

    // ── Investigations: follow-up and re-run (brief §10) ─────────────────────

    private static async Task<IResult> FollowUpAsync(
        Guid id, FollowUpRequest request, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db,
        IConfiguration configuration, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var inquiry = await db.AdminInquiries.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (inquiry is null) return Problems.NotFound("NOT_FOUND", "No such investigation.");
        inquiry.FollowUp(request.On);
        await db.SaveChangesAsync(ct);
        await AuditAsync(db, principal, http, configuration, clock, "inquiry.followup", id.ToString(),
            request.On?.ToString("yyyy-MM-dd") ?? "cleared");
        return Results.NoContent();
    }

    /// <summary>
    /// Ask a saved question again over the days since it was asked.
    /// </summary>
    /// <remarks>
    /// The new investigation is a child of the old one, so the thread shows
    /// "then" and "now" side by side; the original is never overwritten.
    /// </remarks>
    private static async Task<IResult> RerunAsync(
        Guid id, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db, WordOsConfiguration config,
        IOptions<AdminIntelOptions> options, IAdminInsightService insight, IConfiguration configuration,
        TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var original = await db.AdminInquiries.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (original is null) return Problems.NotFound("NOT_FOUND", "No such investigation.");
        // An answer saved from WordOS AI has no fixed section to re-run; it is
        // continued in a conversation instead (ADR-133).
        if (!IntelInquiry.Sections.Any(sec => sec.Key == original.Section))
            return Problems.BadRequest("NOT_RERUNNABLE", "Ask WordOS AI again instead.");

        var data = await LoadAsync(db, config, options, clock, ct);
        var inherited = original.FilterJson is null ? new IntelFilter()
            : JsonSerializer.Deserialize<IntelFilter>(original.FilterJson, Web) ?? new IntelFilter();
        var asked = data.Day(original.CreatedAt);
        var filter = inherited with
        {
            From = asked.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            To = data.Day(data.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Days = null,
        };

        var (newId, json) = await AdminIntelEndpoints.InvestigateAndSaveAsync(
            db, data, principal, original.Section, original.Question, filter, original.Id, insight, clock, ct);
        await AuditAsync(db, principal, http, configuration, clock, "inquiry.rerun", newId.ToString(), $"of {id}");
        return Results.Text(json, "application/json");
    }

    // ── Shared ───────────────────────────────────────────────────────────────

    /// <summary>The areas a decision may belong to: the investigator's sections, and "general".</summary>
    private static readonly IReadOnlySet<string> Areas =
        IntelInquiry.Sections.Select(s => s.Key).Where(k => k != IntelInquiry.Discussion).Append("general").ToHashSet();

    private static IReadOnlyList<MetricTarget> Normalise(IReadOnlyList<MetricTarget>? metrics) =>
        (metrics ?? []).Select(m => m with { Direction = m.Direction == "down" ? "down" : "up" })
            .DistinctBy(m => m.Key).ToList();

    private static IResult? BadMetrics(IReadOnlyList<MetricTarget>? metrics)
    {
        if (metrics is null) return null;
        if (metrics.Count > 8) return Problems.BadRequest("TOO_MANY_METRICS", "Eight metrics at most.");
        var unknown = metrics.FirstOrDefault(m => IntelMeasures.Find(m.Key) is null);
        return unknown is null ? null : Problems.BadRequest("UNKNOWN_METRIC", $"Unknown metric '{unknown.Key}'.");
    }

    private static DateOnly Today(WordOsConfiguration config, TimeProvider clock) =>
        DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(config.ReportingUtcOffsetHours)).DateTime);

    private static async Task<Dictionary<Guid, string>> NamesAsync(WordOsDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var set = ids.Where(i => i != Guid.Empty).Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => set.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    /// <summary>
    /// Other changes shipped inside an "after" window — what else could have
    /// moved the numbers (brief §12).
    /// </summary>
    private static async Task<IReadOnlyList<Overlap>> OverlapsAsync(
        WordOsDbContext db, Window after, Guid? exceptDecision, Guid? exceptChange, CancellationToken ct) =>
        await db.ProductChanges.AsNoTracking()
            .Where(c => c.ShippedOn >= after.From && c.ShippedOn <= after.To
                        && (exceptChange == null || c.Id != exceptChange)
                        && (exceptDecision == null || c.DecisionId != exceptDecision))
            .OrderBy(c => c.ShippedOn)
            .Select(c => new Overlap(c.Id, c.Title, c.Kind, c.ShippedOn))
            .ToListAsync(ct);

    /// <summary>A record's history is its slice of the audit trail — one log, not two.</summary>
    private static async Task<object> HistoryAsync(WordOsDbContext db, Guid id, CancellationToken ct)
    {
        var target = id.ToString();
        return await db.AdminAuditEvents.AsNoTracking().Where(a => a.TargetId == target)
            .OrderByDescending(a => a.CreatedAt).Take(100)
            .Select(a => new
            {
                action = a.Action,
                detail = a.Detail,
                at = a.CreatedAt,
                actor = db.Users.Where(u => u.Id == a.ActorId).Select(u => u.DisplayName).FirstOrDefault(),
            })
            .ToListAsync(ct);
    }
}
