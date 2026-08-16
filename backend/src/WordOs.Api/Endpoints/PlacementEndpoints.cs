using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Levels;
using WordOs.Domain.Placement;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Endpoints;

/// <summary>
/// The adaptive placement test.
/// </summary>
/// <remarks>
/// Three calls rather than one, because an adaptive test cannot hand the client
/// its items up front: which question comes next depends on how the previous
/// one was answered (ADR-009, <c>docs/06-PLACEMENT-ALGORITHM.md</c>).
///
/// Everything the level depends on is computed here — scoring, item choice,
/// ability estimation, the final band. The client posts an answer string and
/// renders what it is told (rule R1).
/// </remarks>
public static class PlacementEndpoints
{
    public sealed record AnswerRequest(
        [property: Required, MaxLength(64)] string ItemId,
        [property: Required, MaxLength(4000)] string Answer);

    public sealed record PlacementItemResponse(
        string Id,
        string Skill,
        string Type,
        string Prompt,
        IReadOnlyList<string> Options,
        string? Passage,
        string? AudioText);

    public static IEndpointRouteBuilder MapPlacementEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/placement")
            .WithTags("Placement")
            .RequireAuthorization()
            // Each free-text answer will cost an AI evaluation in Phase 6.
            .RequireRateLimiting(RateLimitPolicies.Expensive);

        group.MapPost("/start", StartAsync);
        group.MapPost("/{id:guid}/answer", AnswerAsync);
        group.MapPost("/{id:guid}/complete", CompleteAsync);

        return app;
    }

    private static async Task<IResult> StartAsync(
        ClaimsPrincipal principal,
        WordOsDbContext db,
        PlacementEngine engine,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        var now = clock.GetUtcNow();

        // Abandon any run left open, so a learner who closed the app mid-test
        // starts cleanly rather than resuming a half-scored estimate.
        var stale = await db.PlacementSessions
            .Where(s => s.UserId == userId && !s.IsComplete)
            .ToListAsync(ct);
        db.PlacementSessions.RemoveRange(stale);

        var session = PlacementSession.Start(userId.Value, now);
        var item = engine.NextItem([], Random.Shared);
        session.SetCurrentItem(item?.Id);

        db.PlacementSessions.Add(session);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToStep(session, item, engine, 0));
    }

    private static async Task<IResult> AnswerAsync(
        Guid id,
        AnswerRequest request,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        PlacementEngine engine,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(request, out var errors))
            return Results.ValidationProblem(errors);

        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        // Scoped by the caller's own id: another learner's run is not
        // addressable even with its id (docs/07-SECURITY.md §4).
        var session = await db.PlacementSessions
            .Include(s => s.Answers)
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);

        if (session is null)
        {
            return Problems.NotFound(
                "PLACEMENT_NOT_FOUND",
                "This placement test has expired. Please start again.");
        }

        if (session.IsComplete)
        {
            return Problems.Conflict(
                "PLACEMENT_COMPLETE", "This placement test is already finished.");
        }

        // A retry after a dropped connection, or a client out of step. Rejecting
        // keeps the ability estimate honest — replaying answers must not be a
        // way to manufacture a level.
        if (session.CurrentItemId != request.ItemId)
        {
            return Problems.Conflict(
                "ITEM_NOT_CURRENT", "That question is no longer the active one.");
        }

        var item = PlacementItemBank.Find(request.ItemId);
        if (item is null)
            return Problems.NotFound("ITEM_NOT_FOUND", "Question not found.");

        // Difficulty and score are both derived server-side from the item the
        // server issued — never from anything the client sent.
        var difficulty = engine.Config.Scale.DifficultyOf(item.Level);
        var score = engine.ScoreAnswer(item, request.Answer);
        var now = clock.GetUtcNow();

        session.RecordAnswer(
            item, difficulty, score, now,
            // The AI evaluator arrives in Phase 6; until then every free-text
            // answer is scored by the offline fallback, and that is recorded.
            scoredByFallback: item.IsFreeText);

        var next = engine.NextItem(session.ToResponses(), Random.Shared);
        session.SetCurrentItem(next?.Id);

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToStep(session, next, engine, session.Answers.Count));
    }

    private static async Task<IResult> CompleteAsync(
        Guid id,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        PlacementEngine engine,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        var session = await db.PlacementSessions
            .Include(s => s.Answers)
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);

        if (session is null)
        {
            return Problems.NotFound(
                "PLACEMENT_NOT_FOUND",
                "This placement test has expired. Please start again.");
        }

        // Completing early would place a learner on a fraction of the evidence.
        if (!session.IsComplete && session.CurrentItemId is not null)
        {
            return Problems.Conflict(
                "PLACEMENT_INCOMPLETE",
                "The placement test is not finished yet.");
        }

        var outcome = engine.Complete(session.ToResponses());
        var now = clock.GetUtcNow();

        var user = await db.Users
            .Include(u => u.SkillLevels)
            .FirstAsync(u => u.Id == userId, ct);

        foreach (var result in outcome.Levels)
        {
            var level = user.LevelFor(result.Skill);
            level.ApplyPlacement(result.Level, result.Confidence, result.Accuracy);

            if (result.Skill == SkillType.Spelling)
                level.SetSpellingSupportMode(outcome.SpellingSupportMode);

            var change = LevelChangeRecord.Create(
                user.Id, result.Skill, null, result.Level,
                LevelChangeType.Placement, now,
                reason: "placement", accuracy: result.Accuracy);
            db.LevelChanges.Add(change);
        }

        user.AdvanceOnboarding(OnboardingStage.Complete);
        session.Complete(now);

        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            levels = outcome.Levels.Select(l => new
            {
                skill = l.Skill.ToWire(),
                // Null for Spelling — measured, never levelled (ADR-008).
                systemAssessedLevel = l.Level?.ToWire(),
                userSelectedLevel = l.Level?.ToWire(),
                confidence = l.Confidence,
                rollingAccuracy = l.Accuracy,
            }).ToList(),
            spelling = new
            {
                itemsAnswered = outcome.SpellingItemsAnswered,
                correct = outcome.SpellingCorrect,
                supportMode = outcome.SpellingSupportMode
                    .ToWire(),
            },
            summary = outcome.HasLowConfidence
                ? "These are your starting levels. A couple of them are still "
                  + "provisional — WordOS keeps measuring your real sessions and "
                  + "will settle them within your first two weeks."
                : "Your starting levels are set per skill. They are a starting "
                  + "point — WordOS keeps measuring your real performance and "
                  + "adjusts them over time.",
        });
    }

    private static object ToStep(
        PlacementSession session,
        BankItem? item,
        PlacementEngine engine,
        int answered) => new
        {
            sessionId = session.Id,
            isComplete = item is null,
            item = item is null ? null : Project(item),
            progress = new
            {
                answered,
                // An estimate on purpose: an adaptive test stops as soon as it
                // is confident, so the real total is not known in advance.
                estimatedTotal = Math.Max(answered, engine.Config.EstimatedTotalItems),
                currentSkill = (item?.Skill ?? SkillType.Spelling)
                    .ToWire(),
                skillCount = engine.Config.SkillOrder.Count,
            },
        };

    /// <summary>
    /// Projects an item for the client: options shuffled (rule R7), and neither
    /// the correct answer nor the difficulty band included — showing a learner
    /// they are on a "C1 item" changes how they answer.
    /// </summary>
    private static PlacementItemResponse Project(BankItem item)
    {
        var options = item.Options.ToArray();
        Random.Shared.Shuffle(options);

        return new PlacementItemResponse(
            item.Id,
            item.Skill.ToWire(),
            item.IsFreeText ? "FREE_TEXT" : "MULTIPLE_CHOICE",
            item.Prompt,
            options,
            item.Passage,
            item.AudioText);
    }
}
