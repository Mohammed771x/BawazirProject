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
        group.MapPost("/practice/start", StartPracticeAsync);
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

        // Every word this learner still owes a review, whatever pipeline state
        // it reached — a word on Reading counts exactly as much as one that
        // matured, because the question is what they remember (rule R9). A word
        // already recalled correctly is done and is not here (ADR-099).
        var owned = await db.Words
            .Where(w => w.UserId == userId && w.ReviewPassedAt == null)
            .OrderBy(w => w.AddedAt)
            .ToListAsync(ct);

        // Ripe, oldest first, capped at one sitting (ADR-089). A word added
        // today is deliberately not here: testing it the day it arrived asks
        // what the learner still has in mind, not what they retained.
        var words = WeeklyReviewPolicy.NextSitting(owned, now, config);

        if (words.Count == 0)
        {
            // Two different situations, and telling them apart is the whole
            // value of the message: a learner in their first week has a *date*
            // to be given, and one who has cleared everything has nothing to do
            // and should be told so plainly.
            var opensAt = WeeklyReviewPolicy.OpensAt(owned, config);

            return opensAt is null
                ? Problems.Conflict(
                    "REVIEW_NOTHING_TO_REVIEW",
                    "You have recalled every word you added. Nothing to review.")
                // The *date* lives on the hub, which is the screen with the
                // challenge card on it and the one place a client needs it.
                // This message only has to be honest to a caller who reached
                // the endpoint another way.
                : Problems.Conflict(
                    "REVIEW_NOT_READY",
                    "Your words need a week to settle before the challenge "
                    + $"opens. The first are ready on {opensAt:yyyy-MM-dd}.");
        }

        var periodStart = words[0].AddedAt;

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

        // What is left behind the cap, so the client can say "there is another
        // group after this one" instead of letting it arrive as a surprise
        // tomorrow (ADR-089).
        var remainingAfter =
            WeeklyReviewPolicy.Ripe(owned, now, config).Count - words.Count;

        return Results.Ok(new
        {
            id = review.Id,
            periodStart = review.PeriodStart,
            totalWords = review.TotalWords,
            wordsWaitingAfterThis = remainingAfter,
            isPractice = false,
            queue = review.Queue.Select(ToItem).ToList(),
        });
    }

    /// <summary>
    /// Goes over the last finished review's words again, as practice (ADR-120).
    /// </summary>
    /// <remarks>
    /// The challenge closes once it is done and opens again when words have
    /// had another week — which left a learner who wanted to go over the
    /// week's words with a locked card. This is that going-over: the same
    /// words, the same questions, and none of it recorded (rule R9).
    /// </remarks>
    private static async Task<IResult> StartPracticeAsync(
        ClaimsPrincipal principal,
        WordOsDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        var (source, words) = await PracticeSourceAsync(db, userId.Value, ct);
        if (source is null || words.Count == 0)
        {
            return Problems.Conflict(
                "PRACTICE_NOTHING_TO_PRACTISE",
                "Finish a weekly review first; its words can then be practised.");
        }

        // Only an unfinished practice is replaced. An unfinished *review* is
        // left alone: starting a practice must never throw away the real one.
        var stale = await db.WeeklyReviews
            .Where(r => r.UserId == userId && !r.IsComplete && r.IsPractice)
            .ToListAsync(ct);
        db.WeeklyReviews.RemoveRange(stale);

        var now = clock.GetUtcNow();
        var practice = WeeklyReview.StartPractice(userId.Value, source, now);
        var random = Random.Shared;
        var meanings = words.Select(w => w.Meaning).ToList();

        foreach (var word in Shuffled(words, random))
        {
            practice.AddItem(WeeklyReviewItem.Create(
                word.Id, word.Text,
                BuildOptions(word.Meaning, meanings, random),
                word.Meaning));
        }

        db.WeeklyReviews.Add(practice);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            id = practice.Id,
            periodStart = practice.PeriodStart,
            totalWords = practice.TotalWords,
            wordsWaitingAfterThis = 0,
            isPractice = true,
            queue = practice.Queue.Select(ToItem).ToList(),
        });
    }

    /// <summary>
    /// The review a practice goes over — the learner's latest finished one —
    /// and those of its words the learner still has.
    /// </summary>
    /// <remarks>
    /// Shared with the hub, so the card offers practice exactly when this
    /// endpoint would start one. A word deleted since is not practised: the
    /// learner removed it, and asking about it would bring it back.
    /// </remarks>
    public static async Task<(WeeklyReview? Source, List<Word> Words)> PracticeSourceAsync(
        WordOsDbContext db, Guid userId, CancellationToken ct)
    {
        var source = await db.WeeklyReviews
            .Include(r => r.Items)
            .Where(r => r.UserId == userId && r.IsComplete && !r.IsPractice)
            .OrderByDescending(r => r.CompletedAt)
            .FirstOrDefaultAsync(ct);

        if (source is null) return (null, []);

        var ids = source.Items.Select(i => i.WordId).Distinct().ToList();
        var words = await db.Words
            .Where(w => ids.Contains(w.Id) && w.UserId == userId
                        && w.State != WordState.Deleted)
            .OrderBy(w => w.AddedAt)
            .ToListAsync(ct);

        return (source, words);
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
        // A practice round is recorded as nothing (ADR-120): it neither
        // retires a word from the challenge nor ripens it again, and it is
        // not a meeting with the word the challenge would count.
        var word = review.IsPractice
            ? null
            : await db.Words.FirstOrDefaultAsync(w => w.Id == item.WordId, ct);
        if (word is not null)
        {
            // Correct first time retires the word from the challenge; anything
            // else leaves it in the pool, ripening again for next week
            // (ADR-099). First attempt, because that is the standard the
            // weekly score itself is computed to — a word rescued on the
            // second try was not remembered (R9).
            word.MarkReviewed(now, passed: item.FirstAttemptCorrect == true);

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
            // A practice is not this week's review: logging it as one would
            // tell the reminders the challenge was done (ADR-120).
            if (!review.IsPractice)
            {
                db.ActivityEvents.Add(ActivityEvent.Record(
                    userId.Value, ActivityType.ReviewCompleted, now,
                    entityId: review.Id));
            }
        }

        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            reviewId = review.Id,
            totalWords = review.TotalWords,
            firstPassCorrect = review.FirstPassCorrect,
            weeklyScore = Math.Round(review.WeeklyScore, 4),
            totalAttempts = review.TotalAttempts,
            isPractice = review.IsPractice,
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
