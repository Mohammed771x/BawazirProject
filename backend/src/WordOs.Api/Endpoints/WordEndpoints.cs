using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Common;
using WordOs.Domain.Words;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Endpoints;

public static class WordEndpoints
{
    public sealed record WordCandidateResponse(
        string SenseId,
        string Text,
        string Meaning,
        string DefinitionEn,
        string PartOfSpeech,
        string? SuggestedLevel,
        bool IsSpellingSuggestion);

    // `[property: ...]` — see the note in AuthEndpoints: without it the
    // annotations bind to the constructor parameter and never run.
    public sealed record AddWordRequest(
        [property: Required, MaxLength(64)] string SenseId,
        [property: MaxLength(128)] string? Text,
        [property: MaxLength(256)] string? Meaning);

    public sealed record WordResponse(
        Guid Id,
        string SenseId,
        string Text,
        string Meaning,
        string DefinitionEn,
        string PartOfSpeech,
        string CefrLevel,
        string State,
        string? CurrentSkill,
        DateTimeOffset AddedAt,
        DateTimeOffset? NextEligibleAt,
        int ExposureCount,
        // The five per-skill rows. The client draws the pipeline from these
        // rather than inferring it from `currentSkill` — which is exactly the
        // difference between rendering server state and recomputing it (R1).
        IReadOnlyList<WordSkillResponse> Skills);

    public sealed record WordSkillResponse(
        string Skill,
        string Status,
        DateTimeOffset? AvailableAt,
        int Attempts,
        DateTimeOffset? PassedAt);

    public sealed record WordEventResponse(
        string Type,
        string? Skill,
        DateTimeOffset CreatedAt);

    public static IEndpointRouteBuilder MapWordEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/words")
            .WithTags("Words")
            .RequireAuthorization();

        group.MapGet("/lookup", LookupAsync)
            .RequireRateLimiting(RateLimitPolicies.Lookup);

        group.MapPost("", AddAsync);
        group.MapGet("", ListAsync);
        group.MapGet("/{id:guid}", DetailAsync);

        return app;
    }

    /// <summary>
    /// Prefix search over the lexicon.
    /// </summary>
    /// <remarks>
    /// Typing <c>bo</c> returns every sense whose word starts with those
    /// letters, each carrying the word, its CEFR level and the Arabic meaning
    /// of <i>that sense</i>.
    ///
    /// The result set is bounded and a minimum query length is required, so the
    /// endpoint cannot be walked to dump the lexicon
    /// (docs/07-SECURITY.md §6).
    /// </remarks>
    private static async Task<IResult> LookupAsync(
        string? q,
        WordOsDbContext db,
        CancellationToken ct)
    {
        var query = (q ?? string.Empty).Trim().ToLowerInvariant();

        if (query.Length < 2)
            return Results.Ok(Array.Empty<WordCandidateResponse>());
        if (query.Length > 64)
            return Problems.BadRequest("QUERY_TOO_LONG", "Search term is too long.");

        // Parameterised by EF Core — the query never becomes SQL text.
        var matches = await db.LexiconEntries
            .Where(l => l.TextNormalized.StartsWith(query))
            .OrderBy(l => l.FrequencyRank)
            .ThenBy(l => l.TextNormalized)
            .ThenBy(l => l.SenseId)
            .Take(25)
            .Select(l => new WordCandidateResponse(
                l.SenseId, l.Text, l.MeaningAr, l.DefinitionEn, l.PartOfSpeech,
                l.CefrLevel != null ? l.CefrLevel.Value.ToWire() : null,
                false))
            .ToListAsync(ct);

        return Results.Ok(matches);
    }

    /// <summary>
    /// Adds a word to the learner's pipeline.
    /// </summary>
    /// <remarks>
    /// The request body is a <b>lookup key</b>, never a source of truth: the
    /// sense is re-resolved against the lexicon and the stored row is what gets
    /// copied, so a forged level, definition or meaning is discarded
    /// (ADR-012, docs/07-SECURITY.md §5).
    /// </remarks>
    private static async Task<IResult> AddAsync(
        AddWordRequest request,
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

        var entry = await db.LexiconEntries
            .FirstOrDefaultAsync(l => l.SenseId == request.SenseId, ct);

        if (entry is null)
        {
            return Problems.NotFound(
                "WORD_NOT_FOUND",
                "That word and meaning are not in the dictionary.");
        }

        // Duplicate identity is the sense, scoped to this learner.
        var duplicate = await db.Words.AnyAsync(
            w => w.UserId == userId && w.SenseId == entry.SenseId, ct);
        if (duplicate)
        {
            return Problems.Conflict(
                "WORD_ALREADY_ADDED",
                "You have already added this word with this meaning.");
        }

        var word = Word.Add(
            userId.Value,
            entry.SenseId,
            entry.Text,
            entry.MeaningAr,
            entry.DefinitionEn,
            entry.PartOfSpeech,
            // A sense with no CEFR band still enters the pipeline; B1 is the
            // neutral default for content generation, and the level engine
            // corrects it from real performance.
            entry.CefrLevel ?? CefrLevel.B1,
            config,
            clock.GetUtcNow());

        db.Words.Add(word);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(word));
    }

    private static async Task<IResult> ListAsync(
        string? state,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        // Always scoped to the caller's own id from the token. A word id or
        // user id from the request is never trusted (docs/07-SECURITY.md §4).
        //
        // The skills are part of the response, so they are loaded here — EF does
        // no lazy loading, and reading an unloaded navigation would silently
        // report every word as having no pipeline at all.
        var query = db.Words.Include(w => w.Skills).Where(w => w.UserId == userId);

        if (!string.IsNullOrWhiteSpace(state))
        {
            if (!Enum.TryParse<WordState>(state, ignoreCase: true, out var parsed))
                return Problems.BadRequest("INVALID_STATE", "Unknown word state.");
            query = query.Where(w => w.State == parsed);
        }

        var items = await query
            .OrderByDescending(w => w.AddedAt)
            .Take(500)
            .ToListAsync(ct);

        return Results.Ok(new
        {
            items = items.Select(ToResponse).ToList(),
            total = items.Count,
        });
    }

    /// <summary>
    /// One word with its full history.
    /// </summary>
    /// <remarks>
    /// Scoped to the caller's own id: a word id from another learner is not
    /// addressable, and returns 404 rather than 403 so the id itself reveals
    /// nothing (docs/07-SECURITY.md §4).
    /// </remarks>
    private static async Task<IResult> DetailAsync(
        Guid id,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        var word = await db.Words
            .Include(w => w.Skills)
            .Include(w => w.Events)
            .FirstOrDefaultAsync(w => w.Id == id && w.UserId == userId, ct);

        if (word is null)
            return Problems.NotFound("WORD_NOT_FOUND", "Word not found.");

        var response = ToResponse(word);

        return Results.Ok(new
        {
            response.Id,
            response.SenseId,
            response.Text,
            response.Meaning,
            response.DefinitionEn,
            response.PartOfSpeech,
            response.CefrLevel,
            response.State,
            response.CurrentSkill,
            response.AddedAt,
            response.NextEligibleAt,
            response.ExposureCount,
            response.Skills,
            events = word.Events
                .OrderBy(e => e.CreatedAt)
                .Select(e => new WordEventResponse(
                    e.Type.ToWire(), e.Skill?.ToWire(), e.CreatedAt))
                .ToList(),
        });
    }

    private static WordResponse ToResponse(Word w) =>
        new(w.Id, w.SenseId, w.Text, w.Meaning, w.DefinitionEn, w.PartOfSpeech,
            w.CefrLevel.ToWire(),
            w.State.ToWire(),
            w.CurrentSkill?.ToWire(),
            w.AddedAt,
            // When the word is due next: the schedule of the skill it is
            // currently on, and nothing once it has matured.
            w.CurrentSkill is null
                ? null
                : w.SkillState(w.CurrentSkill.Value).AvailableAt,
            w.ExposureCount,
            w.Skills
                .OrderBy(s => s.Skill)
                .Select(s => new WordSkillResponse(
                    s.Skill.ToWire(), s.Status.ToWire(),
                    s.AvailableAt, s.Attempts, s.PassedAt))
                .ToList());
}
