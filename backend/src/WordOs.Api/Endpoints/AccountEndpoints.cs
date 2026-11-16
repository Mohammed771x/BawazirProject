using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WordOs.Application.Abstractions;
using WordOs.Domain.Common;
using WordOs.Domain.Users;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Endpoints;

/// <summary>
/// The account itself: proving its email address (ADR-141) and deleting it
/// for good (ADR-140).
/// </summary>
public static class AccountEndpoints
{
    public sealed record VerifyEmailRequest(
        [property: Required, RegularExpression(@"^[0-9]{6}$")] string Code);

    public sealed record DeleteAccountRequest(
        [property: Required, MaxLength(128)] string Password);

    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var email = app.MapGroup("/api/auth/email").WithTags("Auth").RequireAuthorization();
        email.MapPost("/verify", VerifyAsync);
        email.MapPost("/resend", ResendAsync);

        app.MapPost("/api/me/delete", DeleteAsync).WithTags("Auth").RequireAuthorization();
        return app;
    }

    // ── Email verification (ADR-141) ─────────────────────────────────────────

    /// <summary>
    /// Issues a code to a freshly registered learner and emails it. Called by
    /// registration after the account is saved; sending is best-effort — a
    /// failed send leaves a code the learner can replace with "send again".
    /// </summary>
    internal static async Task SendCodeAsync(
        User user,
        WordOsDbContext db,
        IPasswordHasher hasher,
        IEmailSender emailSender,
        WordOsConfiguration config,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        user.IssueEmailCode(
            await hasher.HashAsync(code, ct),
            now,
            now.AddMinutes(config.EmailVerificationCodeExpiryMinutes));
        await db.SaveChangesAsync(ct);

        await emailSender.SendAsync(
            Compose(user.Email, user.DisplayName, code, config.EmailVerificationCodeExpiryMinutes),
            ct);
    }

    private static async Task<IResult> VerifyAsync(
        VerifyEmailRequest request,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        IPasswordHasher hasher,
        WordOsConfiguration config,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(request, out var errors))
            return Results.ValidationProblem(errors);

        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        // One check of a code at a time: two guesses racing could otherwise
        // both read four attempts used and both be allowed the fifth.
        await using var serialised = await RowLock.AcquireAsync(db, "email-code", userId.Value, ct);

        var user = await db.Users
            .Include(u => u.Interests)
            .Include(u => u.SkillLevels)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Results.Unauthorized();

        // Already proved — a repeat of a request whose reply was lost. Answer
        // with the account as it is, which is what the first reply said.
        if (!user.AwaitsEmailVerification)
            return Results.Ok(AuthEndpoints.ToUserResponse(user));

        var now = clock.GetUtcNow();
        if (!user.EmailCodeRedeemable(now, config.EmailVerificationMaxAttempts))
        {
            return Problems.BadRequest(
                "EMAIL_CODE_EXPIRED",
                "That code has expired or was tried too many times. Send a new one.");
        }

        if (!await hasher.VerifyAsync(request.Code, user.EmailCodeHash!, ct))
        {
            user.RecordEmailCodeFailure();
            await db.SaveChangesAsync(ct);
            return Problems.BadRequest("INVALID_EMAIL_CODE", "That code is not right. Check it and try again.");
        }

        user.ConfirmEmail(now);
        db.ActivityEvents.Add(ActivityEvent.Record(user.Id, ActivityType.EmailVerified, now));
        await db.SaveChangesAsync(ct);

        return Results.Ok(AuthEndpoints.ToUserResponse(user));
    }

    private static async Task<IResult> ResendAsync(
        ClaimsPrincipal principal,
        WordOsDbContext db,
        IPasswordHasher hasher,
        IEmailSender emailSender,
        WordOsConfiguration config,
        HttpResponse response,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        await using var serialised = await RowLock.AcquireAsync(db, "email-code", userId.Value, ct);

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Results.Unauthorized();

        if (!user.AwaitsEmailVerification)
            return Problems.Conflict("EMAIL_ALREADY_VERIFIED", "This email address is already verified.");

        // The cooldown lives on the account row rather than in memory, so a
        // restart or a second instance cannot be used to send a flood.
        var now = clock.GetUtcNow();
        var wait = user.SecondsUntilEmailCodeResend(now, config.EmailVerificationResendSeconds);
        if (wait > 0)
        {
            response.Headers.RetryAfter = wait.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Json(
                new { error = new { code = "RATE_LIMITED", message = "Wait a moment before asking for another code.", retryAfterSeconds = wait } },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        await SendCodeAsync(user, db, hasher, emailSender, config, now, ct);
        return Results.Accepted(value: new { resendAfterSeconds = config.EmailVerificationResendSeconds });
    }

    // ── Account deletion (ADR-140) ───────────────────────────────────────────

    /// <summary>
    /// Deletes the caller's account and everything that is theirs.
    /// </summary>
    /// <remarks>
    /// <para>Asks for the password again: a token left on a borrowed phone must
    /// not be enough to erase someone's learning.</para>
    /// <para>One row is deleted, and PostgreSQL removes the rest. Every table
    /// that holds a learner's data points at <c>users</c> with ON DELETE
    /// CASCADE — words and through them their skill states, events, exposures
    /// and review items; sessions and their items; placement; levels and level
    /// changes; interests; weekly reviews; feedback; activity and analytics
    /// events; refresh tokens and reset codes. The cascade is the schema's,
    /// so a table added later is covered by its own foreign key, and a test
    /// counts every one of them after a deletion.</para>
    /// <para>This is the exception R8's "kept, not deleted" was written around:
    /// that rule stops the <i>system</i> discarding a word; here the learner
    /// asks for all of it to go, which is theirs to ask (Google Play's account
    /// deletion policy requires it).</para>
    /// <para>Staff accounts are refused: decisions, notes and the audit trail
    /// name their author, and deleting an Owner from a phone could leave the
    /// product with nobody who can run it.</para>
    /// </remarks>
    private static async Task<IResult> DeleteAsync(
        DeleteAccountRequest request,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        IPasswordHasher hasher,
        AttemptLimiter attempts,
        HttpResponse response,
        ILoggerFactory logs,
        CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(request, out var errors))
            return Results.ValidationProblem(errors);

        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Results.Unauthorized();

        if (user.Role != UserRole.User)
        {
            return Problems.Forbidden(
                "STAFF_ACCOUNT",
                "Staff accounts cannot be deleted from the app.");
        }

        // The same per-account budget as sign-in: confirming a deletion is
        // guessing a password, and a stolen token must not make that free.
        if (attempts.CheckAccount("delete", user.Email, response) is { } limited) return limited;

        if (!await hasher.VerifyAsync(request.Password, user.PasswordHash, ct))
        {
            // 400, not 401: the session is fine, and a 401 makes the app treat
            // it as expired and sign the learner out mid-confirmation.
            return Problems.BadRequest("WRONG_PASSWORD", "That password is not right.");
        }

        var deleted = await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);

        // The id only: the address and name went with the row.
        logs.CreateLogger("WordOs.Account").LogInformation(
            "Account {UserId} deleted at the learner's request.", userId);

        return deleted == 0 ? Results.Unauthorized() : Results.NoContent();
    }

    // ── The email ────────────────────────────────────────────────────────────

    private static EmailMessage Compose(string address, string displayName, string code, int minutes)
    {
        var name = WebUtility.HtmlEncode(displayName);
        var subject = "WordOS — رمز تأكيد البريد / email verification code";

        var text =
            $"""
            مرحباً {displayName}،

            رمز تأكيد بريدك في WordOS هو:

                {code}

            ينتهي خلال {minutes} دقيقة. إن لم تنشئ حساباً في WordOS، تجاهل هذه الرسالة.

            ——

            Hello {displayName},

            Your WordOS email verification code is:

                {code}

            It expires in {minutes} minutes. If you did not create a WordOS account, ignore this email.
            """;

        var html =
            $"""
            <div dir="rtl" style="font-family:-apple-system,Segoe UI,Roboto,sans-serif;font-size:16px;color:#1a1a1a">
              <p>مرحباً {name}،</p>
              <p>رمز تأكيد بريدك في WordOS هو:</p>
              <p style="font-size:32px;font-weight:700;letter-spacing:6px;direction:ltr;text-align:center;margin:24px 0">{code}</p>
              <p>ينتهي خلال {minutes} دقيقة. إن لم تنشئ حساباً في WordOS، تجاهل هذه الرسالة.</p>
            </div>
            <hr style="border:none;border-top:1px solid #e0e0e0;margin:28px 0">
            <div dir="ltr" style="font-family:-apple-system,Segoe UI,Roboto,sans-serif;font-size:16px;color:#1a1a1a">
              <p>Hello {name},</p>
              <p>Your WordOS email verification code is:</p>
              <p style="font-size:32px;font-weight:700;letter-spacing:6px;text-align:center;margin:24px 0">{code}</p>
              <p>It expires in {minutes} minutes. If you did not create a WordOS account, ignore this email.</p>
            </div>
            """;

        return new EmailMessage(address, displayName, subject, html, text);
    }
}
