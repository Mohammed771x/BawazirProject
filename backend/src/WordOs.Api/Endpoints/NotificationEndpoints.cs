using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Review;
using WordOs.Domain.Reminders;
using WordOs.Domain.Users;
using WordOs.Domain.Words;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Endpoints;

/// <summary>
/// The daily reminders this learner's phone should raise (ADR-076).
/// </summary>
/// <remarks>
/// A local notification is fired by the device, usually with no network and
/// often with the app not running — so whatever it is going to say has to be
/// decided before it is scheduled. That is the whole reason this endpoint
/// exists: rule R1 says the client renders server-provided state, and "you have
/// four words ready on Thursday morning" is server state that happens to be
/// about the future.
///
/// <para>So the server works out, for each of the next
/// <see cref="WordOsConfiguration.ReminderHorizonDays"/> days and each of the
/// two times of day, what will be true at that moment, and hands the phone a
/// list. The phone schedules them and says them in the learner's language
/// (ADR-035); it does not count anything and does not decide what to say.</para>
///
/// <para>Nothing is pushed. There is no Firebase, no device token, no server
/// process that wakes up at eight — a notification here is an alarm the phone
/// sets for itself, which is why a learner who never opens the app eventually
/// stops hearing from us. That is a real limit and an accepted one: the
/// alternative is a push infrastructure this product does not need.</para>
/// </remarks>
public static class NotificationEndpoints
{
    /// <param name="Slot">
    /// <c>MORNING</c> or <c>EVENING</c>. The phone uses it to choose the
    /// greeting; the two are otherwise the same kind of thing.
    /// </param>
    /// <param name="Date">
    /// The local calendar date it belongs to, as <c>yyyy-MM-dd</c>.
    /// </param>
    /// <param name="Hour">
    /// The local wall-clock hour to fire at. Wall-clock and not an instant: the
    /// device schedules it in its own timezone, and a learner means the time on
    /// their own phone when they say "morning".
    /// </param>
    /// <param name="Kind">
    /// What this reminder is about, coarsely — the stable key the client turns
    /// into a sentence (ADR-035). Never a sentence from here: the server does
    /// not know which language this installation reads.
    /// </param>
    /// <param name="Message">
    /// <b>Which</b> sentence, out of twenty (ADR-090). Also a key, and also
    /// never a sentence. A client that does not recognise it falls back on
    /// <paramref name="Kind"/>, which is why both are sent: the server ships
    /// more often than the phones do.
    /// </param>
    /// <param name="Count">
    /// The number in the line, or zero when it has none. What it counts depends
    /// on <paramref name="Message"/> — words due, days of a streak, days until
    /// the next word ripens — and the client is told by the key which of those
    /// it is holding, so no message has to guess.
    /// </param>
    public sealed record ReminderResponse(
        string Slot,
        DateOnly Date,
        int Hour,
        int Minute,
        string Kind,
        string Message,
        int Count);

    public sealed record RemindersResponse(IReadOnlyList<ReminderResponse> Reminders);

    public static IEndpointRouteBuilder MapNotificationEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/notifications/daily", GetAsync)
            .RequireAuthorization()
            .WithTags("Notifications");

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
        var offset = TimeSpan.FromHours(config.ReportingUtcOffsetHours);

        // Everything still in the pipeline, once. Each reminder asks the same
        // set a different question — "which of you are due at 8am on the 17th" —
        // and a query per reminder would be fourteen round trips for a screen
        // the learner never sees.
        var learning = await db.Words
            .Where(w => w.UserId == userId && w.State == WordState.Learning)
            .Include(w => w.Skills)
            .ToListAsync(ct);

        // Every word still owed a review, for the challenge's ripeness (ADR-089)
        // — and the count of words owned at all, which separates "you have not
        // started" from "you have nothing due today". Those are different
        // messages to send somebody, and "0 words ready" is the wrong one for
        // both. Deleted words are excluded by the query filter (ADR-071).
        //
        // Still owed means never recalled correctly, not never asked: a word
        // the learner missed comes back a week later (ADR-099).
        var unreviewed = await db.Words
            .Where(w => w.UserId == userId && w.ReviewPassedAt == null)
            .Select(w => new { w.AddedAt, w.State, w.LastReviewedAt })
            .ToListAsync(ct);

        var owned = await db.Words.CountAsync(w => w.UserId == userId, ct);
        var active = await db.Words.CountAsync(
            w => w.UserId == userId && w.State == WordState.Active, ct);

        var facts = await FactsAsync(
            db, userId.Value, now, offset, config, learning, owned, active, ct);

        var today = DateOnly.FromDateTime(now.ToOffset(offset).DateTime);
        var slots = new List<(ReminderResponse Head, ReminderSlot Slot)>();

        for (var day = 0; day < config.ReminderHorizonDays; day++)
        {
            var date = today.AddDays(day);

            foreach (var (name, hour) in new[]
                     {
                         ("MORNING", config.MorningReminderHour),
                         ("EVENING", config.EveningReminderHour),
                     })
            {
                var at = new DateTimeOffset(
                    date.Year, date.Month, date.Day, hour, 0, 0, offset);

                // Today's morning slot, asked for in the afternoon. Sending it
                // anyway would have the phone either fire it immediately or
                // silently drop it, and neither is a reminder.
                if (at <= now) continue;

                var due = learning.Count(w =>
                    config.SkillsOrder.Any(skill => w.IsEligibleFor(skill, at)));

                // How many words will have ripened for the challenge by then —
                // projected, not guessed: ripeness is a function of when the
                // word was last put in front of the learner and nothing else
                // (ADR-089, ADR-099).
                var ripe = unreviewed.Count(w =>
                    w.State != WordState.Deleted &&
                    WeeklyReviewPolicy.RipensAt(
                        w.LastReviewedAt ?? w.AddedAt, config) <= at);

                slots.Add((
                    new ReminderResponse(name, date, hour, 0, "", "", 0),
                    new ReminderSlot(
                        At: at,
                        Morning: name == "MORNING",
                        IsToday: date == today,
                        WordsDue: due,
                        RipeForReview: ripe)));
            }
        }

        var composed = ReminderComposer.Compose(
            slots.Select(s => s.Slot).ToList(),
            facts,
            userId.Value,
            today.DayNumber);

        var reminders = slots
            .Select((s, i) => s.Head with
            {
                Kind = composed[i].Kind.ToWire(),
                Message = composed[i].Message.ToWire(),
                Count = composed[i].Count,
            })
            .ToList();

        return Results.Ok(new RemindersResponse(reminders));
    }

    /// <summary>
    /// What is true about this learner right now — the facts a message may rest
    /// on, gathered once for the whole week of reminders.
    /// </summary>
    /// <remarks>
    /// Everything here describes <b>now</b>, and the composer is what decides
    /// which of it may be said at a slot three days out (ADR-090). Keeping the
    /// gathering and the deciding apart is deliberate: the rule about staleness
    /// is a rule about honesty, and it belongs somewhere it can be read and
    /// tested rather than buried in a query.
    /// </remarks>
    private static async Task<ReminderFacts> FactsAsync(
        WordOsDbContext db,
        Guid userId,
        DateTimeOffset now,
        TimeSpan offset,
        WordOsConfiguration config,
        IReadOnlyList<Word> learning,
        int owned,
        int active,
        CancellationToken ct)
    {
        var todayStart = config.StartOfDay(now);

        // The days they finished something on, newest first. Thirty is plenty:
        // a streak longer than that is still reported as thirty-plus days of
        // habit, and loading a year of history to say so would be silly.
        var finishedOn = await db.ActivityEvents
            .Where(e => e.UserId == userId
                        && e.CreatedAt >= now.AddDays(-30)
                        && (e.Type == ActivityType.SessionCompleted
                            || e.Type == ActivityType.ReviewCompleted))
            .Select(e => e.CreatedAt)
            .ToListAsync(ct);

        var days = finishedOn
            .Select(at => DateOnly.FromDateTime(at.ToOffset(offset).DateTime))
            .Distinct()
            .OrderDescending()
            .ToList();

        var today = DateOnly.FromDateTime(now.ToOffset(offset).DateTime);
        var practisedToday = days.Contains(today);
        var practisedYesterday = days.Contains(today.AddDays(-1));

        // A run ending today or yesterday. Ending *yesterday* still counts:
        // at eight in the morning a learner who practised every day for a week
        // has a streak, and telling them it is over before the day has started
        // is both wrong and discouraging.
        var streak = 0;
        var cursor = practisedToday ? today : today.AddDays(-1);
        while (days.Contains(cursor))
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }

        int? daysSince = days.Count == 0
            ? null
            : today.DayNumber - days[0].DayNumber;

        // A level the *system* moved up in the last week. Deliberately three
        // narrowings: only a system-validated change, because rule R6 says a
        // level the learner chose for themselves is a preference and not an
        // achievement; only upwards, because a reminder is not the place to
        // tell somebody their level was lowered; and only recently, because
        // "your level went up" about a fortnight ago is not news.
        var levelRose = await db.LevelChanges.AnyAsync(
            c => c.UserId == userId
                 && c.ChangeType == LevelChangeType.SystemValidatedChange
                 && c.CreatedAt >= now.AddDays(-7)
                 && c.NewLevel != null
                 && (c.PreviousLevel == null || c.NewLevel > c.PreviousLevel), ct);

        // Words one skill short of finishing. The most motivating fact the app
        // has about anybody, and it is otherwise invisible until they open it.
        var lastSkill = config.SkillsOrder[^1];
        var almostActive = learning.Count(w => w.CurrentSkill == lastSkill);

        // When the next word comes due, so an empty day can say how long it
        // stays empty rather than only that it is empty.
        var nextDue = learning
            .SelectMany(w => w.Skills)
            .Where(s => s.Status != SkillStatus.Passed && s.AvailableAt > now)
            .Select(s => s.AvailableAt!.Value)
            .DefaultIfEmpty()
            .Min();

        int? daysUntilNextDue = nextDue == default
            ? null
            : Math.Max(
                0,
                DateOnly.FromDateTime(nextDue.ToOffset(offset).DateTime).DayNumber
                - today.DayNumber);

        _ = todayStart;

        return new ReminderFacts(
            OwnedWords: owned,
            LearningWords: learning.Count,
            ActiveWords: active,
            StreakDays: streak,
            PractisedToday: practisedToday,
            PractisedYesterday: practisedYesterday,
            DaysSinceLastSession: daysSince,
            LevelRoseRecently: levelRose,
            AlmostActive: almostActive,
            DaysUntilNextDue: daysUntilNextDue,
            ReviewMaxPerSitting: config.WeeklyReviewMaxWords);
    }
}
