using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
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
    /// What this reminder is about — the stable key the client turns into a
    /// sentence (ADR-035). Never a sentence from here: the server does not know
    /// which language this installation reads.
    /// </param>
    /// <param name="Count">
    /// What <paramref name="Kind"/> counts — words due for <c>WORDS_DUE</c>,
    /// words in the pipeline for <c>NOTHING_DUE</c>, and zero for
    /// <c>NO_WORDS</c>.
    /// </param>
    public sealed record ReminderResponse(
        string Slot,
        DateOnly Date,
        int Hour,
        int Minute,
        string Kind,
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

        // Words the learner owns at all, deleted ones excluded by the query
        // filter (ADR-071). This separates "you have not started" from "you have
        // nothing due today", which are different messages to send someone.
        var owned = await db.Words.CountAsync(w => w.UserId == userId, ct);

        var reminders = new List<ReminderResponse>();
        var today = DateOnly.FromDateTime(now.ToOffset(offset).DateTime);

        for (var day = 0; day < config.ReminderHorizonDays; day++)
        {
            var date = today.AddDays(day);

            foreach (var (slot, hour) in new[]
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

                var (kind, count) = owned == 0
                    ? ("NO_WORDS", 0)
                    : due > 0
                        ? ("WORDS_DUE", due)
                        : ("NOTHING_DUE", learning.Count);

                reminders.Add(new ReminderResponse(
                    slot, date, hour, 0, kind, count));
            }
        }

        return Results.Ok(new RemindersResponse(reminders));
    }
}
