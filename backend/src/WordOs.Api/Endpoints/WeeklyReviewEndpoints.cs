using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Review;
using WordOs.Domain.Words;
using WordOs.Domain.Users;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Endpoints;

/// <summary>
/// Weekly review — measurement only (rule R9).
/// </summary>
/// <remarks>
/// Nothing in this file calls <see cref="Word.ApplySessionResult"/>,
/// <c>Archive</c>, <c>RecordSession</c> or the level engine. That is not an
/// omission: the review exists to tell the learner (and the experiment) how much
/// of the week actually stuck, and a measurement that changes the thing it
/// measures is worthless.
///
/// The one word-level write it does make is <see cref="Word.MarkReviewed"/>,
/// which records that the word was seen. It affects no schedule and no status.
/// </remarks>
public static class WeeklyReviewEndpoints
{
    public sealed record ReviewAnswerRequest(
        [property: Required] Guid ItemId,
        [property: Required, MaxLength(512)] string Answer);

    public static IEndpointRouteBuilder MapWeeklyReviewEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/weekly-review")
            .WithTags("WeeklyReview")
            // No named policy: these are cheap database writes, covered by the
            // global per-user limiter.
            .RequireAuthorization();

        group.MapPost("/start", StartAsync);
        group.MapPost("/{id:guid}/answer", AnswerAsync);
        group.MapPost("/{id:guid}/complete", CompleteAsync);

        return app;
    }

    private static async Task<IResult> StartAsync(
        ClaimsPrincipal principal,
        WordOsDbContext db,
        WordOsConfiguration config,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        var now = clock.GetUtcNow();
        var periodStart = now.AddDays(-config.WeeklyReviewPeriodDays);

        // Everything added in the period, whatever state it reached. A word
        // that is still on Reading counts exactly as much as one that matured:
        // the question is what the learner remembers, not how far it travelled.
        var words = await db.Words
            .Where(w => w.UserId == userId && w.AddedAt >= periodStart)
            .OrderBy(w => w.AddedAt)
            .ToListAsync(ct);

        if (words.Count == 0)
        {
            return Problems.Conflict(
                "NO_WORDS_IN_PERIOD", "No words were added in this period.");
        }

        // An unfinished review is replaced rather than resumed — a half-answered
        // review carries a stale queue and would distort the score.
        var stale = await db.WeeklyReviews
            .Where(r => r.UserId == userId && !r.IsComplete)
            .ToListAsync(ct);
        db.WeeklyReviews.RemoveRange(stale);

        var review = WeeklyReview.Start(userId.Value, periodStart, now);
        var random = Random.Shared;
        var meanings = words.Select(w => w.Meaning).ToList();

        foreach (var word in Shuffled(words, random))
        {
            review.AddItem(WeeklyReviewItem.Create(
                word.Id, word.Text,
                BuildOptions(word.Meaning, meanings, random),
                word.Meaning));
        }

        db.WeeklyReviews.Add(review);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            id = review.Id,
            periodStart = review.PeriodStart,
            totalWords = review.TotalWords,
            queue = review.Queue.Select(ToItem).ToList(),
        });
    }

    private static async Task<IResult> AnswerAsync(
        Guid id,
        ReviewAnswerRequest request,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        WordOsConfiguration config,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(request, out var errors))
            return Results.ValidationProblem(errors);

        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        var review = await db.WeeklyReviews
            .Include(r => r.Items)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);

        if (review is null)
            return Problems.NotFound("REVIEW_NOT_FOUND", "Review not found.");

        if (review.IsComplete)
            return Problems.Conflict("REVIEW_COMPLETE", "This review is finished.");

        var item = review.Items.FirstOrDefault(i => i.Id == request.ItemId);
        if (item is null)
            return Problems.NotFound("ITEM_NOT_FOUND", "Question not found.");

        if (review.CurrentItemId != item.Id)
        {
            return Problems.Conflict(
                "ITEM_NOT_CURRENT", "That question is no longer the active one.");
        }

        var now = clock.GetUtcNow();

        // Compared against what the server issued — never against a correct
        // answer supplied by the client.
        var isCorrect = string.Equals(
            request.Answer, item.CorrectAnswer, StringComparison.Ordinal);

        item.MarkAnswered(now);
        var requeued = review.RecordAttempt(item, isCorrect, config.MaxAttemptsPerItem);

        // Being reviewed is exposure. Rule R8: a priority signal, never a limit
        // and never a delete trigger.
        //
        // Recorded as an event keyed by (word, source, review), so the requeue
        // that follows a wrong answer cannot count the same word twice — the
        // learner met it once, in one review.
        var word = await db.Words.FirstOrDefaultAsync(w => w.Id == item.WordId, ct);
        if (word is not null)
        {
            word.MarkReviewed(now);

            var alreadyCounted = await db.WordExposures.AnyAsync(
                e => e.WordId == word.Id
                     && e.Source == ExposureSource.WeeklyReview
                     && e.SourceId == review.Id, ct);

            if (!alreadyCounted)
            {
                db.WordExposures.Add(WordExposure.Record(
                    word.Id, ExposureSource.WeeklyReview, review.Id, now));
                word.RecordExposure(now);
            }
        }

        await db.SaveChangesAsync(ct);

        var next = review.Queue.FirstOrDefault();

        return Results.Ok(new
        {
            itemId = item.Id,
            isCorrect,
            correctAnswer = item.CorrectAnswer,
            requeued,
            remaining = review.Queue.Count,
            nextItem = next is null ? null : ToItem(next),
        });
    }

    private static async Task<IResult> CompleteAsync(
        Guid id,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        var review = await db.WeeklyReviews
            .Include(r => r.Items)
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);

        if (review is null)
            return Problems.NotFound("REVIEW_NOT_FOUND", "Review not found.");

        if (!review.IsComplete)
        {
            var now = clock.GetUtcNow();
            review.Complete(now);
            db.ActivityEvents.Add(ActivityEvent.Record(
                userId.Value, ActivityType.ReviewCompleted, now,
                entityId: review.Id));
        }

        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            reviewId = review.Id,
            totalWords = review.TotalWords,
            firstPassCorrect = review.FirstPassCorrect,
            weeklyScore = Math.Round(review.WeeklyScore, 4),
            totalAttempts = review.TotalAttempts,
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static object ToItem(WeeklyReviewItem item) => new
    {
        id = item.Id,
        wordId = item.WordId,
        prompt = item.Prompt,
        options = JsonSerializer.Deserialize<List<string>>(item.OptionsJson),
    };

    private static List<string> BuildOptions(
        string correct,
        IReadOnlyList<string> pool,
        Random random)
    {
        var others = pool
            .Where(m => !string.Equals(m, correct, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var options = new List<string> { correct };
        options.AddRange(Shuffled(others, random).Take(3));

        foreach (var filler in Fillers)
        {
            if (options.Count >= 4) break;
            if (!options.Contains(filler, StringComparer.Ordinal))
                options.Add(filler);
        }

        return Shuffled(options, random).ToList();
    }

    private static readonly string[] Fillers =
    [
        "لوحة مفاتيح", "شبكة الإنترنت", "قاعدة بيانات", "متصفح",
        "مكتبة عامة", "مطار دولي", "وجبة خفيفة", "ملعب رياضي",
    ];

    private static List<T> Shuffled<T>(IEnumerable<T> source, Random random)
    {
        var list = source.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }
}
