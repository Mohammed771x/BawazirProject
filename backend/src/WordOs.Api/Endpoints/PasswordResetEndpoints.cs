using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WordOs.Application.Abstractions;
using WordOs.Domain.Common;
using WordOs.Domain.Users;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Endpoints;

/// <summary>
/// Forgotten-password recovery: a six-digit code by email (ADR-078).
/// </summary>
/// <remarks>
/// The governing rule of both endpoints is that <b>neither may reveal whether
/// an email address is registered</b>. A learner who mistypes their address
/// and an attacker probing for accounts must get byte-identical answers —
/// including when the provider is down, and including in how long the request
/// takes (docs/07-SECURITY.md §2, §6).
/// </remarks>
public static class PasswordResetEndpoints
{
    public sealed record ForgotPasswordRequest(
        [property: Required, MaxLength(320)] string Email);

    public sealed record ResetPasswordRequest(
        [property: Required, MaxLength(320)] string Email,
        [property: Required, RegularExpression(@"^[0-9]{6}$")] string Code,
        [property: Required, MinLength(8), MaxLength(128)] string NewPassword);

    public static IEndpointRouteBuilder MapPasswordResetEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth/password").WithTags("Auth");

        // The authentication budget, not a looser one: these are
        // credential-adjacent endpoints and belong in the same partition as
        // login for exactly the same reason.
        group.MapPost("/forgot", ForgotAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Authentication);

        group.MapPost("/reset", ResetAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Authentication);

        return app;
    }

    private static async Task<IResult> ForgotAsync(
        ForgotPasswordRequest request,
        WordOsDbContext db,
        IPasswordHasher hasher,
        IEmailSender email,
        WordOsConfiguration config,
        TimeProvider clock,
        ILoggerFactory logs,
        CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(request, out var errors))
            return Results.ValidationProblem(errors);

        var address = request.Email.Trim().ToLowerInvariant();
        var now = clock.GetUtcNow();

        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Email == address, ct);

        if (user is not null)
        {
            // Asking again retires whatever was outstanding. Without this, a
            // learner who requests three codes has three live doors and
            // fifteen guesses instead of five, and the attempt cap stops
            // meaning anything.
            var outstanding = await db.PasswordResetCodes
                .Where(c => c.UserId == user.Id
                    && c.UsedAt == null && c.InvalidatedAt == null)
                .ToListAsync(ct);
            foreach (var old in outstanding) old.Invalidate(now);

            var code = GenerateCode();

            db.PasswordResetCodes.Add(PasswordResetCode.Issue(
                user.Id,
                await hasher.HashAsync(code, ct),
                now,
                now.AddMinutes(config.PasswordResetCodeExpiryMinutes)));

            await db.SaveChangesAsync(ct);

            // Deliberately after the save: a code that was emailed but not
            // stored is a code that cannot work, which is the worse of the two
            // failures. The other way round merely wastes a row.
            //
            // The result is ignored on purpose — see the response below.
            await email.SendAsync(
                Compose(address, user.DisplayName, code,
                    config.PasswordResetCodeExpiryMinutes),
                ct);
        }

        // Identical for a known address, an unknown one, and a provider
        // outage. 202 rather than 200 because it is honest: the request was
        // accepted, and whether anything was sent is not being stated.
        return Results.Accepted(value: new
        {
            message = "If that email is registered, a code is on its way.",
        });
    }

    private static async Task<IResult> ResetAsync(
        ResetPasswordRequest request,
        WordOsDbContext db,
        IPasswordHasher hasher,
        WordOsConfiguration config,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(request, out var errors))
            return Results.ValidationProblem(errors);

        var address = request.Email.Trim().ToLowerInvariant();
        var now = clock.GetUtcNow();

        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Email == address, ct);

        // The newest code this learner holds. Older ones were invalidated when
        // it was issued, so there is only ever one to consider.
        var stored = user is null
            ? null
            : await db.PasswordResetCodes
                .Where(c => c.UserId == user.Id)
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);

        // An unknown email still pays for a verification, so the time taken
        // does not separate "no such account" from "wrong code" — the same
        // defence login uses against enumeration.
        var matches = stored is not null
            ? await hasher.VerifyAsync(request.Code, stored.CodeHash, ct)
            : await hasher.VerifyAsync(request.Code, DummyCodeHash.Value, ct) && false;

        if (user is null || stored is null || !matches
            || !stored.IsRedeemable(now, config.PasswordResetMaxAttempts))
        {
            // A wrong guess against a real, live code costs one of its five
            // attempts. A wrong *email* costs nothing, because there is no row
            // to charge — which is invisible from outside, as both answers are
            // this one.
            if (stored is not null && !matches
                && stored.IsRedeemable(now, config.PasswordResetMaxAttempts))
            {
                stored.RecordFailedAttempt(config.PasswordResetMaxAttempts);
                await db.SaveChangesAsync(ct);
            }

            return Problems.BadRequest(
                "INVALID_RESET_CODE",
                "That code is wrong or has expired. Request a new one.");
        }

        user.ChangePassword(await hasher.HashAsync(request.NewPassword, ct));
        stored.MarkUsed(now);

        // Everything else that could open this account is closed. Whoever knew
        // the old password — which is the case a reset exists to answer — is
        // signed out of every device, not merely prevented from signing in
        // again (docs/07-SECURITY.md §2).
        var sessions = await db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var session in sessions) session.Revoke(now);

        db.ActivityEvents.Add(
            ActivityEvent.Record(user.Id, ActivityType.PasswordReset, now));

        await db.SaveChangesAsync(ct);

        // No tokens in the response: a reset ends with the learner signing in
        // with the password they just chose. Handing back a session here would
        // mean a single stolen code is a session, with no second factor of
        // "and they know the new password" in the way.
        return Results.Ok(new { message = "Password changed. Please sign in." });
    }

    /// <summary>
    /// Six digits from a cryptographic source.
    /// </summary>
    /// <remarks>
    /// <c>RandomNumberGenerator</c>, never <c>Random</c>: a predictable code is
    /// no code at all, and <c>Random</c> seeded from the clock is guessable by
    /// anyone who knows roughly when the request was made.
    ///
    /// The full range including leading zeros — "004821" is as good a code as
    /// any, and excluding them would throw away a tenth of the space for
    /// cosmetics.
    /// </remarks>
    private static string GenerateCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private static EmailMessage Compose(
        string address, string displayName, string code, int minutes)
    {
        // Bilingual because the server does not know which language the app is
        // showing, and Arabic is the default (docs/08-FINAL-SPECIFICATION.md).
        // Guessing wrong would hand a learner a message they cannot read.
        var subject = $"WordOS — رمز إعادة تعيين كلمة المرور / password reset code";

        var text =
            $"""
            مرحباً {displayName}،

            رمز إعادة تعيين كلمة المرور الخاص بك هو:

                {code}

            ينتهي خلال {minutes} دقيقة. إن لم تطلب هذا، تجاهل هذه الرسالة —
            لم يتغير شيء في حسابك.

            ——

            Hello {displayName},

            Your WordOS password reset code is:

                {code}

            It expires in {minutes} minutes. If you did not ask for this,
            ignore this email — nothing about your account has changed.
            """;

        // Inline styles only: every mail client strips <style> blocks, and
        // several strip <head> with them.
        var html =
            $"""
            <div dir="rtl" style="font-family:-apple-system,Segoe UI,Roboto,sans-serif;font-size:16px;color:#1a1a1a">
              <p>مرحباً {Escape(displayName)}،</p>
              <p>رمز إعادة تعيين كلمة المرور الخاص بك هو:</p>
              <p style="font-size:32px;font-weight:700;letter-spacing:6px;direction:ltr;text-align:center;margin:24px 0">{code}</p>
              <p>ينتهي خلال {minutes} دقيقة. إن لم تطلب هذا، تجاهل هذه الرسالة — لم يتغير شيء في حسابك.</p>
            </div>
            <hr style="border:none;border-top:1px solid #e0e0e0;margin:28px 0">
            <div dir="ltr" style="font-family:-apple-system,Segoe UI,Roboto,sans-serif;font-size:16px;color:#1a1a1a">
              <p>Hello {Escape(displayName)},</p>
              <p>Your WordOS password reset code is:</p>
              <p style="font-size:32px;font-weight:700;letter-spacing:6px;text-align:center;margin:24px 0">{code}</p>
              <p>It expires in {minutes} minutes. If you did not ask for this, ignore this email — nothing about your account has changed.</p>
            </div>
            """;

        return new EmailMessage(address, displayName, subject, html, text);
    }

    /// <summary>
    /// A display name is learner-written text and lands in an HTML document
    /// (docs/07-SECURITY.md §14).
    /// </summary>
    private static string Escape(string value) =>
        System.Net.WebUtility.HtmlEncode(value);

    /// <summary>
    /// A real Argon2id hash, so a request for an unregistered address costs the
    /// same time as one for a registered address holding a live code.
    /// </summary>
    private static class DummyCodeHash
    {
        public static readonly string Value =
            new Infrastructure.Security.Argon2PasswordHasher().Hash("000000");
    }
}
