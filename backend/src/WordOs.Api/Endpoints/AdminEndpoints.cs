using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Words;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Endpoints;

/// <summary>
/// Owner-only analytics.
/// </summary>
/// <remarks>
/// Authorization is enforced <b>here</b>, not by hiding a nav item in Flutter.
/// Two independent layers on purpose (docs/07-SECURITY.md §3):
///
/// <list type="number">
/// <item><c>RequireAuthorization(Policies.OwnerOnly)</c> on the group;</item>
/// <item>an explicit role check inside every handler, so a refactor that drops
/// the attribute cannot silently open the data.</item>
/// </list>
/// </remarks>
public static class AdminEndpoints
{
    public sealed record AdminUserSummaryResponse(
        Guid Id,
        string DisplayName,
        string Email,
        string Role,
        DateTimeOffset CreatedAt,
        DateTimeOffset? LastActiveAt,
        int WordsTotal,
        int WordsActive);

    public sealed record AdminOverviewResponse(
        int UserCount,
        int ActiveToday,
        int ActiveThisWeek,
        int WordsAddedTotal,
        double AverageWordsPerUserPerDay,
        double AverageSessionsPerUser,
        int AverageSessionDurationMs,
        double PipelineCompletionRate,
        IReadOnlyList<SkillStatResponse> SkillStats,
        IReadOnlyList<LevelDistributionResponse> LevelDistributions,
        IReadOnlyList<InterestCountResponse> TopInterests,
        double AiFallbackRate);

    /// <param name="FirstAttemptPasses">
    /// Words passed on their first attempt at this skill — the headline
    /// "first attempt accuracy" figure. Counted from word events rather than
    /// from a running total, so it survives a recount.
    /// </param>
    public sealed record SkillStatResponse(
        string Skill,
        int SessionsCompleted,
        int WordsPassed,
        int WordsFailed,
        int FirstAttemptPasses);

    public sealed record LevelDistributionResponse(
        string Skill,
        Dictionary<string, int> Counts);

    public sealed record InterestCountResponse(
        string Interest,
        int UserCount,
        bool IsCustom);

    public static IEndpointRouteBuilder MapAdminEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin")
            .WithTags("Admin")
            .RequireAuthorization(Policies.OwnerOnly);

        group.MapGet("/overview", OverviewAsync);
        group.MapGet("/users", UsersAsync);
        group.MapGet("/users/{id:guid}", UserDetailAsync);

        return app;
    }

    /// <summary>
    /// The second, defence-in-depth check. Redundant with the policy by design.
    /// </summary>
    private static IResult? RequireOwner(ClaimsPrincipal principal) =>
        principal.IsOwner()
            ? null
            : Problems.Forbidden(
                "FORBIDDEN", "This area is restricted to the system owner.");

    private static async Task<IResult> OverviewAsync(
        ClaimsPrincipal principal,
        WordOsDbContext db,
        WordOsConfiguration config,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (RequireOwner(principal) is { } denied) return denied;

        var now = clock.GetUtcNow();

        // Explicitly UTC. `now.Date` yields a local-kind DateTime, which
        // converts back to a DateTimeOffset carrying the machine's offset —
        // and Npgsql refuses anything but UTC for `timestamptz`. It also means
        // "today" would shift with the server's timezone, which is not a
        // property an analytics figure should have.
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var weekAgo = now.AddDays(-7);

        var learners = db.Users.Where(u => u.Role == UserRole.User);

        var userCount = await learners.CountAsync(ct);
        var activeToday = await learners
            .CountAsync(u => u.LastLoginAt != null && u.LastLoginAt >= today, ct);
        var activeThisWeek = await learners
            .CountAsync(u => u.LastLoginAt != null && u.LastLoginAt >= weekAgo, ct);

        var wordsTotal = await db.Words.CountAsync(ct);
        var wordsActive = await db.Words
            .CountAsync(w => w.State == WordState.Active, ct);

        var levelRows = await db.SkillLevels
            .Where(l => l.SystemAssessedLevel != null)
            .GroupBy(l => new { l.Skill, Level = l.SystemAssessedLevel!.Value })
            .Select(g => new { g.Key.Skill, g.Key.Level, Count = g.Count() })
            .ToListAsync(ct);

        var distributions = levelRows
            .GroupBy(r => r.Skill)
            .Select(g => new LevelDistributionResponse(
                g.Key.ToWire(),
                g.ToDictionary(r => r.Level.ToWire(), r => r.Count)))
            .ToList();

        // Grouped and counted in SQL, then shaped in memory: constructing a
        // record inside the projection is not translatable, and letting EF fail
        // over to client evaluation would pull the whole table back.
        var interestRows = await db.UserInterests
            .GroupBy(i => new { i.Interest, i.IsCustom })
            .Select(g => new
            {
                g.Key.Interest,
                g.Key.IsCustom,
                Count = g.Count(),
            })
            .OrderByDescending(x => x.Count)
            .Take(20)
            .ToListAsync(ct);

        var interests = interestRows
            .Select(x => new InterestCountResponse(x.Interest, x.Count, x.IsCustom))
            .ToList();

        // ── Per-skill outcomes, from the word event log ─────────────────────
        //
        // Counted from events rather than from the words' current state: a word
        // that failed Reading twice and then passed is two failures and one
        // pass, and its current row remembers none of that. The event log is
        // append-only, which is what makes these figures reproducible.
        var skillEvents = await db.WordEvents
            .Where(e => e.Skill != null
                        && (e.Type == WordEventType.SkillPassed
                            || e.Type == WordEventType.SkillFailed))
            .GroupBy(e => new { Skill = e.Skill!.Value, e.Type })
            .Select(g => new { g.Key.Skill, g.Key.Type, Count = g.Count() })
            .ToListAsync(ct);

        var sessionCounts = await db.SkillSessions
            .Where(s => s.IsComplete)
            .GroupBy(s => s.Skill)
            .Select(g => new { Skill = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // A first-attempt pass is a word that passed a skill having never
        // failed it. Computed per (word, skill) so a later retry does not
        // retroactively turn a clean pass into a messy one.
        var attemptRows = await db.WordEvents
            .Where(e => e.Skill != null
                        && (e.Type == WordEventType.SkillPassed
                            || e.Type == WordEventType.SkillFailed))
            .Select(e => new { e.WordId, Skill = e.Skill!.Value, e.Type })
            .ToListAsync(ct);

        var firstAttempt = attemptRows
            .GroupBy(r => new { r.WordId, r.Skill })
            .Where(g => g.Any(r => r.Type == WordEventType.SkillPassed)
                        && g.All(r => r.Type != WordEventType.SkillFailed))
            .GroupBy(g => g.Key.Skill)
            .ToDictionary(g => g.Key, g => g.Count());

        var skillStats = config.SkillsOrder.Select(skill => new SkillStatResponse(
            Skill: skill.ToWire(),
            SessionsCompleted: sessionCounts
                .FirstOrDefault(c => c.Skill == skill)?.Count ?? 0,
            WordsPassed: skillEvents.FirstOrDefault(e =>
                e.Skill == skill && e.Type == WordEventType.SkillPassed)?.Count ?? 0,
            WordsFailed: skillEvents.FirstOrDefault(e =>
                e.Skill == skill && e.Type == WordEventType.SkillFailed)?.Count ?? 0,
            FirstAttemptPasses: firstAttempt.GetValueOrDefault(skill))).ToList();

        // ── Session shape ───────────────────────────────────────────────────
        var completed = await db.SkillSessions
            .Where(s => s.IsComplete && s.CompletedAt != null)
            .Select(s => new
            {
                s.UserId,
                Duration = s.CompletedAt!.Value - s.StartedAt,
                s.UsedAiFallback,
            })
            .ToListAsync(ct);

        var averageDuration = completed.Count == 0
            ? 0
            : (int)completed.Average(s => s.Duration.TotalMilliseconds);

        // The AI fallback rate is the metric that stops a silent quality
        // collapse being read as learners getting worse (`MVP Core.txt` §62).
        var fallbackRate = completed.Count == 0
            ? 0
            : (double)completed.Count(s => s.UsedAiFallback) / completed.Count;

        // Words per learner per day, over the days they have actually been
        // registered — dividing by a fixed window would flatter a new cohort.
        var learnerAges = await learners
            .Select(u => new { u.Id, u.CreatedAt })
            .ToListAsync(ct);

        var wordsPerUserPerDay = 0d;
        if (learnerAges.Count > 0 && wordsTotal > 0)
        {
            var totalDays = learnerAges.Sum(u =>
                Math.Max(1, (now - u.CreatedAt).TotalDays));
            wordsPerUserPerDay = totalDays == 0 ? 0 : wordsTotal / totalDays;
        }

        return Results.Ok(new AdminOverviewResponse(
            UserCount: userCount,
            ActiveToday: activeToday,
            ActiveThisWeek: activeThisWeek,
            WordsAddedTotal: wordsTotal,
            AverageWordsPerUserPerDay: Math.Round(wordsPerUserPerDay, 3),
            AverageSessionsPerUser: userCount == 0
                ? 0
                : Math.Round((double)completed.Count / userCount, 3),
            AverageSessionDurationMs: averageDuration,
            PipelineCompletionRate:
                wordsTotal == 0 ? 0 : (double)wordsActive / wordsTotal,
            SkillStats: skillStats,
            LevelDistributions: distributions,
            TopInterests: interests,
            AiFallbackRate: Math.Round(fallbackRate, 4)));
    }

    private static async Task<IResult> UsersAsync(
        ClaimsPrincipal principal,
        WordOsDbContext db,
        CancellationToken ct)
    {
        if (RequireOwner(principal) is { } denied) return denied;

        var users = await db.Users
            .OrderByDescending(u => u.LastLoginAt ?? u.CreatedAt)
            .Take(200)
            .Select(u => new AdminUserSummaryResponse(
                u.Id,
                u.DisplayName,
                u.Email,
                u.Role.ToWire(),
                u.CreatedAt,
                u.LastLoginAt,
                db.Words.Count(w => w.UserId == u.Id),
                db.Words.Count(w => w.UserId == u.Id && w.State == WordState.Active)))
            .ToListAsync(ct);

        return Results.Ok(users);
    }

    private static async Task<IResult> UserDetailAsync(
        Guid id,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        WordOsConfiguration config,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (RequireOwner(principal) is { } denied) return denied;

        var user = await db.Users
            .Include(u => u.Interests)
            .Include(u => u.SkillLevels)
            .FirstOrDefaultAsync(u => u.Id == id, ct);

        if (user is null)
            return Problems.NotFound("NOT_FOUND", "User not found.");

        var words = await db.Words
            .Where(w => w.UserId == id)
            .Include(w => w.Skills)
            .Include(w => w.Events)
            .ToListAsync(ct);

        var now = clock.GetUtcNow();
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);

        var sessions = await db.SkillSessions
            .Where(s => s.UserId == id && s.IsComplete)
            .ToListAsync(ct);

        var levelChanges = await db.LevelChanges
            .Where(c => c.UserId == id)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        // ── Per-skill outcomes for this learner ─────────────────────────────
        var events = words.SelectMany(w => w.Events.Select(e => new
        {
            w.Id,
            e.Type,
            e.Skill,
            e.CreatedAt,
        })).ToList();

        var skillStats = config.SkillsOrder.Select(skill =>
        {
            var forSkill = events.Where(e => e.Skill == skill).ToList();

            var firstAttempt = forSkill
                .GroupBy(e => e.Id)
                .Count(g => g.Any(e => e.Type == WordEventType.SkillPassed)
                            && g.All(e => e.Type != WordEventType.SkillFailed));

            return new SkillStatResponse(
                Skill: skill.ToWire(),
                SessionsCompleted: sessions.Count(s => s.Skill == skill),
                WordsPassed: forSkill.Count(e => e.Type == WordEventType.SkillPassed),
                WordsFailed: forSkill.Count(e => e.Type == WordEventType.SkillFailed),
                FirstAttemptPasses: firstAttempt);
        }).ToList();

        // ── The last fortnight, day by day ──────────────────────────────────
        //
        // Every day is emitted, including the empty ones: gaps in a chart are
        // the interesting part, and a series that silently omits them reads as
        // uninterrupted study.
        var daily = Enumerable.Range(0, 14)
            .Select(offset =>
            {
                var day = today.AddDays(-offset);
                var next = day.AddDays(1);

                return new
                {
                    date = day,
                    wordsAdded = words.Count(w => w.AddedAt >= day && w.AddedAt < next),
                    perSkillCompleted = sessions
                        .Where(s => s.CompletedAt >= day && s.CompletedAt < next)
                        .GroupBy(s => s.Skill)
                        .ToDictionary(g => g.Key.ToWire(), g => g.Count()),
                    // A completed session is the only durable evidence of a
                    // visit: `LastLoginAt` records one moment, not a history.
                    signedIn = sessions.Any(s => s.CompletedAt >= day && s.CompletedAt < next)
                               || words.Any(w => w.AddedAt >= day && w.AddedAt < next),
                };
            })
            .OrderBy(d => d.date)
            .ToList();

        // ── Where this learner keeps stumbling ──────────────────────────────
        var mistakes = words
            .Select(w => new
            {
                Word = w,
                Failures = w.Events
                    .Where(e => e.Type == WordEventType.SkillFailed)
                    .ToList(),
            })
            .Where(x => x.Failures.Count > 0)
            .OrderByDescending(x => x.Failures.Count)
            .Take(20)
            .Select(x => new
            {
                wordId = x.Word.Id,
                text = x.Word.Text,
                meaning = x.Word.Meaning,
                skill = x.Failures.OrderByDescending(f => f.CreatedAt)
                    .First().Skill?.ToWire(),
                attempts = x.Failures.Count,
                lastFailedAt = x.Failures.Max(f => f.CreatedAt),
            })
            .ToList();

        var spellingLevel = user.SkillLevels
            .FirstOrDefault(l => l.Skill == SkillType.Spelling);

        return Results.Ok(new
        {
            summary = new AdminUserSummaryResponse(
                user.Id, user.DisplayName, user.Email,
                user.Role.ToWire(),
                user.CreatedAt, user.LastLoginAt,
                words.Count,
                words.Count(w => w.State == WordState.Active)),
            interests = user.Interests.Select(i => i.Interest).ToList(),
            levels = user.SkillLevels.Select(AuthEndpoints.ToSkillLevelResponse)
                .ToList(),
            // Spelling is measured but unlevelled (ADR-008): its diagnostic is
            // accuracy and input mode, never a CEFR band.
            spelling = new
            {
                itemsAnswered = spellingLevel?.EvaluationSessions ?? 0,
                correct = (int)Math.Round(
                    (spellingLevel?.RollingAccuracy ?? 0)
                    * (spellingLevel?.EvaluationSessions ?? 0)),
                supportMode =
                    (spellingLevel?.SpellingSupportMode ?? SpellingInputMode.LetterTiles)
                        .ToWire(),
            },
            wordsLearning = words.Count(w => w.State == WordState.Learning),
            wordsActive = words.Count(w => w.State == WordState.Active),
            wordsArchived = words.Count(w => w.State == WordState.Archived),
            wordsAddedToday = words.Count(w => w.AddedAt >= today),
            wordsAddedThisWeek = words.Count(w => w.AddedAt >= now.AddDays(-7)),
            wordsAddedThisMonth = words.Count(w => w.AddedAt >= now.AddDays(-30)),
            skillStats,
            daily,
            mistakes,
            masteredWords = words
                .Where(w => w.State is WordState.Active or WordState.Archived)
                .OrderByDescending(w => w.ActivatedAt)
                .Take(50)
                .Select(w => w.Text)
                .ToList(),
            signInCount = sessions.Count,
            levelChanges = levelChanges.Select(c => new
            {
                skill = c.Skill.ToWire(),
                previousLevel = c.PreviousLevel?.ToWire(),
                newLevel = c.NewLevel?.ToWire(),
                changeType = c.ChangeType.ToWire(),
                c.Accuracy,
                c.SessionsConsidered,
                c.CreatedAt,
            }).ToList(),
        });
    }
}

public static class Policies
{
    public const string OwnerOnly = "OwnerOnly";
}
