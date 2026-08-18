using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WordOs.Application.Abstractions;
using WordOs.Domain.Common;
using WordOs.Domain.Levels;
using WordOs.Domain.Placement;
using WordOs.Domain.Users;
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
            scoredByFallback: item.IsFreeText,
            // The learner's own words — the most useful record this test
            // produces, and impossible to recover later from a level (§26).
            rawAnswer: request.Answer);

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
        IAiContentService ai,
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

        // Speaking and Writing are re-scored by the AI before the bands are
        // computed. During the test they carry an offline score, because the
        // adaptive engine needs a number immediately to choose the next item;
        // that number is length-and-variety and cannot tell a short fluent
        // answer from a padded weak one. Reading and Listening are untouched —
        // their answers are matched against a known key.
        var aiScored = await RateProductiveAnswersAsync(session, ai, ct);

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
        db.ActivityEvents.Add(ActivityEvent.Record(
            user.Id, ActivityType.PlacementCompleted, now,
            entityId: session.Id));

        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            // The four the learner sees. Spelling is measured and stored — its
            // row still exists and still drives the hint strategy — but it is
            // not a fifth primary skill and must not appear beside the others
            // (§13, §20, §21). Grammar likewise: it shaped Speaking and Writing
            // above rather than becoming a level of its own.
            levels = outcome.Levels
                .Where(l => l.Skill != SkillType.Spelling)
                .Select(l => new
                {
                    skill = l.Skill.ToWire(),
                    systemAssessedLevel = l.Level?.ToWire(),
                    userSelectedLevel = l.Level?.ToWire(),
                    confidence = l.Confidence,
                    rollingAccuracy = l.Accuracy,
                }).ToList(),
            // Kept in the payload for the internal diagnostic it feeds — the
            // spelling hint strategy — not as a level to display.
            spelling = new
            {
                itemsAnswered = outcome.SpellingItemsAnswered,
                correct = outcome.SpellingCorrect,
                supportMode = outcome.SpellingSupportMode
                    .ToWire(),
            },
            testVersion = session.TestVersion,
            // Speaking and Writing were judged by the AI; the receptive skills
            // were matched against a key. Saying so is honest, and explains why
            // a band may move once real sessions start.
            productiveScoredByAi = aiScored,
            summary = outcome.HasLowConfidence
                ? "A couple of these are still rough — the test was short, and "
                  + "WordOS will settle them from your first two weeks of real "
                  + "sessions."
                : "Estimated per skill from a short test. WordOS keeps "
                  + "measuring your real performance and adjusts as it learns "
                  + "more about you.",
        });
    }

    /// <summary>
    /// Replaces the offline scores for Speaking and Writing with the AI's.
    /// </summary>
    /// <remarks>
    /// One call per skill, at the end — not per answer during the test, which
    /// would put a model round-trip between every question and the next.
    ///
    /// The model rates; it does not place. Its per-answer scores go back into
    /// the same Rasch estimator the receptive skills use, so a band is still
    /// computed here, from evidence, under this server's confidence rules
    /// (rule R2).
    /// </remarks>
    private static async Task<bool> RateProductiveAnswersAsync(
        PlacementSession session,
        IAiContentService ai,
        CancellationToken ct)
    {
        var rated = false;

        foreach (var skill in new[] { SkillType.Speaking, SkillType.Writing })
        {
            var answers = session.Answers
                .Where(a => a.Skill == skill
                            && !string.IsNullOrWhiteSpace(a.RawAnswer))
                .ToList();

            if (answers.Count == 0) continue;

            var request = new PlacementEvaluationRequest(
                skill,
                answers.Select(a =>
                {
                    var item = PlacementItemBank.Find(a.ItemId);
                    return new PlacementAnswerToRate(
                        a.ItemId, a.Level, item?.Prompt ?? string.Empty,
                        a.RawAnswer!);
                }).ToList());

            var evaluation = await ai.EvaluatePlacementAsync(request, ct);
            if (evaluation.FromFallback) continue;

            foreach (var rating in evaluation.Answers)
            {
                var answer = answers.FirstOrDefault(a => a.ItemId == rating.ItemId);
                // The evidence is stored beside the score, so a surprising band
                // can be read back rather than argued about (Part 3).
                answer?.ApplyAiRating(
                    rating.Score, rating.EstimatedLevel, rating.Evidence);
            }

            rated = true;
        }

        return rated;
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
            // Three types, not two. A spoken item answered in a text box is a
            // writing test filed under Speaking (§17) — and that is exactly
            // what happened while this only ever said FREE_TEXT: the client
            // has the microphone, it simply was never told to show it.
            item.IsSpoken
                ? "SPOKEN"
                : item.IsFreeText
                    ? "FREE_TEXT"
                    : "MULTIPLE_CHOICE",
            item.Prompt,
            options,
            item.Passage,
            item.AudioText);
    }
}
