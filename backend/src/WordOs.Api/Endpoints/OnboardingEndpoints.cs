using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Levels;
using WordOs.Domain.Users;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Endpoints;

public static class OnboardingEndpoints
{
    public sealed record InterestOptionResponse(
        string Slug,
        string LabelEn,
        string LabelAr,
        string Emoji);

    public sealed record SaveInterestsRequest(
        [property: Required, MinLength(1)] IReadOnlyList<string> Interests);

    /// <summary>
    /// The interest catalogue.
    /// </summary>
    /// <remarks>
    /// Deliberately not exhaustive: the client also offers "Other" so a learner
    /// can type something we have never seen. Those arrive as free text and are
    /// stored with <c>IsCustom</c>, which is the signal for growing this list
    /// (demo review §3.1).
    /// </remarks>
    private static readonly InterestOptionResponse[] Catalogue =
    [
        new("technology", "Technology", "التقنية", "💻"),
        new("programming", "Programming", "البرمجة", "⌨️"),
        new("ai", "Artificial Intelligence", "الذكاء الاصطناعي", "🤖"),
        new("football", "Football", "كرة القدم", "⚽"),
        new("business", "Business", "الأعمال", "📈"),
        new("entrepreneurship", "Entrepreneurship", "ريادة الأعمال", "🚀"),
        new("economics", "Economics", "الاقتصاد", "💹"),
        new("medicine", "Medicine", "الطب", "🩺"),
        new("travel", "Travel", "السفر", "✈️"),
        new("history", "History", "التاريخ", "🏛️"),
        new("science", "Science", "العلوم", "🔬"),
    ];

    public static IEndpointRouteBuilder MapOnboardingEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/onboarding/interests", () => Results.Ok(Catalogue))
            .RequireAuthorization()
            .WithTags("Onboarding");

        app.MapPut("/api/me/interests", SaveInterestsAsync)
            .RequireAuthorization()
            .WithTags("Onboarding");

        return app;
    }

    private static async Task<IResult> SaveInterestsAsync(
        SaveInterestsRequest request,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(request, out var errors))
            return Results.ValidationProblem(errors);

        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        // Bounded so a request cannot carry ten thousand interests.
        if (request.Interests.Count > 50)
            return Problems.BadRequest("TOO_MANY_INTERESTS", "Choose up to 50.");

        var cleaned = request.Interests
            .Select(i => i?.Trim() ?? string.Empty)
            .Where(i => i.Length is > 0 and <= 40)
            .ToList();

        if (cleaned.Count == 0)
        {
            return Problems.BadRequest(
                "NO_INTERESTS",
                "Keep at least one interest so we can shape your content.");
        }

        var user = await db.Users
            .Include(u => u.Interests)
            .Include(u => u.SkillLevels)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return Results.Unauthorized();

        // Removal and insertion are both stated explicitly against the DbSet.
        //
        // Entities generate their own key in the property initialiser, so a
        // child appended to an already-tracked parent looks to EF like a row
        // that exists: it emits UPDATE, matches nothing, and throws a
        // concurrency exception. Marking them Added/Deleted directly is
        // unambiguous. `previous` is captured first because ReplaceInterests
        // clears the same list.
        var previous = user.Interests.ToList();
        db.UserInterests.RemoveRange(previous);

        // The domain decides whether each entry is custom and advances the
        // onboarding stage — the client never sends either (rule R1).
        user.ReplaceInterests(cleaned, DateTimeOffset.UtcNow);
        db.UserInterests.AddRange(user.Interests);

        await db.SaveChangesAsync(ct);

        // One profile shape everywhere: register, login, refresh, /me and here.
        // Assembling it by hand in each place is how a field goes missing from
        // exactly one response and nobody notices until the UI reads a default.
        return Results.Ok(AuthEndpoints.ToUserResponse(user));
    }
}

public static class SettingsEndpoints
{
    public sealed record UpdateSkillLevelRequest(
        [property: Required] string Skill,
        [property: Required] string Level);

    public sealed record UpdateDailyTargetRequest(
        [property: Required] string Skill,
        [property: Range(1, 100)] int Target);

    public static IEndpointRouteBuilder MapSettingsEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings")
            .WithTags("Settings")
            .RequireAuthorization();

        group.MapGet("", GetAsync);
        group.MapPatch("/skill-level", UpdateSkillLevelAsync);
        group.MapPatch("/daily-target", UpdateDailyTargetAsync);

        // Public tunables. No secret and no per-user data, but still
        // authenticated — an anonymous caller has no reason to need it.
        app.MapGet("/api/config", (WordOsConfiguration config) => Results.Ok(new
        {
            skillIntervalDays = config.SkillIntervalDays,
            minDailyTarget = config.MinDailyTarget,
            maxDailyTarget = config.MaxDailyTarget,
            defaultDailyTarget = config.DefaultDailyTarget,
            weeklyReviewPeriodDays = config.WeeklyReviewPeriodDays,
            skillsOrder = config.SkillsOrder
                .Select(s => s.ToWire()).ToList(),
            cefrLevels = Enum.GetValues<CefrLevel>()
                .Select(l => l.ToWire()).ToList(),
        })).RequireAuthorization().WithTags("Settings");

        return app;
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal principal,
        WordOsDbContext db,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        var levels = await db.SkillLevels
            .Where(l => l.UserId == userId)
            .ToListAsync(ct);

        return Results.Ok(new { skillLevels = levels.Select(ToResponse).ToList() });
    }

    /// <summary>
    /// Changes the learner's <b>chosen</b> level.
    /// </summary>
    /// <remarks>
    /// Rule R6: this moves <c>UserSelectedLevel</c> only. It can never touch
    /// what the system has validated, and it can never archive a word — a
    /// self-declared level is a content preference, not evidence.
    ///
    /// Spelling is refused with <c>SKILL_NOT_LEVELLED</c>: it is measured but
    /// carries no CEFR band (ADR-008).
    /// </remarks>
    private static async Task<IResult> UpdateSkillLevelAsync(
        UpdateSkillLevelRequest request,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(request, out var errors))
            return Results.ValidationProblem(errors);

        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        if (!Enum.TryParse<SkillType>(request.Skill, ignoreCase: true, out var skill))
            return Problems.BadRequest("INVALID_SKILL", "Unknown skill.");

        var level = CefrLevelExtensions.TryFromWire(request.Level);
        if (level is null)
            return Problems.BadRequest("INVALID_LEVEL", "Unknown CEFR level.");

        var row = await db.SkillLevels
            .FirstOrDefaultAsync(l => l.UserId == userId && l.Skill == skill, ct);
        if (row is null) return Problems.NotFound("NOT_FOUND", "Skill not found.");

        if (!row.CarriesCefrLevel)
        {
            return Problems.BadRequest(
                "SKILL_NOT_LEVELLED",
                "This skill is measured but does not carry a CEFR level.");
        }

        var previous = row.UserSelectedLevel;
        row.SetUserSelectedLevel(level.Value);

        // Logged so the dashboard can compare what learners claim against what
        // the system proved (MVP Core §60).
        db.LevelChanges.Add(LevelChangeRecord.Create(
            userId.Value, skill, previous, level,
            LevelChangeType.UserManualChange, clock.GetUtcNow()));

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(row));
    }

    private static async Task<IResult> UpdateDailyTargetAsync(
        UpdateDailyTargetRequest request,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        WordOsConfiguration config,
        CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(request, out var errors))
            return Results.ValidationProblem(errors);

        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        if (!Enum.TryParse<SkillType>(request.Skill, ignoreCase: true, out var skill))
            return Problems.BadRequest("INVALID_SKILL", "Unknown skill.");

        var row = await db.SkillLevels
            .FirstOrDefaultAsync(l => l.UserId == userId && l.Skill == skill, ct);
        if (row is null) return Problems.NotFound("NOT_FOUND", "Skill not found.");

        // Clamped by the domain, not by the client. A value outside the range
        // is corrected rather than rejected, and the check constraint in the
        // schema is the last line of defence.
        row.SetDailyTarget(request.Target, config);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(row));
    }

    internal static object ToResponse(Domain.Levels.SkillLevel l) => new
    {
        skill = l.Skill.ToWire(),
        userSelectedLevel = l.UserSelectedLevel?.ToWire(),
        systemAssessedLevel = l.SystemAssessedLevel?.ToWire(),
        evaluationSessions = l.EvaluationSessions,
        rollingAccuracy = l.RollingAccuracy,
        dailyTargetWords = l.DailyTargetWords,
        confidence = l.Confidence,
        spellingSupportMode = l.SpellingSupportMode?.ToWire(),
    };
}
