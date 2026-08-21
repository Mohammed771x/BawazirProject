using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Endpoints;

/// <summary>
/// The Skills Hub.
/// </summary>
/// <remarks>
/// Rule R1: the client renders <c>availability</c> verbatim and never computes
/// it. Whether a skill is startable depends on the spaced gap, the word's
/// current skill and the daily target — all server state. A client that worked
/// it out locally would drift the moment any of those changed.
/// </remarks>
public static class HubEndpoints
{
    /// <param name="ActiveSessionId">
    /// An unfinished session for this skill, if there is one.
    /// </param>
    /// <remarks>
    /// This is how a session survives the app being killed. The client stores
    /// nothing (rule R1, rule R4): it asks the hub, and the hub — which reads
    /// the same database the session lives in — says "you were in the middle of
    /// this one". Works on a reinstalled app and on a second device, which a
    /// locally-cached session id would not.
    /// </remarks>
    public sealed record SkillCardResponse(
        string Skill,
        string Availability,
        int DueWordCount,
        int SessionWordCount,
        string? Level,
        DateTimeOffset? NextDueAt,
        Guid? ActiveSessionId);

    public static IEndpointRouteBuilder MapHubEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/hub", GetAsync)
            .RequireAuthorization()
            .WithTags("Hub");

        return app;
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal principal,
        WordOsDbContext db,
        WordOsConfiguration config,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        var now = clock.GetUtcNow();
        var todayStart = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);

        var levels = await db.SkillLevels
            .Where(l => l.UserId == userId)
            .ToDictionaryAsync(l => l.Skill, ct);

        // One query for everything still in the pipeline, then the per-skill
        // counts are computed in memory. Five separate round trips per hub load
        // would be worse for a screen the learner opens constantly.
        var learning = await db.Words
            .Where(w => w.UserId == userId && w.State == WordState.Learning)
            .Include(w => w.Skills)
            .ToListAsync(ct);

        // A session that never finished being built is not one to resume.
        // StartAsync claims its row before generating the content (ADR-063), so
        // an AI refusal or a restart in between can leave a row with no
        // passage, no conversation and no items. Offering it as
        // `activeSessionId` would send the learner to an empty screen; leaving
        // it out means the next start clears it and builds a real one.
        var openSessions = await db.SkillSessions
            .Where(s => s.UserId == userId && !s.IsComplete)
            .Where(s => s.ContentText != null
                        || s.TranscriptJson != null
                        || s.Items.Any())
            .Select(s => new { s.Skill, s.Id })
            .ToListAsync(ct);

        var cards = new List<SkillCardResponse>();

        foreach (var skill in config.SkillsOrder)
        {
            var due = learning.Count(w => w.IsEligibleFor(skill, now));

            // The soonest a currently-waiting word becomes startable — this is
            // what the client shows as "next on …".
            var nextDueAt = learning
                .Where(w => w.CurrentSkill == skill && !w.IsEligibleFor(skill, now))
                .Select(w => w.SkillState(skill).AvailableAt)
                .Where(at => at is not null && at > now)
                .OrderBy(at => at)
                .FirstOrDefault();

            var target = levels.TryGetValue(skill, out var level)
                ? level.DailyTargetWords
                : config.DefaultDailyTarget;

            var active = openSessions.FirstOrDefault(s => s.Skill == skill)?.Id;

            cards.Add(new SkillCardResponse(
                Skill: skill.ToWire(),
                // An unfinished session is startable even when nothing new is
                // due — its words are already spoken for, and abandoning it is
                // the learner's decision to make, not a state to be stuck in.
                Availability: due > 0 || active is not null ? "AVAILABLE" : "EMPTY",
                DueWordCount: due,
                // Capped by the learner's daily target for this skill.
                SessionWordCount: Math.Min(due, target),
                // Null for Spelling, which carries no CEFR band (ADR-008).
                Level: levels.TryGetValue(skill, out var l)
                    ? l.UserSelectedLevel?.ToWire()
                    : null,
                NextDueAt: nextDueAt,
                ActiveSessionId: active));
        }

        var counts = await db.Words
            .Where(w => w.UserId == userId)
            .GroupBy(w => w.State)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int CountOf(WordState state) =>
            counts.FirstOrDefault(c => c.State == state)?.Count ?? 0;

        var addedToday = await db.Words
            .CountAsync(w => w.UserId == userId && w.AddedAt >= todayStart, ct);

        var periodStart = now.AddDays(-config.WeeklyReviewPeriodDays);
        var reviewWordCount = await db.Words
            .CountAsync(w => w.UserId == userId && w.AddedAt >= periodStart, ct);

        return Results.Ok(new
        {
            dailyProgress = new
            {
                wordsAddedToday = addedToday,
                dailyTarget = levels.TryGetValue(config.FirstSkill, out var first)
                    ? first.DailyTargetWords
                    : config.DefaultDailyTarget,
            },
            skills = cards,
            weeklyReview = new
            {
                available = reviewWordCount > 0,
                wordCount = reviewWordCount,
                periodStart,
                nextAvailableAt = (DateTimeOffset?)null,
            },
            vocabulary = new
            {
                learning = CountOf(WordState.Learning),
                active = CountOf(WordState.Active),
                archived = CountOf(WordState.Archived),
            },
        });
    }
}
