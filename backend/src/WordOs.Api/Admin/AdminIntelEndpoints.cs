using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WordOs.Api.Endpoints;
using WordOs.Application.Abstractions;
using WordOs.Domain.Analytics;
using WordOs.Domain.Common;
using WordOs.Domain.Levels;
using WordOs.Domain.Users;
using WordOs.Domain.Words;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Admin;

/// <summary>
/// The admin website's API — product intelligence over learning data (ADR-125).
/// </summary>
/// <remarks>
/// Read-only over the learning pipeline, without exception. Nothing here moves
/// a word, a level or a schedule; the only writes are the admin area's own
/// records — notes, saved investigations, the audit trail. The one tool that
/// does change a learner (bringing a schedule forward) stays where it was, on
/// the Owner-only route, so the analytics surface cannot grow a side effect.
///
/// Authorization is layered as everywhere else in this API: the group policy
/// admits Owner and Analyst, every handler checks again, and contact details
/// are masked for anyone who is not the Owner.
/// </remarks>
public static class AdminIntelEndpoints
{
    public sealed record NoteRequest([property: Required, StringLength(2000, MinimumLength = 1)] string Body);

    public sealed record FeedbackStatusRequest(bool Handled);

    public sealed record InquiryRequest(
        [property: Required, StringLength(32)] string Section,
        [property: Required, StringLength(1000, MinimumLength = 3)] string Question,
        IntelFilter? Filter = null);

    public static IEndpointRouteBuilder MapAdminIntelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/intel")
            .WithTags("Admin Intelligence")
            .RequireAuthorization(Policies.AdminArea);

        group.MapGet("/meta", MetaAsync);
        group.MapGet("/overview", OverviewAsync);
        group.MapGet("/learning", LearningAsync);
        group.MapGet("/skills/{skill}", SkillAsync);
        group.MapGet("/behavior", BehaviorAsync);
        group.MapGet("/friction/{key}", FrictionAsync);
        group.MapGet("/retention", RetentionAsync);
        group.MapGet("/users", UsersAsync);
        group.MapGet("/users/attention", AttentionAsync);
        group.MapGet("/users/{id:guid}", UserAsync);
        group.MapGet("/users/{id:guid}/timeline", TimelineAsync);
        group.MapGet("/cohorts/compare", CompareAsync);
        group.MapGet("/feedback", FeedbackAsync);
        group.MapPost("/feedback/{id:guid}/notes", AddNoteAsync);
        group.MapPatch("/feedback/{id:guid}", SetFeedbackStatusAsync);
        group.MapGet("/inquiries", InquiriesAsync);
        group.MapGet("/inquiries/{id:guid}", InquiryAsync);
        group.MapPost("/inquiries", AskAsync).RequireRateLimiting(RateLimitPolicies.Expensive);
        group.MapGet("/system", SystemAsync);
        group.MapGet("/audit", AuditTrailAsync);

        return app;
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private static IResult? Deny(ClaimsPrincipal principal) =>
        principal.IsOwner() || principal.IsInRole(nameof(UserRole.Analyst))
            ? null
            : Problems.Forbidden("FORBIDDEN", "This area is restricted to administrators.");

    private static Privacy PrivacyFor(ClaimsPrincipal principal) => new(principal.IsOwner());

    private static async Task<IntelDataset> LoadAsync(
        WordOsDbContext db, WordOsConfiguration config, IOptions<AdminIntelOptions> options,
        TimeProvider clock, CancellationToken ct) =>
        await IntelDataset.LoadAsync(db, config, options.Value, clock.GetUtcNow(), ct);

    private static IntelFilter FilterFrom(HttpRequest r)
    {
        string? Q(string k) => r.Query.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.ToString() : null;
        return new IntelFilter(
            From: Q("from"), To: Q("to"),
            Days: int.TryParse(Q("days"), out var d) ? d : null,
            Level: Q("level"), Skill: Q("skill"), Interest: Q("interest"),
            Status: Q("status"), Segment: Q("segment"),
            UserId: Guid.TryParse(Q("userId"), out var u) ? u : null);
    }

    private static object PeriodOf(IntelScope scope) => new
    {
        from = scope.Period.From,
        to = scope.Period.To,
        prevFrom = scope.Period.PrevFrom,
        prevTo = scope.Period.PrevTo,
        days = (int)Math.Round(scope.Period.Length.TotalDays),
        learners = scope.Learners.Count,
    };

    private static async Task AuditAsync(
        WordOsDbContext db, ClaimsPrincipal principal, HttpContext http, IConfiguration configuration,
        TimeProvider clock, string action, string? target = null, string? detail = null)
    {
        var limits = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();
        db.AdminAuditEvents.Add(AdminAuditEvent.Record(
            principal.UserId()!.Value, action, clock.GetUtcNow(), target, detail,
            ClientAddress.Of(http, limits.ClientAddressHeader, limits.ClientAddressEntry)));
        await db.SaveChangesAsync();
    }

    // ── Meta ─────────────────────────────────────────────────────────────────

    private static async Task<IResult> MetaAsync(
        ClaimsPrincipal principal, WordOsDbContext db, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;

        var me = await db.Users.AsNoTracking().FirstAsync(u => u.Id == principal.UserId(), ct);
        var interests = await db.UserInterests.AsNoTracking()
            .GroupBy(i => i.Interest)
            .Select(g => new { interest = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .Take(40)
            .ToListAsync(ct);

        var firstClient = await db.AnalyticsEvents.AsNoTracking()
            .Where(e => e.Source == AnalyticsSource.Client)
            .OrderBy(e => e.OccurredAt).Select(e => (DateTimeOffset?)e.OccurredAt).FirstOrDefaultAsync(ct);
        var firstServer = await db.AnalyticsEvents.AsNoTracking()
            .Where(e => e.Source == AnalyticsSource.Server)
            .OrderBy(e => e.OccurredAt).Select(e => (DateTimeOffset?)e.OccurredAt).FirstOrDefaultAsync(ct);

        return Results.Ok(new
        {
            me = new { id = me.Id, name = me.DisplayName, email = me.Email, role = me.Role.ToWire() },
            canSeeContact = principal.IsOwner(),
            skills = IntelMetrics.Skills.Select(IntelMetrics.Wire),
            levels = new[] { "A1", "A2", "B1", "B2", "C1", "C2" },
            interests,
            segments = Segments.Catalogue.Select(s => new { key = s.Key, label = s.Label }),
            attention = Attention.Catalogue.Select(a => new { key = a.Key, label = a.Label, description = a.Description }),
            funnel = Funnel.Stages.Select(f => new { key = f.Key, label = f.Label, hint = f.Hint }),
            tracking = new { clientSince = firstClient, serverSince = firstServer },
        });
    }

    // ── Overview ─────────────────────────────────────────────────────────────

    private static async Task<IResult> OverviewAsync(
        HttpRequest http, ClaimsPrincipal principal, WordOsDbContext db, WordOsConfiguration config,
        IOptions<AdminIntelOptions> options, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var scope = new IntelScope(await LoadAsync(db, config, options, clock, ct), FilterFrom(http));

        var skills = IntelMetrics.Skills.Select(s => IntelMetrics.SkillSummaryFor(scope, s)).ToList();
        var funnel = IntelMetrics.FunnelFor(scope);
        var friction = IntelMetrics.Friction(scope);

        return Results.Ok(new
        {
            period = PeriodOf(scope),
            health = IntelMetrics.Health(scope),
            engagement = IntelMetrics.Engagement(scope),
            funnel,
            lifecycle = IntelMetrics.LifecycleFor(scope),
            skills,
            signals = IntelMetrics.Signals(scope, skills, funnel, friction),
        });
    }

    // ── Learning & skills ────────────────────────────────────────────────────

    private static async Task<IResult> LearningAsync(
        HttpRequest http, ClaimsPrincipal principal, WordOsDbContext db, WordOsConfiguration config,
        IOptions<AdminIntelOptions> options, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var scope = new IntelScope(await LoadAsync(db, config, options, clock, ct), FilterFrom(http));

        var failures = IntelMetrics.Skills.Select(s => IntelMetrics.FailuresFor(scope, s)).ToList();
        var levels = IntelMetrics.Levels(scope);

        return Results.Ok(new
        {
            period = PeriodOf(scope),
            skills = IntelMetrics.Skills.Select(s => new
            {
                current = IntelMetrics.SkillSummaryFor(scope, s),
                previous = IntelMetrics.SkillSummaryFor(scope, s, previous: true),
            }),
            failures,
            categories = IntelMetrics.Categories(failures),
            levels = new { flows = levels.Flows, bySkill = levels.BySkill, byLevel = levels.ByLevel, distribution = levels.Distribution },
            weeklyReview = IntelMetrics.WeeklyReview(scope),
            mostFailedWords = MostFailedWords(scope, null, 12),
        });
    }

    public sealed record FailedWord(string Text, string Meaning, int Failures, int Users, IReadOnlyList<SkillCount> Skills);

    public sealed record SkillCount(string? Skill, int Count);

    private static IReadOnlyList<FailedWord> MostFailedWords(IntelScope scope, SkillType? skill, int take) =>
        scope.Words
            .Select(w => (w, fails: w.Events.Where(e => e.Type == WordEventType.SkillFailed
                                                         && scope.Period.Contains(e.CreatedAt)
                                                         && (skill == null || e.Skill == skill)).ToList()))
            .Where(x => x.fails.Count > 0)
            .GroupBy(x => x.w.Text.ToLowerInvariant())
            .Select(g => new FailedWord(
                g.First().w.Text,
                g.First().w.Meaning,
                g.Sum(x => x.fails.Count),
                g.Select(x => x.w.UserId).Distinct().Count(),
                g.SelectMany(x => x.fails).GroupBy(e => e.Skill)
                    .Select(k => new SkillCount(k.Key?.ToWire(), k.Count()))
                    .OrderByDescending(k => k.Count).ToList()))
            .OrderByDescending(x => x.Failures)
            .Take(take)
            .ToList();

    /// <summary>
    /// One skill, all the way down (admin brief §30): metric → segment → user → event.
    /// </summary>
    private static async Task<IResult> SkillAsync(
        string skill, HttpRequest http, ClaimsPrincipal principal, WordOsDbContext db, WordOsConfiguration config,
        IOptions<AdminIntelOptions> options, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        if (!Enum.TryParse<SkillType>(skill, ignoreCase: true, out var s) || !Enum.IsDefined(s))
            return Problems.NotFound("SKILL_NOT_FOUND", "No such skill.");

        var data = await LoadAsync(db, config, options, clock, ct);
        var scope = new IntelScope(data, FilterFrom(http) with { Skill = s.ToWire() });
        var privacy = PrivacyFor(principal);

        var sessions = scope.SessionsIn().Where(x => !x.IsPractice).ToList();
        var decisions = scope.Words.SelectMany(w => w.Events.Select(e => (w, e)))
            .Where(x => x.e.Skill == s && scope.Period.Contains(x.e.CreatedAt)
                        && x.e.Type is WordEventType.SkillPassed or WordEventType.SkillFailed)
            .ToList();
        var failures = decisions.Where(x => x.e.Type == WordEventType.SkillFailed).ToList();

        // By user: who carries this skill's failures.
        var byUser = failures.GroupBy(x => x.w.UserId)
            .Select(g => (user: data.LearnerById(g.Key), fails: g.Count(),
                passes: decisions.Count(d => d.w.UserId == g.Key && d.e.Type == WordEventType.SkillPassed)))
            .Where(x => x.user != null)
            .OrderByDescending(x => x.fails)
            .Take(data.Options.MaxListedUsers)
            .Select(x => UserRows.For(data, x.user!, privacy, $"{x.fails} فشل · {x.passes} نجاح"))
            .ToList();

        // By level of the content in use when it happened.
        var byLevel = sessions.GroupBy(x => x.LevelUsed)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var items = g.SelectMany(x => x.Items).Where(i => i.Attempts > 0).ToList();
                return new
                {
                    level = s == SkillType.Spelling ? "—" : g.Key.ToWire(),
                    sessions = g.Count(),
                    firstAttemptAccuracy = IntelMetrics.Ratio(items.Count(i => i.FirstAttemptCorrect == true), items.Count(i => i.FirstAttemptCorrect != null)),
                    abandoned = g.Count(data.IsStale),
                };
            }).ToList();

        // By day: when.
        var first = data.Day(scope.Period.From);
        var last = data.Day(scope.Period.To.AddTicks(-1));
        var daily = new List<object>();
        for (var d = first; d <= last; d = d.AddDays(1))
        {
            var day = d;
            daily.Add(new
            {
                date = day.ToString("yyyy-MM-dd"),
                passed = decisions.Count(x => data.Day(x.e.CreatedAt) == day && x.e.Type == WordEventType.SkillPassed),
                failed = failures.Count(x => data.Day(x.e.CreatedAt) == day),
                sessions = sessions.Count(x => data.Day(x.StartedAt) == day),
            });
        }

        // Attempts: how many tries words needed at this skill, all time.
        var attempts = scope.Words.SelectMany(w => w.Skills).Where(k => k.Skill == s && k.Attempts > 0)
            .GroupBy(k => Math.Min(k.Attempts, 5))
            .OrderBy(g => g.Key)
            .Select(g => new { attempts = g.Key == 5 ? "5+" : g.Key.ToString(), words = g.Count() })
            .ToList();

        // What happened next to the words that failed here.
        var failedWords = failures.Select(x => x.w).DistinctBy(w => w.Id).ToList();
        var next = new
        {
            recovered = failedWords.Count(w => w.Events.Any(e => e.Skill == s && e.Type == WordEventType.SkillPassed
                                                                 && e.CreatedAt > failures.Where(f => f.w.Id == w.Id).Min(f => f.e.CreatedAt))),
            waiting = failedWords.Count(w => w.State == WordState.Learning && w.CurrentSkill == s),
            deleted = failedWords.Count(w => w.State == WordState.Deleted),
            total = failedWords.Count,
        };

        return Results.Ok(new
        {
            period = PeriodOf(scope),
            skill = s.ToWire(),
            summary = IntelMetrics.SkillSummaryFor(scope, s),
            previous = IntelMetrics.SkillSummaryFor(scope, s, previous: true),
            content = IntelMetrics.ContentFor(scope, s),
            failures = IntelMetrics.FailuresFor(scope, s),
            byUser,
            byLevel,
            daily,
            attempts,
            next,
            words = MostFailedWords(scope, s, 15),
            mostTranslated = s == SkillType.Reading
                ? IntelMetrics.MostTranslated(scope).Select(x => new { word = x.Word, count = x.Count })
                : null,
            levelFlows = IntelMetrics.Levels(scope).Flows,
        });
    }

    // ── Behaviour & UX ───────────────────────────────────────────────────────

    private static async Task<IResult> BehaviorAsync(
        HttpRequest http, ClaimsPrincipal principal, WordOsDbContext db, WordOsConfiguration config,
        IOptions<AdminIntelOptions> options, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var scope = new IntelScope(await LoadAsync(db, config, options, clock, ct), FilterFrom(http));
        var events = scope.Events.Where(e => scope.Period.Contains(e.OccurredAt)).ToList();

        return Results.Ok(new
        {
            period = PeriodOf(scope),
            tracked = events.Any(e => e.Source == AnalyticsSource.Client),
            friction = IntelMetrics.Friction(scope),
            time = IntelMetrics.Time(scope),
            content = IntelMetrics.Skills.Select(s => new { skill = s.ToWire(), metrics = IntelMetrics.ContentFor(scope, s) }),
            mostTranslated = IntelMetrics.MostTranslated(scope).Select(x => new { word = x.Word, count = x.Count }),
            screens = events.Where(e => e.Name == AnalyticsEventNames.ScreenLeft && e.Screen != null)
                .GroupBy(e => e.Screen!)
                .Select(g => new
                {
                    screen = g.Key,
                    views = g.Count(),
                    users = g.Select(e => e.UserId).Distinct().Count(),
                    medianMs = IntelMetrics.Median(g.Where(e => e.DurationMs != null).Select(e => (double)e.DurationMs!.Value)),
                    quickExits = g.Count(e => e.DurationMs < scope.Data.Options.ImmediateBackSeconds * 1000),
                })
                .OrderByDescending(x => x.views).ToList(),
            errors = events.Where(e => e.Name == AnalyticsEventNames.ApiError)
                .GroupBy(e => IntelMetrics.Prop<string>(e, "code") ?? "UNKNOWN")
                .Select(g => new { code = g.Key, count = g.Count(), users = g.Select(e => e.UserId).Distinct().Count() })
                .OrderByDescending(x => x.count).ToList(),
        });
    }

    private static async Task<IResult> FrictionAsync(
        string key, HttpRequest http, ClaimsPrincipal principal, WordOsDbContext db, WordOsConfiguration config,
        IOptions<AdminIntelOptions> options, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var data = await LoadAsync(db, config, options, clock, ct);
        var scope = new IntelScope(data, FilterFrom(http));
        var item = IntelMetrics.Friction(scope).FirstOrDefault(f => f.Key == key);
        if (item is null) return Problems.NotFound("NOT_FOUND", "No such friction point.");

        var events = scope.Events.Where(e => scope.Period.Contains(e.OccurredAt)).ToList();
        var sessions = scope.SessionsIn().Where(s => !s.IsPractice).ToList();

        // The sessions this friction point is about, so each can be followed to
        // whether the learner finished it.
        var (sessionIds, users) = key switch
        {
            "listening_replay" => Of(events.Where(e => e.Name == AnalyticsEventNames.AudioReplayed)),
            "reading_translation" => Of(events.Where(e => e.Name == AnalyticsEventNames.TranslationOpened)),
            "exercise_exit" => Of(events.Where(e => e.Name == AnalyticsEventNames.ExerciseExited)),
            "immediate_back" => Of(events.Where(e => e.Name == AnalyticsEventNames.ScreenLeft && e.SessionId != null
                                                     && e.DurationMs < data.Options.ImmediateBackSeconds * 1000)),
            "hint_usage" => Of(events.Where(e => e.Name == AnalyticsEventNames.HintUsed)),
            "long_idle" => Of(events.Where(e => e.DurationMs > data.Options.LongIdleSeconds * 1000
                                                && e.Name is AnalyticsEventNames.AnswerSubmitted or AnalyticsEventNames.WritingEvaluated
                                                    or AnalyticsEventNames.SpeakingTurn)),
            "api_errors" => Of(events.Where(e => e.Name == AnalyticsEventNames.ApiError)),
            "empty_states" => Of(events.Where(e => e.Name == AnalyticsEventNames.EmptyStateShown)),
            "ai_errors" => (sessions.Where(s => s.UsedAiFallback).Select(s => s.Id).ToHashSet(),
                events.Where(e => e.Name == AnalyticsEventNames.AiCall && e.Result == "error").Select(e => e.UserId)
                    .Concat(sessions.Where(s => s.UsedAiFallback).Select(s => s.UserId)).ToHashSet()),
            "retry" => (sessions.Where(s => s.Items.Any(i => i.Attempts > 1)).Select(s => s.Id).ToHashSet(),
                sessions.Where(s => s.Items.Any(i => i.Attempts > 1)).Select(s => s.UserId).ToHashSet()),
            _ when key.StartsWith("abandon_") => Abandoned(scope, events, sessions, item.Skill),
            _ => (new HashSet<Guid>(), new HashSet<Guid>()),
        };

        var affected = sessions.Where(s => sessionIds.Contains(s.Id)).ToList();
        var abandonedIds = events.Where(e => e.Name == AnalyticsEventNames.SessionAbandoned && e.SessionId != null)
            .Select(e => e.SessionId!.Value).ToHashSet();
        var privacy = PrivacyFor(principal);

        return Results.Ok(new
        {
            period = PeriodOf(scope),
            item,
            users = users.Select(data.LearnerById).OfType<User>()
                .Take(data.Options.MaxListedUsers)
                .Select(u => UserRows.For(data, u, privacy,
                    $"{events.Count(e => e.UserId == u.Id && sessionIds.Contains(e.SessionId ?? Guid.Empty))} حدثًا"))
                .ToList(),
            outcome = new
            {
                completed = affected.Count(s => s.IsComplete),
                open = affected.Count(s => !s.IsComplete && !data.IsStale(s)),
                abandoned = affected.Count(data.IsStale) + sessionIds.Count(abandonedIds.Contains),
            },
            byLevel = affected.GroupBy(s => s.Skill == SkillType.Spelling ? "—" : s.LevelUsed.ToWire())
                .Select(g => new { level = g.Key, sessions = g.Count() }).OrderBy(x => x.level),
            bySkill = affected.GroupBy(s => s.Skill.ToWire())
                .Select(g => new { skill = g.Key, sessions = g.Count() }),
            byDay = affected.GroupBy(s => data.Day(s.StartedAt).ToString("yyyy-MM-dd"))
                .Select(g => new { date = g.Key, sessions = g.Count() }).OrderBy(x => x.date),
            performance = new
            {
                affectedAccuracy = IntelMetrics.Ratio(
                    affected.SelectMany(s => s.Items).Count(i => i.FirstAttemptCorrect == true),
                    affected.SelectMany(s => s.Items).Count(i => i.FirstAttemptCorrect != null)),
                othersAccuracy = IntelMetrics.Ratio(
                    sessions.Except(affected).SelectMany(s => s.Items).Count(i => i.FirstAttemptCorrect == true),
                    sessions.Except(affected).SelectMany(s => s.Items).Count(i => i.FirstAttemptCorrect != null)),
            },
            content = affected.Where(s => s.ContentTitle != null)
                .GroupBy(s => s.ContentTitle!)
                .Select(g => new { title = g.Key, sessions = g.Count(), level = g.First().LevelUsed.ToWire() })
                .OrderByDescending(x => x.sessions).Take(10),
        });

        static (HashSet<Guid>, HashSet<Guid>) Of(IEnumerable<AnalyticsEvent> evs)
        {
            var list = evs.ToList();
            return (list.Where(e => e.SessionId != null).Select(e => e.SessionId!.Value).ToHashSet(),
                list.Select(e => e.UserId).ToHashSet());
        }

        static (HashSet<Guid>, HashSet<Guid>) Abandoned(IntelScope scope, List<AnalyticsEvent> events,
            List<WordOs.Domain.Sessions.SkillSession> sessions, string? skill)
        {
            var s = Enum.Parse<SkillType>(skill!, ignoreCase: true);
            var explicitly = events.Where(e => e.Name == AnalyticsEventNames.SessionAbandoned && e.Skill == s).ToList();
            var stale = sessions.Where(x => x.Skill == s && scope.Data.IsStale(x)).ToList();
            return (explicitly.Select(e => e.SessionId ?? Guid.Empty).Concat(stale.Select(x => x.Id)).ToHashSet(),
                explicitly.Select(e => e.UserId).Concat(stale.Select(x => x.UserId)).ToHashSet());
        }
    }

    // ── Retention ────────────────────────────────────────────────────────────

    private static async Task<IResult> RetentionAsync(
        HttpRequest http, ClaimsPrincipal principal, WordOsDbContext db, WordOsConfiguration config,
        IOptions<AdminIntelOptions> options, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var data = await LoadAsync(db, config, options, clock, ct);
        var scope = new IntelScope(data, FilterFrom(http));
        var p = scope.Period;

        var activeInPeriod = scope.Learners.Where(u => data.ActiveDaysOf(u.Id).Any(d => p.Contains(data.StartOf(d)))).ToList();
        var returning = activeInPeriod.Count(u => data.ActiveDaysOf(u.Id).Any(d => data.StartOf(d) < p.From));

        var samples = IntelMetrics.RecallSamples(scope);

        return Results.Ok(new
        {
            period = PeriodOf(scope),
            kpis = new List<Kpi>
            {
                new("d1", "D1 Retention", IntelMetrics.RetentionAt(scope, 1, p.To), IntelMetrics.RetentionAt(scope, 1, p.PrevTo), "pct",
                    "عادوا في اليوم الأول بعد التسجيل أو بعده"),
                new("d7", "D7 Retention", IntelMetrics.RetentionAt(scope, 7, p.To), IntelMetrics.RetentionAt(scope, 7, p.PrevTo), "pct",
                    "عادوا بعد 7 أيام أو أكثر"),
                new("d30", "D30 Retention", IntelMetrics.RetentionAt(scope, 30, p.To), IntelMetrics.RetentionAt(scope, 30, p.PrevTo), "pct",
                    "عادوا بعد 30 يومًا أو أكثر"),
                new("returning", "مستخدمون عائدون", returning, null, "int", "نشطون في الفترة وكانوا نشطين قبلها"),
                new("recall_success", "Recall Success", IntelMetrics.Ratio(samples.Count(x => x.Success), samples.Count), null, "pct",
                    "تذكّر الكلمة من المحاولة الأولى عند عودتها"),
                new("recall_failure", "Recall Failure", IntelMetrics.Ratio(samples.Count(x => !x.Success), samples.Count), null, "pct"),
            },
            cohorts = IntelMetrics.Cohorts(scope),
            recall = new
            {
                curve = IntelMetrics.RecallCurve(samples),
                bySkill = IntelMetrics.Skills.Select(s => new
                {
                    skill = s.ToWire(),
                    curve = IntelMetrics.RecallCurve(samples.Where(x => x.Skill == s)),
                }),
                review = IntelMetrics.RecallCurve(samples.Where(x => x.Source == "review")),
                samples = samples.Count,
                configuredGapDays = config.SkillIntervalDays,
            },
            recovery = IntelMetrics.Recovery(scope),
            weeklyReview = IntelMetrics.WeeklyReview(scope),
        });
    }

    // ── Users ────────────────────────────────────────────────────────────────

    private static async Task<IResult> UsersAsync(
        string? q, string? sort, HttpRequest http, ClaimsPrincipal principal, WordOsDbContext db,
        WordOsConfiguration config, IOptions<AdminIntelOptions> options, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var data = await LoadAsync(db, config, options, clock, ct);
        var scope = new IntelScope(data, FilterFrom(http));
        var privacy = PrivacyFor(principal);

        var learners = scope.Learners.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            learners = learners.Where(u =>
                u.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                // A masked viewer may search by name only: searching by an
                // address they cannot see would confirm it one letter at a time.
                || (privacy.CanSeeContact && (u.Email.Contains(term, StringComparison.OrdinalIgnoreCase)
                                              || (u.PhoneNumber ?? "").Contains(term))));
        }

        var rows = learners.Select(u => UserRows.For(data, u, privacy)).ToList();
        rows = (sort ?? "last_active") switch
        {
            "joined" => rows.OrderByDescending(r => r.JoinedAt).ToList(),
            "active_days" => rows.OrderByDescending(r => r.ActiveDays).ToList(),
            "words" => rows.OrderByDescending(r => r.Words).ToList(),
            "mastered" => rows.OrderByDescending(r => r.Mastered).ToList(),
            "name" => rows.OrderBy(r => r.Name).ToList(),
            _ => rows.OrderByDescending(r => r.LastActiveAt ?? DateTimeOffset.MinValue).ToList(),
        };

        return Results.Ok(new
        {
            total = rows.Count,
            segment = scope.Filter.Segment is { } seg ? Segments.LabelOf(seg) : null,
            rows = rows.Take(data.Options.MaxListedUsers),
        });
    }

    private static async Task<IResult> AttentionAsync(
        HttpRequest http, ClaimsPrincipal principal, WordOsDbContext db, WordOsConfiguration config,
        IOptions<AdminIntelOptions> options, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var data = await LoadAsync(db, config, options, clock, ct);
        var scope = new IntelScope(data, FilterFrom(http));
        var privacy = PrivacyFor(principal);

        var flagged = scope.Learners.Select(u => (u, reasons: Attention.Reasons(data, u))).Where(x => x.reasons.Count > 0).ToList();

        return Results.Ok(new
        {
            total = flagged.Count,
            groups = Attention.Catalogue.Select(c => new
            {
                key = c.Key,
                label = c.Label,
                description = c.Description,
                users = flagged.Where(x => x.reasons.Any(r => r.Key == c.Key))
                    .Select(x => UserRows.For(data, x.u, privacy, x.reasons.First(r => r.Key == c.Key).Evidence))
                    .ToList(),
            }).Where(g => g.users.Count > 0),
        });
    }

    /// <summary>User 360 (admin brief §18–§21).</summary>
    private static async Task<IResult> UserAsync(
        Guid id, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db, WordOsConfiguration config,
        IOptions<AdminIntelOptions> options, IConfiguration configuration, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var data = await LoadAsync(db, config, options, clock, ct);
        var user = data.LearnerById(id);
        if (user is null) return Problems.NotFound("USER_NOT_FOUND", "No such learner.");

        // Reading one person's whole history is the act the audit trail exists for.
        await AuditAsync(db, principal, http, configuration, clock, "user.viewed", id.ToString());

        var privacy = PrivacyFor(principal);
        var now = data.Now;
        var words = data.WordsOf(id);
        var kept = words.Where(w => w.State != WordState.Deleted).ToList();
        var sessions = data.SessionsOf(id);
        var events = data.EventsOf(id);
        var scope = new IntelScope(data, new IntelFilter(UserId: id, Days: 3650));

        object PeriodBlock(string key, string label, DateTimeOffset since)
        {
            var wordEvents = words.SelectMany(w => w.Events).Where(e => e.CreatedAt >= since).ToList();
            return new
            {
                key,
                label,
                added = words.Count(w => w.AddedAt >= since),
                passed = wordEvents.Count(e => e.Type == WordEventType.SkillPassed),
                failed = wordEvents.Count(e => e.Type == WordEventType.SkillFailed),
                mastered = wordEvents.Count(e => e.Type == WordEventType.BecameMature),
                archived = wordEvents.Count(e => e.Type == WordEventType.Archived),
                sessions = sessions.Count(s => s.StartedAt >= since && !s.IsPractice),
            };
        }

        var today = data.StartOf(data.Day(now));
        var lastClient = events.Where(e => e.Source == AnalyticsSource.Client).MaxBy(e => e.OccurredAt);

        var activity = new List<object>();
        for (var d = data.Day(now).AddDays(-89); d <= data.Day(now); d = d.AddDays(1))
        {
            var day = d;
            activity.Add(new
            {
                date = day.ToString("yyyy-MM-dd"),
                count = data.ActivityOf(id).Count(t => data.Day(t) == day),
            });
        }

        return Results.Ok(new
        {
            header = new
            {
                row = UserRows.For(data, user, privacy),
                email = privacy.Email(user.Email),
                phone = privacy.Phone(user),
                whatsapp = privacy.Phone(user) is { } phone ? $"https://wa.me/{phone.TrimStart('+')}" : null,
                onboarding = user.OnboardingStage.ToWire(),
                interests = user.Interests.Select(i => i.Interest),
                appVersion = lastClient?.AppVersion,
                platform = lastClient?.Platform,
                funnelStage = Funnel.StageOf(data, user),
            },
            states = new
            {
                total = kept.Count,
                learning = kept.Count(w => w.State == WordState.Learning),
                mastered = kept.Count(w => w.MaturedAt is not null),
                active = kept.Count(w => w.State == WordState.Active),
                archived = kept.Count(w => w.State == WordState.Archived),
                deleted = words.Count(w => w.State == WordState.Deleted),
                due = kept.Count(w => Attention.DueSince(data, w) is not null),
                overdue = Attention.Overdue(data, kept),
            },
            periods = new[]
            {
                PeriodBlock("today", "اليوم", today),
                PeriodBlock("week", "هذا الأسبوع", now.AddDays(-7)),
                PeriodBlock("month", "هذا الشهر", now.AddDays(-30)),
                PeriodBlock("all", "منذ التسجيل", DateTimeOffset.MinValue),
            },
            skills = IntelMetrics.Skills.Select(s =>
            {
                var summary = IntelMetrics.SkillSummaryFor(scope, s);
                var level = user.SkillLevels.FirstOrDefault(l => l.Skill == s);
                return new
                {
                    summary,
                    selectedLevel = level?.UserSelectedLevel?.ToWire(),
                    assessedLevel = level?.SystemAssessedLevel?.ToWire(),
                };
            }),
            words = words.OrderByDescending(w => w.AddedAt).Select(w =>
            {
                var due = Attention.DueSince(data, w);
                return new
                {
                    id = w.Id,
                    text = w.Text,
                    meaning = w.Meaning,
                    state = w.State.ToWire(),
                    currentSkill = w.CurrentSkill?.ToWire(),
                    addedAt = w.AddedAt,
                    exposures = w.ExposureCount,
                    due = due is not null,
                    overdue = due is not null && now - due.Value >= TimeSpan.FromDays(data.Options.OverdueDays),
                    failures = w.Events.Count(e => e.Type == WordEventType.SkillFailed),
                    skills = IntelMetrics.Skills.Select(s =>
                    {
                        var st = w.Skills.FirstOrDefault(k => k.Skill == s);
                        var failed = w.Events.Count(e => e.Skill == s && e.Type == WordEventType.SkillFailed);
                        return new
                        {
                            skill = s.ToWire(),
                            status = st?.EffectiveStatus(now).ToWire(),
                            attempts = st?.Attempts ?? 0,
                            failures = failed,
                        };
                    }),
                };
            }),
            levels = data.LevelChanges.Where(l => l.UserId == id).OrderByDescending(l => l.CreatedAt).Select(l => new
            {
                skill = l.Skill.ToWire(),
                from = l.PreviousLevel?.ToWire(),
                to = l.NewLevel?.ToWire(),
                type = l.ChangeType.ToWire(),
                reason = l.Reason,
                at = l.CreatedAt,
            }),
            feedback = data.Feedback.Where(f => f.UserId == id).OrderByDescending(f => f.CreatedAt).Select(f => new
            {
                id = f.Id,
                body = f.Body,
                category = f.Category?.ToWire(),
                status = f.Status.ToWire(),
                createdAt = f.CreatedAt,
            }),
            attention = Attention.Reasons(data, user).Select(r => new { key = r.Key, label = Attention.LabelOf(r.Key), evidence = r.Evidence }),
            activity,
        });
    }

    private static async Task<IResult> TimelineAsync(
        Guid id, bool? verbose, ClaimsPrincipal principal, WordOsDbContext db, WordOsConfiguration config,
        IOptions<AdminIntelOptions> options, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var data = await LoadAsync(db, config, options, clock, ct);
        var user = data.LearnerById(id);
        if (user is null) return Problems.NotFound("USER_NOT_FOUND", "No such learner.");

        return Results.Ok(new { events = IntelTimeline.For(data, user, verbose == true) });
    }

    // ── Cohorts ──────────────────────────────────────────────────────────────

    private static async Task<IResult> CompareAsync(
        string? a, string? b, HttpRequest http, ClaimsPrincipal principal, WordOsDbContext db,
        WordOsConfiguration config, IOptions<AdminIntelOptions> options, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var data = await LoadAsync(db, config, options, clock, ct);
        var baseFilter = FilterFrom(http);

        object Describe(string key)
        {
            var scope = new IntelScope(data, baseFilter with { Segment = key == "all" ? null : key });
            var learners = scope.Learners;
            var skills = IntelMetrics.Skills.Select(s => IntelMetrics.SkillSummaryFor(scope, s)).ToList();
            double? Avg(Func<User, double> f) => learners.Count == 0 ? null : learners.Average(f);

            return new
            {
                key,
                label = Segments.LabelOf(key),
                size = learners.Count,
                metrics = new List<Metric>
                {
                    new("active_days", "متوسط أيام النشاط", Avg(u => data.ActiveDaysOf(u.Id).Count), "num"),
                    new("sessions", "متوسط Sessions", Avg(u => data.SessionsOf(u.Id).Count(s => !s.IsPractice)), "num"),
                    new("words", "متوسط الكلمات", Avg(u => data.WordsOf(u.Id).Count(w => w.State != WordState.Deleted)), "num"),
                    new("mastered", "متوسط الكلمات المتقنة", Avg(u => data.WordsOf(u.Id).Count(w => w.MaturedAt != null)), "num"),
                    new("d7", "D7 Retention", IntelMetrics.RetentionAt(scope, 7, data.Now), "pct"),
                    new("abandonment", "الانسحاب", IntelMetrics.Ratio(skills.Sum(s => s.Abandoned), skills.Sum(s => s.Sessions)), "pct"),
                },
                skills = skills.Select(s => new { s.Skill, s.FirstAttemptAccuracy, s.PassRate, s.AbandonmentRate }),
            };
        }

        return Results.Ok(new { a = Describe(a ?? "all"), b = Describe(b ?? "joined:30") });
    }

    // ── Feedback ─────────────────────────────────────────────────────────────

    private static async Task<IResult> FeedbackAsync(
        string? status, string? category, ClaimsPrincipal principal, WordOsDbContext db, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var privacy = PrivacyFor(principal);

        var all = await db.FeedbackMessages.AsNoTracking().OrderByDescending(f => f.CreatedAt).Take(500).ToListAsync(ct);
        var userIds = all.Select(f => f.UserId).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
        var ids = all.Select(f => f.Id).ToList();
        var notes = (await db.AdminNotes.AsNoTracking().Where(n => ids.Contains(n.FeedbackId)).OrderBy(n => n.CreatedAt).ToListAsync(ct))
            .ToLookup(n => n.FeedbackId);
        var authorIds = notes.SelectMany(g => g).Select(n => n.AuthorId).Distinct().ToList();
        var authors = await db.Users.AsNoTracking().Where(u => authorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        var filtered = all.Where(f =>
            (string.IsNullOrWhiteSpace(status) || f.Status.ToWire().Equals(status, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(category) || (f.Category?.ToWire() ?? "UNCATEGORISED").Equals(category, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return Results.Ok(new
        {
            summary = new
            {
                total = all.Count,
                unread = all.Count(f => f.Status == FeedbackStatus.New),
                lastWeek = all.Count(f => f.CreatedAt > DateTimeOffset.UtcNow.AddDays(-7)),
                byCategory = all.GroupBy(f => f.Category?.ToWire() ?? "UNCATEGORISED")
                    .Select(g => new { category = g.Key, count = g.Count(), unread = g.Count(f => f.Status == FeedbackStatus.New) })
                    .OrderByDescending(x => x.count),
            },
            repeated = FeedbackThemes.Find(all),
            items = filtered.Select(f => new
            {
                id = f.Id,
                body = f.Body,
                category = f.Category?.ToWire(),
                status = f.Status.ToWire(),
                createdAt = f.CreatedAt,
                handledAt = f.HandledAt,
                appVersion = f.AppVersion,
                platform = f.Platform,
                user = users.TryGetValue(f.UserId, out var u)
                    ? new { id = u.Id, name = u.DisplayName, email = privacy.Email(u.Email), phone = privacy.Phone(u) }
                    : null,
                notes = notes[f.Id].Select(n => new
                {
                    id = n.Id,
                    body = n.Body,
                    author = authors.GetValueOrDefault(n.AuthorId, "—"),
                    createdAt = n.CreatedAt,
                }),
            }),
        });
    }

    private static async Task<IResult> AddNoteAsync(
        Guid id, NoteRequest request, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db,
        IConfiguration configuration, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        if (!MiniValidator.TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!await db.FeedbackMessages.AnyAsync(f => f.Id == id, ct))
            return Problems.NotFound("NOT_FOUND", "That message does not exist.");

        var note = AdminNote.Create(id, principal.UserId()!.Value, request.Body, clock.GetUtcNow());
        db.AdminNotes.Add(note);
        await db.SaveChangesAsync(ct);
        await AuditAsync(db, principal, http, configuration, clock, "feedback.note", id.ToString());

        return Results.Ok(new { id = note.Id, body = note.Body, createdAt = note.CreatedAt });
    }

    private static async Task<IResult> SetFeedbackStatusAsync(
        Guid id, FeedbackStatusRequest request, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db,
        IConfiguration configuration, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var message = await db.FeedbackMessages.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (message is null) return Problems.NotFound("NOT_FOUND", "That message does not exist.");

        message.SetHandled(request.Handled, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        await AuditAsync(db, principal, http, configuration, clock,
            request.Handled ? "feedback.handled" : "feedback.reopened", id.ToString());

        return Results.Ok(new { id = message.Id, status = message.Status.ToWire(), handledAt = message.HandledAt });
    }

    // ── Inquiries ────────────────────────────────────────────────────────────

    private static async Task<IResult> InquiriesAsync(ClaimsPrincipal principal, WordOsDbContext db, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;

        var items = await db.AdminInquiries.AsNoTracking()
            .OrderByDescending(i => i.CreatedAt).Take(100)
            .Select(i => new
            {
                id = i.Id,
                section = i.Section,
                question = i.Question,
                summary = i.Summary,
                interpretedBy = i.InterpretedBy,
                createdAt = i.CreatedAt,
                author = db.Users.Where(u => u.Id == i.AuthorId).Select(u => u.DisplayName).FirstOrDefault(),
            })
            .ToListAsync(ct);

        return Results.Ok(new { items });
    }

    private static async Task<IResult> InquiryAsync(Guid id, ClaimsPrincipal principal, WordOsDbContext db, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var inquiry = await db.AdminInquiries.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);
        if (inquiry is null) return Problems.NotFound("NOT_FOUND", "No such investigation.");

        return Results.Text(inquiry.ResultJson, "application/json");
    }

    private static async Task<IResult> AskAsync(
        InquiryRequest request, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db,
        WordOsConfiguration config, IOptions<AdminIntelOptions> options, IAdminInsightService insight,
        IConfiguration configuration, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        if (!MiniValidator.TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        if (!IntelInquiry.Sections.Any(s => s.Key == request.Section))
            return Problems.BadRequest("UNKNOWN_SECTION", "Choose one of the listed sections.");

        var data = await LoadAsync(db, config, options, clock, ct);
        var scope = new IntelScope(data, request.Filter ?? new IntelFilter());
        var me = await db.Users.AsNoTracking().FirstAsync(u => u.Id == principal.UserId(), ct);

        var result = await IntelInquiry.InvestigateAsync(
            scope, request.Section, request.Question.Trim(), PrivacyFor(principal), insight, ct);

        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7(now);
        var full = result with { Id = id, Author = me.DisplayName, CreatedAt = now };
        var json = JsonSerializer.Serialize(full, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        // Saved under the id the admin was shown, so the link they copy opens it.
        db.AdminInquiries.Add(AdminInquiry.Create(id, me.Id, request.Section, request.Question,
            result.Summary, result.InterpretedBy, json, now));
        await db.SaveChangesAsync(ct);
        await AuditAsync(db, principal, http, configuration, clock, "inquiry.created", id.ToString(), request.Section);

        return Results.Text(json, "application/json");
    }

    // ── System ───────────────────────────────────────────────────────────────

    private static async Task<IResult> SystemAsync(
        ClaimsPrincipal principal, WordOsDbContext db, WordOsConfiguration config,
        IOptions<AdminIntelOptions> options, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var now = clock.GetUtcNow();

        var recent = await db.AnalyticsEvents.AsNoTracking()
            .Where(e => e.OccurredAt > now.AddDays(-1))
            .GroupBy(e => new { e.Name, e.Source })
            .Select(g => new { name = g.Key.Name, source = g.Key.Source, count = g.Count() })
            .ToListAsync(ct);

        var versions = await db.AnalyticsEvents.AsNoTracking()
            .Where(e => e.AppVersion != null && e.OccurredAt > now.AddDays(-30))
            .GroupBy(e => new { e.AppVersion, e.Platform })
            .Select(g => new { version = g.Key.AppVersion, platform = g.Key.Platform, users = g.Select(e => e.UserId).Distinct().Count() })
            .ToListAsync(ct);

        var admins = await db.Users.AsNoTracking()
            .Where(u => u.Role != UserRole.User)
            .Select(u => new { name = u.DisplayName, email = u.Email, role = u.Role })
            .ToListAsync(ct);

        return Results.Ok(new
        {
            configuration = new
            {
                skillIntervalDays = config.SkillIntervalDays,
                weeklyReviewPeriodDays = config.WeeklyReviewPeriodDays,
                weeklyReviewMaturityDays = config.WeeklyReviewMaturityDays,
                maxAttemptsPerItem = config.MaxAttemptsPerItem,
                reportingUtcOffsetHours = config.ReportingUtcOffsetHours,
            },
            thresholds = options.Value,
            tracking = recent.OrderByDescending(r => r.count)
                .Select(r => new { r.name, source = r.source.ToWire(), r.count }),
            versions,
            admins = admins.Select(a => new
            {
                a.name,
                email = principal.IsOwner() ? a.email : new Privacy(false).Email(a.email),
                role = a.role.ToWire(),
            }),
        });
    }

    private static async Task<IResult> AuditTrailAsync(ClaimsPrincipal principal, WordOsDbContext db, CancellationToken ct)
    {
        // Who looked at whom is itself sensitive: Owner only.
        if (!principal.IsOwner())
            return Problems.Forbidden("FORBIDDEN", "The audit trail is restricted to the system owner.");

        var items = await db.AdminAuditEvents.AsNoTracking()
            .OrderByDescending(a => a.CreatedAt).Take(200)
            .Select(a => new
            {
                id = a.Id,
                action = a.Action,
                targetId = a.TargetId,
                detail = a.Detail,
                address = a.ClientAddress,
                createdAt = a.CreatedAt,
                actor = db.Users.Where(u => u.Id == a.ActorId).Select(u => u.DisplayName).FirstOrDefault(),
            })
            .ToListAsync(ct);

        return Results.Ok(new { items });
    }
}
