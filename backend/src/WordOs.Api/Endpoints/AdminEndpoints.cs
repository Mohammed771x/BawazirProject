using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Users;
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
    /// <param name="LastActiveAt">
    /// The last thing this account actually did, from the activity log — not
    /// its last sign-in. A learner who signs in on Monday and studies all week
    /// has one login and seven days of work, and the column that only knew the
    /// login reported them as untouched since Monday (§34–§35).
    /// </param>
    /// <param name="SessionsCompleted">
    /// Sessions this account has finished. The client has always shown this
    /// figure; until now the server never sent it, so every row read zero.
    /// </param>
    /// <param name="Phone">
    /// The learner's number in international form, or null if they gave none
    /// (ADR-053). Owner-only, like everything else on this endpoint: it is here
    /// so the Owner can actually reach the person whose report they are reading.
    /// </param>
    public sealed record AdminUserSummaryResponse(
        Guid Id,
        string DisplayName,
        string Email,
        string Role,
        DateTimeOffset CreatedAt,
        DateTimeOffset? LastActiveAt,
        int WordsTotal,
        int WordsActive,
        int SessionsCompleted,
        string? Phone);

    public sealed record AdminOverviewResponse(
        int UserCount,
        int ActiveToday,
        int ActiveThisWeek,
        int WordsAddedTotal,
        double AverageWordsPerUserPerDay,
        double AverageSessionsPerUser,
        int AverageSessionDurationMs,
        /// <summary>
        /// The duration of the middle session.
        /// </summary>
        /// <remarks>
        /// Reported beside the mean because the mean describes nobody here: a
        /// session is resumable by design (ADR-039), so a learner who opens one
        /// and finishes it tomorrow contributes a duration measured in hours.
        /// Measured on real data: median 16 seconds, mean 45 minutes, longest
        /// 46 hours — eleven sessions out of 288 moved the headline figure by a
        /// factor of 170.
        /// </remarks>
        int MedianSessionDurationMs,
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
    /// <param name="WordsDecided">
    /// Distinct words that have passed or failed this skill at least once —
    /// the denominator <paramref name="FirstAttemptPasses"/> belongs over.
    ///
    /// <para>Not the same as <paramref name="WordsPassed"/> +
    /// <paramref name="WordsFailed"/>, which count *events*: a word that failed
    /// twice before passing is three events and one word. Dividing a count of
    /// words by a count of events is not a ratio of anything, and it understated
    /// Speaking's first-attempt accuracy as 67% where the answer is 86%.</para>
    /// </param>
    public sealed record SkillStatResponse(
        string Skill,
        int SessionsCompleted,
        int WordsPassed,
        int WordsFailed,
        int FirstAttemptPasses,
        int WordsDecided);

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
        group.MapGet("/users/{id:guid}/words", UserWordsAsync);
        group.MapGet("/words/{wordId:guid}", WordJourneyAsync);
        group.MapGet("/users/{id:guid}/placement", PlacementEvidenceAsync);
        group.MapPost("/users/{id:guid}/advance-schedule", AdvanceScheduleAsync);

        return app;
    }

    public sealed record AdvanceScheduleRequest(int Days = 2);

    /// <summary>
    /// Brings a learner's waiting skills forward, for testing the gaps.
    /// </summary>
    /// <remarks>
    /// The pipeline puts two days between one skill and the next, which is the
    /// behaviour the product exists to measure — and it makes the pipeline
    /// impossible to demonstrate or to test end to end, because seeing five
    /// skills through takes over a week of waiting. The mock backend had a
    /// "skip two days" control for exactly this reason; this is the same thing
    /// against the real one (ADR-037).
    ///
    /// What it does <b>not</b> do matters as much as what it does:
    ///
    /// * It moves <i>scheduled</i> dates only. A pass, a failure, an attempt, a
    ///   session — anything that already happened — is untouched, so the
    ///   learner's history stays true and only the future arrives sooner.
    /// * It is written to the activity log, because a pipeline finished in an
    ///   afternoon would otherwise read as an extraordinary learner rather than
    ///   as a moved clock.
    /// * It is Owner-only, twice over: the route group carries the policy and
    ///   the handler checks again.
    /// </remarks>
    private static async Task<IResult> AdvanceScheduleAsync(
        Guid id,
        AdvanceScheduleRequest request,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (RequireOwner(principal) is { } denied) return denied;

        // Bounded on both sides: a negative number would push the schedule away
        // and a huge one would land the dates outside what a date can hold.
        var days = Math.Clamp(request.Days, 1, 30);

        var learner = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (learner is null)
            return Problems.NotFound("USER_NOT_FOUND", "No such learner.");

        var words = await db.Words
            .Include(w => w.Skills)
            .Where(w => w.UserId == id)
            .ToListAsync(ct);

        foreach (var word in words) word.AdvanceSchedule(days);

        var now = clock.GetUtcNow();
        db.ActivityEvents.Add(ActivityEvent.Record(
            id, ActivityType.ScheduleAdvanced, now));

        await db.SaveChangesAsync(ct);

        // What is due *now*, so the dashboard can say what the skip unlocked
        // rather than just claiming success.
        var dueNow = words
            .SelectMany(w => w.Skills)
            .Count(s => s.EffectiveStatus(now) == SkillStatus.Available);

        return Results.Ok(new
        {
            days,
            wordsShifted = words.Count,
            skillsDueNow = dueNow,
        });
    }

    /// <summary>
    /// The second, defence-in-depth check. Redundant with the policy by design.
    /// </summary>
    private static IResult? RequireOwner(ClaimsPrincipal principal) =>
        principal.IsOwner()
            ? null
            : Problems.Forbidden(
                "FORBIDDEN", "This area is restricted to the system owner.");

    /// <param name="days">
    /// The window to report on — 1 for today, 5, 10, or any custom number of
    /// days. Omitted means all time. Part 3 asks for the choice because the
    /// two questions are different: "is this working?" is answered over months,
    /// "did today go wrong?" is answered over hours.
    /// </param>
    private static async Task<IResult> OverviewAsync(
        int? days,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        WordOsConfiguration config,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (RequireOwner(principal) is { } denied) return denied;

        var now = clock.GetUtcNow();

        // The learner's day, not the server's machine-local one and not UTC.
        // A fixed product offset (ADR-052): UTC made every morning before 3am
        // belong to yesterday for an audience three hours ahead of it, and the
        // machine's own zone would make the figure depend on where the server
        // happens to be running.
        var today = config.StartOfDay(now);
        var weekAgo = now.AddDays(-7);

        // Clamped, not trusted: the number is typed in by hand, and a big
        // enough one used to overflow `AddDays` and take the dashboard with it.
        var from = ReportingWindow.From(days, now);

        var learners = db.Users.Where(u => u.Role == UserRole.User);

        var userCount = await learners.CountAsync(ct);

        // Every figure below is *per learner*, so every figure below counts
        // only learners (ADR-052). Owner accounts exist to test the product:
        // they had been landing in the numerators while the denominator counted
        // learners alone, and on this database that was 47 of 288 sessions and
        // 114 of 392 skill decisions — a developer's afternoon of testing
        // reported as how the audience is doing.
        var learnerIds = db.Users
            .Where(u => u.Role == UserRole.User)
            .Select(u => u.Id);

        // Counted from the activity log rather than from `LastLoginAt`
        // (Part 3 §34–§35). A single "last login" column can only ever answer
        // "were they here most recently in this window?" — a learner who signed
        // in on Monday and again today vanishes from Monday's figure entirely.
        var activeToday = await db.ActivityEvents
            .Where(e => e.CreatedAt >= today && learnerIds.Contains(e.UserId))
            .Select(e => e.UserId)
            .Distinct()
            .CountAsync(ct);

        var activeThisWeek = await db.ActivityEvents
            .Where(e => e.CreatedAt >= weekAgo && learnerIds.Contains(e.UserId))
            .Select(e => e.UserId)
            .Distinct()
            .CountAsync(ct);

        // Words added inside the window — the figure the window is about.
        var wordsTotal = await db.Words
            .CountAsync(w => w.AddedAt >= from && learnerIds.Contains(w.UserId), ct);

        // Pipeline completion is deliberately *not* windowed. A word needs five
        // skills and four two-day gaps to reach Active, so "of the words added
        // today, how many are Active?" is structurally zero and would read as a
        // collapse rather than as arithmetic.
        var wordsEver = await db.Words
            .CountAsync(w => learnerIds.Contains(w.UserId), ct);
        var wordsActive = await db.Words
            .CountAsync(w => w.State == WordState.Active
                             && learnerIds.Contains(w.UserId), ct);

        var levelRows = await db.SkillLevels
            .Where(l => l.SystemAssessedLevel != null
                        && learnerIds.Contains(l.UserId))
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
            .Where(i => learnerIds.Contains(i.UserId))
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
        var learnerWordIds = db.Words
            .Where(w => learnerIds.Contains(w.UserId))
            .Select(w => w.Id);

        var skillEvents = await db.WordEvents
            .Where(e => e.CreatedAt >= from
                        && e.Skill != null
                        && learnerWordIds.Contains(e.WordId)
                        && (e.Type == WordEventType.SkillPassed
                            || e.Type == WordEventType.SkillFailed))
            .GroupBy(e => new { Skill = e.Skill!.Value, e.Type })
            .Select(g => new { g.Key.Skill, g.Key.Type, Count = g.Count() })
            .ToListAsync(ct);

        var sessionCounts = await db.SkillSessions
            .Where(s => s.IsComplete && s.CompletedAt >= from
                        && learnerIds.Contains(s.UserId))
            .GroupBy(s => s.Skill)
            .Select(g => new { Skill = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // A first-attempt pass is a word that passed a skill having never
        // failed it. Computed per (word, skill) so a later retry does not
        // retroactively turn a clean pass into a messy one.
        var attemptRows = await db.WordEvents
            .Where(e => e.CreatedAt >= from
                        && e.Skill != null
                        && learnerWordIds.Contains(e.WordId)
                        && (e.Type == WordEventType.SkillPassed
                            || e.Type == WordEventType.SkillFailed))
            .Select(e => new { e.WordId, Skill = e.Skill!.Value, e.Type })
            .ToListAsync(ct);

        var perWord = attemptRows
            .GroupBy(r => new { r.WordId, r.Skill })
            .ToList();

        var firstAttempt = perWord
            .Where(g => g.Any(r => r.Type == WordEventType.SkillPassed)
                        && g.All(r => r.Type != WordEventType.SkillFailed))
            .GroupBy(g => g.Key.Skill)
            .ToDictionary(g => g.Key, g => g.Count());

        // The denominator first-attempt accuracy actually belongs over: words
        // this skill has decided, not attempts it has recorded (ADR-052).
        var decided = perWord
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
            FirstAttemptPasses: firstAttempt.GetValueOrDefault(skill),
            WordsDecided: decided.GetValueOrDefault(skill))).ToList();

        // ── Session shape ───────────────────────────────────────────────────
        var completed = await db.SkillSessions
            .Where(s => s.IsComplete && s.CompletedAt != null && s.CompletedAt >= from
                        && learnerIds.Contains(s.UserId))
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

        // The middle session, which is the one a person would recognise. The
        // mean is kept because a growing gap between the two is itself the
        // signal that sessions are being abandoned and resumed (ADR-052).
        var sortedDurations = completed
            .Select(s => s.Duration.TotalMilliseconds)
            .OrderBy(ms => ms)
            .ToList();

        var medianDuration = sortedDurations.Count == 0
            ? 0
            : (int)sortedDurations[sortedDurations.Count / 2];

        // The AI fallback rate is the metric that stops a silent quality
        // collapse being read as learners getting worse (`MVP Core.txt` §62).
        var fallbackRate = completed.Count == 0
            ? 0
            : (double)completed.Count(s => s.UsedAiFallback) / completed.Count;

        // Words per learner per day. Over all time the denominator is how long
        // each learner has actually been registered — dividing by a fixed
        // window would flatter a new cohort. Inside a window it is simply the
        // window, because that is the question being asked.
        var learnerAges = await learners
            .Select(u => new { u.Id, u.CreatedAt })
            .ToListAsync(ct);

        var wordsPerUserPerDay = 0d;
        if (learnerAges.Count > 0 && wordsTotal > 0)
        {
            var totalDays = days is > 0
                ? learnerAges.Count * (double)days.Value
                : learnerAges.Sum(u => Math.Max(1, (now - u.CreatedAt).TotalDays));

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
            MedianSessionDurationMs: medianDuration,
            PipelineCompletionRate:
                wordsEver == 0 ? 0 : (double)wordsActive / wordsEver,
            SkillStats: skillStats,
            LevelDistributions: distributions,
            TopInterests: interests,
            AiFallbackRate: Math.Round(fallbackRate, 4)));
    }

    /// <summary>
    /// The learner list: searchable, paged, and filterable by activity window.
    /// </summary>
    /// <remarks>
    /// Part 3 asks for all three. The window (<c>days</c>) is answered from the
    /// activity log, so "active in the last 5 days" means the learner actually
    /// did something — not that their single <c>LastLoginAt</c> column happens
    /// to fall inside it (§34–§35).
    /// </remarks>
    private static async Task<IResult> UsersAsync(
        string? q,
        int? days,
        int? page,
        int? pageSize,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (RequireOwner(principal) is { } denied) return denied;

        var paging = Page.From(page, pageSize, maxSize: 200);

        var query = db.Users.AsQueryable();

        var term = SearchTerm.Clean(q);
        if (term.Length > 64)
            return Problems.BadRequest("QUERY_TOO_LONG", "Search term is too long.");

        if (term.Length > 0)
        {
            query = query.Where(u =>
                EF.Functions.ILike(u.DisplayName, $"%{term}%") ||
                EF.Functions.ILike(u.Email, $"%{term}%"));
        }

        if (days is > 0)
        {
            var from = ReportingWindow.From(days, clock.GetUtcNow());

            query = query.Where(u =>
                db.ActivityEvents.Any(e => e.UserId == u.Id && e.CreatedAt >= from));
        }

        var total = await query.CountAsync(ct);

        var users = await query
            // Ordered by the last thing they did, not the last time they signed
            // in. Fifty-four accounts on this database had done work and never
            // logged in again since registering, so they sorted to the bottom
            // as though they had gone quiet (ADR-052).
            .OrderByDescending(u =>
                db.ActivityEvents
                    .Where(e => e.UserId == u.Id)
                    .Max(e => (DateTimeOffset?)e.CreatedAt)
                ?? u.CreatedAt)
            .Skip(paging.Offset)
            .Take(paging.Size)
            .Select(u => new AdminUserSummaryResponse(
                u.Id,
                u.DisplayName,
                u.Email,
                u.Role.ToWire(),
                u.CreatedAt,
                db.ActivityEvents
                    .Where(e => e.UserId == u.Id)
                    .Max(e => (DateTimeOffset?)e.CreatedAt)
                ?? u.LastLoginAt,
                db.Words.Count(w => w.UserId == u.Id),
                db.Words.Count(w => w.UserId == u.Id && w.State == WordState.Active),
                // The client has always drawn this number; the server had never
                // sent it, so every row read "0 sessions" however much the
                // learner had done.
                db.SkillSessions.Count(x => x.UserId == u.Id && x.IsComplete),
                u.PhoneNumber == null
                    ? null
                    : "+" + u.PhoneCountryCode + u.PhoneNumber))
            .ToListAsync(ct);

        return Results.Ok(new
        {
            items = users,
            total,
            page = paging.Index,
            pageSize = paging.Size,
            hasMore = paging.HasMore(total),
        });
    }

    /// <summary>
    /// One learner's vocabulary, filtered by pipeline state and paged.
    /// </summary>
    /// <remarks>
    /// The Owner's view is the opposite of the learner's: where My Words hides
    /// the pipeline states because they are internal machinery (Part 2 §42),
    /// this exists precisely to inspect them — which words are stuck in
    /// Learning, which reached Active, which were archived when the level grew.
    /// </remarks>
    private static async Task<IResult> UserWordsAsync(
        Guid id,
        string? state,
        string? q,
        int? page,
        int? pageSize,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        CancellationToken ct)
    {
        if (RequireOwner(principal) is { } denied) return denied;

        var paging = Page.From(page, pageSize, maxSize: 200);

        var query = db.Words.Include(w => w.Skills).Where(w => w.UserId == id);

        if (!string.IsNullOrWhiteSpace(state))
        {
            if (!Enum.TryParse<WordState>(state, ignoreCase: true, out var parsed))
                return Problems.BadRequest("INVALID_STATE", "Unknown word state.");
            query = query.Where(w => w.State == parsed);
        }

        var term = SearchTerm.Clean(q);
        if (term.Length > 64)
            return Problems.BadRequest("QUERY_TOO_LONG", "Search term is too long.");
        if (term.Length > 0)
        {
            query = query.Where(w =>
                EF.Functions.ILike(w.Text, $"%{term}%") || w.Meaning.Contains(term));
        }

        var total = await query.CountAsync(ct);

        var words = await query
            .OrderByDescending(w => w.AddedAt)
            .Skip(paging.Offset)
            .Take(paging.Size)
            .ToListAsync(ct);

        return Results.Ok(new
        {
            items = words.Select(w => new
            {
                w.Id,
                w.Text,
                w.Meaning,
                cefrLevel = w.CefrLevel.ToWire(),
                state = w.State.ToWire(),
                currentSkill = w.CurrentSkill?.ToWire(),
                w.AddedAt,
                w.ExposureCount,
                skillsPassed = w.Skills.Count(sk => sk.Status == SkillStatus.Passed),
                attempts = w.Skills.Sum(sk => sk.Attempts),
            }).ToList(),
            total,
            page = paging.Index,
            pageSize = paging.Size,
            hasMore = paging.HasMore(total),
        });
    }

    /// <summary>
    /// One word's whole life, for any learner.
    /// </summary>
    /// <remarks>
    /// The word journey Part 3 asks for: added on this date with this intended
    /// meaning, attempted these skills on these dates, passed or failed each
    /// one, matured, archived. Read from the append-only word event log, so a
    /// word that failed Reading twice before passing shows all three events
    /// rather than only its current state.
    ///
    /// Owner-only and addressed by word id alone — the learner it belongs to
    /// comes back in the response rather than being trusted from the request.
    /// </remarks>
    private static async Task<IResult> WordJourneyAsync(
        Guid wordId,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        CancellationToken ct)
    {
        if (RequireOwner(principal) is { } denied) return denied;

        var word = await db.Words
            .Include(w => w.Skills)
            .Include(w => w.Events)
            .FirstOrDefaultAsync(w => w.Id == wordId, ct);

        if (word is null)
            return Problems.NotFound("WORD_NOT_FOUND", "Word not found.");

        var owner = await db.Users
            .Where(u => u.Id == word.UserId)
            .Select(u => new { u.Id, u.DisplayName, u.Email })
            .FirstAsync(ct);

        var exposures = await db.WordExposures
            .Where(e => e.WordId == wordId)
            .OrderBy(e => e.OccurredAt)
            .Select(e => new { source = e.Source.ToString(), e.OccurredAt })
            .ToListAsync(ct);

        return Results.Ok(new
        {
            word = new
            {
                word.Id,
                word.Text,
                word.Meaning,
                word.DefinitionEn,
                cefrLevel = word.CefrLevel.ToWire(),
                state = word.State.ToWire(),
                currentSkill = word.CurrentSkill?.ToWire(),
                word.AddedAt,
                word.ActivatedAt,
                word.ExposureCount,
            },
            learner = owner,
            skills = word.Skills
                .OrderBy(sk => sk.Skill)
                .Select(sk => new
                {
                    skill = sk.Skill.ToWire(),
                    status = sk.Status.ToWire(),
                    sk.Attempts,
                    sk.AvailableAt,
                    sk.PassedAt,
                })
                .ToList(),
            // The trail itself, oldest first — this is the journey.
            events = word.Events
                .OrderBy(e => e.CreatedAt)
                .Select(e => new
                {
                    type = e.Type.ToWire(),
                    skill = e.Skill?.ToWire(),
                    e.CreatedAt,
                })
                .ToList(),
            // Exposure is a priority signal, never a limit (rule R8) — shown
            // here so a word that keeps reappearing can be seen doing it.
            exposures,
        });
    }

    /// <summary>
    /// What the placement test actually saw.
    /// </summary>
    /// <remarks>
    /// A CEFR band is a conclusion, and Part 3 asks for the evidence behind it:
    /// which items were asked, at what level, in which domain, and what the
    /// learner said. Free-text and spoken answers are stored verbatim
    /// (ADR-022's sibling decision in the placement rework) precisely so this
    /// view can exist — a level alone can never be audited, only believed.
    ///
    /// <c>testVersion</c> matters as much as the answers: a result produced by
    /// an older item bank is not comparable to a current one, and without the
    /// version stamped on it there is no way to know which you are looking at.
    /// </remarks>
    private static async Task<IResult> PlacementEvidenceAsync(
        Guid id,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        CancellationToken ct)
    {
        if (RequireOwner(principal) is { } denied) return denied;

        var session = await db.PlacementSessions
            .Include(p => p.Answers)
            .Where(p => p.UserId == id)
            .OrderByDescending(p => p.StartedAt)
            .FirstOrDefaultAsync(ct);

        if (session is null)
        {
            return Results.Ok(new
            {
                completed = false,
                answers = Array.Empty<object>(),
            });
        }

        // The bands the placement itself assigned, kept apart from where the
        // learner is now: "started at A2, now B1" is the single most useful
        // sentence this dashboard can produce, and it needs both halves.
        var placementChanges = await db.LevelChanges
            .Where(c => c.UserId == id
                        && c.ChangeType == LevelChangeType.Placement)
            .ToListAsync(ct);

        var current = await db.SkillLevels
            .Where(l => l.UserId == id)
            .ToListAsync(ct);

        return Results.Ok(new
        {
            sessionId = session.Id,
            completed = session.IsComplete,
            testVersion = session.TestVersion,
            startedAt = session.StartedAt,
            completedAt = session.CompletedAt,
            // How many free-text answers were scored offline rather than by
            // the AI evaluator — the caveat that belongs beside the result.
            fallbackScoredCount = session.FallbackScoredCount,
            progress = new
            {
                levels = current
                    .Where(l => l.Skill != SkillType.Spelling)
                    .OrderBy(l => l.Skill)
                    .Select(l => new
                    {
                        skill = l.Skill.ToWire(),
                        initialLevel = placementChanges
                            .FirstOrDefault(c => c.Skill == l.Skill)
                            ?.NewLevel?.ToWire(),
                        currentLevel = (l.SystemAssessedLevel
                                        ?? l.UserSelectedLevel)?.ToWire(),
                        systemAssessedLevel = l.SystemAssessedLevel?.ToWire(),
                        userSelectedLevel = l.UserSelectedLevel?.ToWire(),
                        l.Confidence,
                        l.RollingAccuracy,
                    })
                    .ToList(),
            },
            answers = session.Answers
                .OrderBy(a => a.AnsweredAt)
                .Select(a => new
                {
                    a.ItemId,
                    skill = a.Skill.ToWire(),
                    domain = a.Domain.ToString(),
                    level = a.Level.ToWire(),
                    alsoEvidenceFor = a.AlsoEvidenceFor?.ToWire(),
                    a.Difficulty,
                    a.Score,
                    // Verbatim. A score of 0.4 says nothing about *why*.
                    a.RawAnswer,
                    // The AI evaluator's verdict, where one was produced.
                    a.EvaluationJson,
                    a.AnsweredAt,
                })
                .ToList(),
        });
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
        var today = config.StartOfDay(now);

        var sessions = await db.SkillSessions
            .Where(s => s.UserId == id && s.IsComplete)
            .ToListAsync(ct);

        var levelChanges = await db.LevelChanges
            .Where(c => c.UserId == id)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

        var activity = await db.ActivityEvents
            .Where(e => e.UserId == id)
            .OrderBy(e => e.CreatedAt)
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
            var forSkill = events
                .Where(e => e.Skill == skill
                            && e.Type is WordEventType.SkillPassed
                                      or WordEventType.SkillFailed)
                .ToList();

            var byWord = forSkill.GroupBy(e => e.Id).ToList();

            var firstAttempt = byWord
                .Count(g => g.Any(e => e.Type == WordEventType.SkillPassed)
                            && g.All(e => e.Type != WordEventType.SkillFailed));

            return new SkillStatResponse(
                Skill: skill.ToWire(),
                SessionsCompleted: sessions.Count(s => s.Skill == skill),
                WordsPassed: forSkill.Count(e => e.Type == WordEventType.SkillPassed),
                WordsFailed: forSkill.Count(e => e.Type == WordEventType.SkillFailed),
                FirstAttemptPasses: firstAttempt,
                WordsDecided: byWord.Count);
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
                    // Straight from the activity log now (§34–§35), rather
                    // than inferred from whatever else happened to be durable —
                    // a learner who opened the app, read a passage and left
                    // used to leave no trace at all.
                    signedIn = activity.Any(e => e.CreatedAt >= day && e.CreatedAt < next),
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
                user.CreatedAt,
                // The last thing they did, as in the list they came from.
                activity.Count > 0
                    ? activity.Max(e => e.CreatedAt)
                    : user.LastLoginAt,
                words.Count,
                words.Count(w => w.State == WordState.Active),
                sessions.Count,
                user.PhoneNumber is null
                    ? null
                    : $"+{user.PhoneCountryCode}{user.PhoneNumber}"),
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
            // Actual sign-ins, from the log. This used to be the session count,
            // which is a different number wearing the wrong label (§34–§35).
            signInCount = activity.Count(e =>
                e.Type is ActivityType.SignedIn or ActivityType.Registered),
            // The last fifty things this learner did, newest first — the raw
            // trail behind every figure above.
            activity = activity
                .OrderByDescending(e => e.CreatedAt)
                .Take(50)
                .Select(e => new
                {
                    type = e.Type.ToString(),
                    skill = e.Skill?.ToWire(),
                    entityId = e.EntityId,
                    e.CreatedAt,
                })
                .ToList(),
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
