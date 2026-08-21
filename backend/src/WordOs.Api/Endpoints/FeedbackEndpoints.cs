using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Users;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Endpoints;

/// <summary>
/// A learner writing to the Owner, and the Owner reading it (ADR-053).
/// </summary>
/// <remarks>
/// Until now a learner who hit a problem had nowhere to report it — no address
/// in the app, no support screen. The realistic outcome of that is that they
/// stop using it and nobody ever learns why, which for an experiment whose
/// whole point is measurement is the worst answer available.
///
/// Two halves, deliberately asymmetric:
///
/// <list type="bullet">
/// <item>any signed-in learner may <b>write</b>, and can never read anything —
/// not their own messages back, and certainly not anybody else's;</item>
/// <item>only the Owner may <b>read</b>, and reading is all they can do to it
/// besides marking it dealt with. Nothing here edits what a learner wrote.</item>
/// </list>
/// </remarks>
public static class FeedbackEndpoints
{
    public sealed record SendFeedbackRequest(
        [property: Required]
        [property: StringLength(4000, MinimumLength = 1)]
        string Body,
        [property: StringLength(32)] string? AppVersion = null,
        [property: StringLength(32)] string? Platform = null);

    public sealed record HandleFeedbackRequest(bool Handled);

    public static IEndpointRouteBuilder MapFeedbackEndpoints(
        this IEndpointRouteBuilder app)
    {
        // The learner's half. Rate-limited with the expensive endpoints, not
        // because it costs anything to serve, but because an unbounded write of
        // free text is how a table fills up overnight.
        app.MapPost("/api/feedback", SendAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.Expensive)
            .WithTags("Feedback");

        var owner = app.MapGroup("/api/admin/feedback")
            .WithTags("Admin")
            .RequireAuthorization(Policies.OwnerOnly);

        owner.MapGet("", ListAsync);
        owner.MapPatch("/{id:guid}", HandleAsync);

        return app;
    }

    private static async Task<IResult> SendAsync(
        SendFeedbackRequest request,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(request, out var errors))
            return Results.ValidationProblem(errors);

        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        // Control characters stripped for the same reason every other text
        // field strips them: PostgreSQL refuses a NUL byte outright, and a
        // learner who pastes from a broken keyboard should not get a 500.
        var body = SearchTerm.Clean(request.Body);
        if (body.Length == 0)
            return Problems.BadRequest("EMPTY_FEEDBACK", "Write something first.");

        var now = clock.GetUtcNow();

        var message = FeedbackMessage.Create(
            userId.Value, body, now,
            appVersion: SearchTerm.Clean(request.AppVersion ?? string.Empty),
            platform: SearchTerm.Clean(request.Platform ?? string.Empty));

        db.FeedbackMessages.Add(message);

        // Logged like everything else a learner does, so "did anyone report
        // anything the day it broke?" is answerable from the same trail as
        // every other question (ADR-025). The text is not copied here — the
        // activity log deliberately holds no free text.
        db.ActivityEvents.Add(ActivityEvent.Record(
            userId.Value, ActivityType.FeedbackSent, now));

        await db.SaveChangesAsync(ct);

        return Results.Ok(new { id = message.Id, sentAt = message.CreatedAt });
    }

    /// <param name="status">
    /// <c>NEW</c>, <c>HANDLED</c>, or omitted for everything. New first is the
    /// default order regardless, because the point of the screen is what has
    /// not been dealt with yet.
    /// </param>
    private static async Task<IResult> ListAsync(
        string? status,
        int? page,
        int? pageSize,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        CancellationToken ct)
    {
        if (!principal.IsOwner())
            return Problems.Forbidden(
                "FORBIDDEN", "This area is restricted to the system owner.");

        var pageIndex = Math.Max(0, page ?? 0);
        var size = pageSize is null or <= 0 ? 50 : Math.Min(pageSize.Value, 200);

        var query = db.FeedbackMessages.AsQueryable();

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<FeedbackStatus>(status, ignoreCase: true, out var parsed))
                return Problems.BadRequest("INVALID_STATUS", "Unknown status filter.");

            query = query.Where(m => m.Status == parsed);
        }

        var total = await query.CountAsync(ct);
        var unread = await db.FeedbackMessages
            .CountAsync(m => m.Status == FeedbackStatus.New, ct);

        // Joined to the learner, because a message with no name attached is a
        // complaint from nobody — and the Owner's next move is usually to
        // contact them.
        var items = await query
            .OrderBy(m => m.Status)
            .ThenByDescending(m => m.CreatedAt)
            .Skip(pageIndex * size)
            .Take(size)
            .Select(m => new
            {
                id = m.Id,
                body = m.Body,
                status = m.Status.ToString().ToUpperInvariant(),
                createdAt = m.CreatedAt,
                handledAt = m.HandledAt,
                appVersion = m.AppVersion,
                platform = m.Platform,
                user = db.Users
                    .Where(u => u.Id == m.UserId)
                    .Select(u => new
                    {
                        id = u.Id,
                        displayName = u.DisplayName,
                        email = u.Email,
                        phoneCountryCode = u.PhoneCountryCode,
                        phoneNumber = u.PhoneNumber,
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return Results.Ok(new
        {
            items,
            total,
            unread,
            page = pageIndex,
            pageSize = size,
            hasMore = (pageIndex + 1) * size < total,
        });
    }

    private static async Task<IResult> HandleAsync(
        Guid id,
        HandleFeedbackRequest request,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!principal.IsOwner())
            return Problems.Forbidden(
                "FORBIDDEN", "This area is restricted to the system owner.");

        var message = await db.FeedbackMessages.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (message is null)
            return Problems.NotFound("NOT_FOUND", "That message does not exist.");

        message.SetHandled(request.Handled, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            id = message.Id,
            status = message.Status.ToString().ToUpperInvariant(),
            handledAt = message.HandledAt,
        });
    }
}
