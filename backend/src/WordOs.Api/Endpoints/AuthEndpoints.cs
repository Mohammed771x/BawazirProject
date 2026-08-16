using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WordOs.Application.Abstractions;
using WordOs.Domain.Common;
using WordOs.Domain.Levels;
using WordOs.Domain.Users;
using WordOs.Infrastructure.Persistence;
using WordOs.Infrastructure.Security;

namespace WordOs.Api.Endpoints;

public static class AuthEndpoints
{
    // Length caps are enforced here as well as in the schema, so an oversized
    // body is rejected before it reaches the database (docs/07-SECURITY.md §5).
    //
    // `[property: ...]` is required, not stylistic: on a positional record an
    // attribute binds to the constructor *parameter* by default, where
    // Validator.TryValidateObject never looks — so the annotations would be
    // silently inert and every one of these rules would go unenforced.
    public sealed record RegisterRequest(
        [property: Required, EmailAddress, MaxLength(320)] string Email,
        [property: Required, MinLength(8), MaxLength(128)] string Password,
        [property: Required, MaxLength(120)] string DisplayName);

    public sealed record LoginRequest(
        [property: Required, MaxLength(320)] string Email,
        [property: Required, MaxLength(128)] string Password);

    public sealed record RefreshRequest([property: Required] string RefreshToken);

    public sealed record AuthResponse(
        string Token,
        DateTimeOffset ExpiresAt,
        string RefreshToken,
        UserResponse User);

    public sealed record UserResponse(
        Guid Id,
        string Email,
        string DisplayName,
        string Role,
        string OnboardingStage,
        IReadOnlyList<string> Interests,
        // Part of the profile because the client renders levels and daily
        // targets straight from it and must never invent a default: an absent
        // list would silently display A1 for a C1 learner.
        IReadOnlyList<SkillLevelResponse> SkillLevels,
        DateTimeOffset CreatedAt);

    /// <summary>
    /// One skill's levels. Both bands are null for Spelling — measured, but
    /// carrying no CEFR band at all (ADR-008). Clients branch on the skill,
    /// never on a sentinel level.
    /// </summary>
    public sealed record SkillLevelResponse(
        string Skill,
        string? UserSelectedLevel,
        string? SystemAssessedLevel,
        int EvaluationSessions,
        double RollingAccuracy,
        int DailyTargetWords,
        double Confidence,
        string? SpellingSupportMode);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", RegisterAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Authentication);

        group.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Authentication);

        group.MapPost("/refresh", RefreshAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Authentication);

        group.MapPost("/logout", LogoutAsync).RequireAuthorization();

        app.MapGet("/api/me", MeAsync).RequireAuthorization().WithTags("Auth");

        return app;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        WordOsDbContext db,
        IPasswordHasher hasher,
        JwtTokenService tokens,
        WordOsConfiguration config,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(request, out var errors))
            return Results.ValidationProblem(errors);

        var email = request.Email.Trim().ToLowerInvariant();
        var now = clock.GetUtcNow();

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return Problems.Conflict(
                "EMAIL_TAKEN", "This email is already registered.");
        }

        // Role is never taken from the request. Registration always creates a
        // learner; there is no client-reachable path to Owner
        // (docs/07-SECURITY.md §3).
        var user = User.Register(
            email, hasher.Hash(request.Password), request.DisplayName,
            config, now);

        db.Users.Add(user);

        var issued = tokens.Issue(user, now);
        db.RefreshTokens.Add(RefreshToken.Issue(
            user.Id,
            JwtTokenService.HashRefreshToken(issued.RefreshToken),
            now,
            issued.RefreshExpiresAt));

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToAuthResponse(user, issued));
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        WordOsDbContext db,
        IPasswordHasher hasher,
        JwtTokenService tokens,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(request, out var errors))
            return Results.ValidationProblem(errors);

        var email = request.Email.Trim().ToLowerInvariant();
        var now = clock.GetUtcNow();

        var user = await db.Users
            .Include(u => u.Interests)
            .Include(u => u.SkillLevels)
            .FirstOrDefaultAsync(u => u.Email == email, ct);

        // The same response whether the email is unknown or the password is
        // wrong, so the endpoint cannot be used to enumerate accounts. The hash
        // is still verified against a dummy when the user is missing, so the
        // timing does not give it away either.
        var valid = user is not null
            ? hasher.Verify(request.Password, user.PasswordHash)
            : hasher.Verify(request.Password, DummyHash.Value) && false;

        if (user is null || !valid)
        {
            return Problems.Unauthorized(
                "INVALID_CREDENTIALS", "Wrong email or password.");
        }

        user.RecordLogin(now);

        var issued = tokens.Issue(user, now);
        db.RefreshTokens.Add(RefreshToken.Issue(
            user.Id,
            JwtTokenService.HashRefreshToken(issued.RefreshToken),
            now,
            issued.RefreshExpiresAt));

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToAuthResponse(user, issued));
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        WordOsDbContext db,
        JwtTokenService tokens,
        TimeProvider clock,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hash = JwtTokenService.HashRefreshToken(request.RefreshToken);

        var stored = await db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored is null)
            return Problems.Unauthorized("INVALID_REFRESH", "Please sign in again.");

        // Presenting an already-used token means it leaked: revoke the whole
        // rotation family rather than merely refusing this one.
        if (stored.UsedAt is not null)
        {
            var family = await db.RefreshTokens
                .Where(t => t.FamilyId == stored.FamilyId && t.RevokedAt == null)
                .ToListAsync(ct);
            foreach (var token in family) token.Revoke(now);
            await db.SaveChangesAsync(ct);

            return Problems.Unauthorized(
                "REFRESH_REUSED", "Please sign in again.");
        }

        if (!stored.IsActive(now))
            return Problems.Unauthorized("INVALID_REFRESH", "Please sign in again.");

        var user = await db.Users
            .Include(u => u.Interests)
            .Include(u => u.SkillLevels)
            .FirstOrDefaultAsync(u => u.Id == stored.UserId, ct);
        if (user is null)
            return Problems.Unauthorized("INVALID_REFRESH", "Please sign in again.");

        stored.MarkUsed(now);

        var issued = tokens.Issue(user, now);
        db.RefreshTokens.Add(RefreshToken.Issue(
            user.Id,
            JwtTokenService.HashRefreshToken(issued.RefreshToken),
            now,
            issued.RefreshExpiresAt,
            stored.FamilyId));

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToAuthResponse(user, issued));
    }

    private static async Task<IResult> LogoutAsync(
        ClaimsPrincipal principal,
        WordOsDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.NoContent();

        // Revoke every outstanding refresh token: signing out must actually end
        // the session, not only drop the client's copy.
        var active = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(ct);

        var now = clock.GetUtcNow();
        foreach (var token in active) token.Revoke(now);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(
        ClaimsPrincipal principal,
        WordOsDbContext db,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        // Scoped by the caller's own id from the token — never by a route
        // parameter (docs/07-SECURITY.md §4).
        var user = await db.Users
            .Include(u => u.Interests)
            .Include(u => u.SkillLevels)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        return user is null
            ? Results.Unauthorized()
            : Results.Ok(ToUserResponse(user));
    }

    private static AuthResponse ToAuthResponse(
        User user,
        JwtTokenService.IssuedToken issued) =>
        new(issued.AccessToken, issued.ExpiresAt, issued.RefreshToken,
            ToUserResponse(user));

    public static UserResponse ToUserResponse(User user) =>
        new(user.Id,
            user.Email,
            user.DisplayName,
            user.Role.ToWire(),
            user.OnboardingStage.ToWire(),
            user.Interests.Select(i => i.Interest).ToList(),
            user.SkillLevels.Select(ToSkillLevelResponse).ToList(),
            user.CreatedAt);

    public static SkillLevelResponse ToSkillLevelResponse(SkillLevel level) =>
        new(level.Skill.ToWire(),
            level.UserSelectedLevel?.ToWire(),
            level.SystemAssessedLevel?.ToWire(),
            level.EvaluationSessions,
            level.RollingAccuracy,
            level.DailyTargetWords,
            level.Confidence,
            level.SpellingSupportMode?.ToWire());

    /// <summary>
    /// A real Argon2id hash of a throwaway value, used so an unknown email
    /// costs the same time as a known one.
    /// </summary>
    private static class DummyHash
    {
        public static readonly string Value =
            new Argon2PasswordHasher().Hash("not-a-real-password");
    }
}
