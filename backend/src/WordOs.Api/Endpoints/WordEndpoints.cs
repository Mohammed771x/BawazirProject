using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using WordOs.Application.Lexicon;
using WordOs.Domain.Common;
using WordOs.Domain.Lexicon;
using WordOs.Domain.Users;
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

        group.MapGet("/define", DefineAsync)
            .RequireRateLimiting(RateLimitPolicies.Lookup);

        group.MapPost("", AddAsync);
        group.MapGet("", ListAsync);
        group.MapGet("/{id:guid}", DetailAsync);

        return app;
    }

    /// <summary>
    /// Search over the lexicon, in either language.
    /// </summary>
    /// <remarks>
    /// Typing <c>bo</c> returns every sense whose word starts with those
    /// letters, each carrying the word, its CEFR level and the Arabic meaning
    /// of <i>that sense</i>.
    ///
    /// Three things make it answer more than a plain prefix does:
    ///
    /// * <b>Arabic in, English out.</b> A query written in Arabic searches the
    ///   meanings instead of the spellings, so <c>يذهب</c> finds <c>go</c>
    ///   (ADR-034). Both sides are folded to one unvocalised form first,
    ///   because the glosses carry diacritics and nobody types them.
    /// * <b>Inflections.</b> When nothing starts with what was typed, the
    ///   surface form is resolved the way the reading screen resolves a tapped
    ///   word, so <c>went</c> finds <c>go</c> rather than nothing at all.
    /// * <b>One-letter words.</b> <c>a</c> and <c>I</c> are words; they are
    ///   matched exactly rather than as a prefix, which is what keeps a single
    ///   letter from returning a page of the dictionary.
    ///
    /// The result set is bounded and the query length is capped, so the
    /// endpoint cannot be walked to dump the lexicon (docs/07-SECURITY.md §6).
    /// </remarks>
    private static async Task<IResult> LookupAsync(
        string? q,
        WordOsDbContext db,
        CancellationToken ct)
    {
        // Control characters are stripped rather than searched for: PostgreSQL
        // refuses a NUL byte in a text value, and a pasted one is a crash, not
        // a query.
        var raw = SearchTerm.Clean(q);

        if (raw.Length == 0) return Results.Ok(Array.Empty<WordCandidateResponse>());
        if (raw.Length > 64)
            return Problems.BadRequest("QUERY_TOO_LONG", "Search term is too long.");

        if (ArabicText.ContainsArabic(raw))
            return Results.Ok(await SearchByMeaningAsync(raw, db, ct));

        var query = raw.ToLowerInvariant();

        // A single letter is a word, not a prefix: "a" and "I" must be
        // addable, and matching them as prefixes would return the dictionary.
        if (query.Length == 1)
        {
            return Results.Ok(await ProjectAsync(
                db.LexiconEntries.Where(l => l.TextNormalized == query), 25, ct));
        }

        var matches = await ProjectAsync(
            db.LexiconEntries.Where(l => l.TextNormalized.StartsWith(query)),
            25, ct);

        if (matches.Count > 0) return Results.Ok(matches);

        // Nothing starts with it. The learner may simply have typed the word as
        // they met it — "went", "studies", "running" — so the base forms are
        // tried before giving up. The exact spelling was already tried above.
        foreach (var candidate in SurfaceForms.CandidatesFor(query).Skip(1))
        {
            var resolved = await ProjectAsync(
                db.LexiconEntries.Where(l => l.TextNormalized == candidate), 25, ct);

            if (resolved.Count > 0) return Results.Ok(resolved);
        }

        return Results.Ok(Array.Empty<WordCandidateResponse>());
    }

    /// <summary>
    /// The English words whose Arabic meaning matches what was typed.
    /// </summary>
    /// <remarks>
    /// Ordered by how close the match is before how common the word is: a gloss
    /// that <i>is</i> the query outranks one that merely contains it, so
    /// <c>ذهب</c> offers the verb before "18-karat gold".
    /// </remarks>
    private static async Task<List<WordCandidateResponse>> SearchByMeaningAsync(
        string raw,
        WordOsDbContext db,
        CancellationToken ct)
    {
        var term = ArabicText.Normalize(raw);
        if (term.Length < 2) return [];

        // EF parameterises both; the term never becomes SQL text. The wildcards
        // are escaped so a learner typing % or _ searches for those characters.
        var contains = $"%{term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";

        return await db.LexiconEntries
            .Where(l => EF.Functions.Like(l.MeaningArNormalized, contains, "\\"))
            .OrderBy(l => l.MeaningArNormalized == term ? 0
                : l.MeaningArNormalized.StartsWith(term) ? 1 : 2)
            .ThenBy(l => l.FrequencyRank)
            .ThenBy(l => l.TextNormalized)
            .ThenBy(l => l.SenseId)
            .Take(25)
            .Select(l => new WordCandidateResponse(
                l.SenseId, l.Text, l.MeaningAr, l.DefinitionEn, l.PartOfSpeech,
                l.CefrLevel != null ? l.CefrLevel.Value.ToWire() : null,
                false))
            .ToListAsync(ct);
    }

    /// <summary>The wire shape, ordered the way autocomplete wants it.</summary>
    private static Task<List<WordCandidateResponse>> ProjectAsync(
        IQueryable<LexiconEntry> query,
        int take,
        CancellationToken ct) =>
        query
            .OrderBy(l => l.FrequencyRank)
            .ThenBy(l => l.TextNormalized)
            .ThenBy(l => l.SenseId)
            .Take(take)
            .Select(l => new WordCandidateResponse(
                l.SenseId, l.Text, l.MeaningAr, l.DefinitionEn, l.PartOfSpeech,
                l.CefrLevel != null ? l.CefrLevel.Value.ToWire() : null,
                false))
            .ToListAsync(ct);

    /// <summary>
    /// Exact lookup of a word as it appears in a passage.
    /// </summary>
    /// <remarks>
    /// The reading screen lets a learner tap any word to see what it means
    /// (Part 2 §17). That word arrives inflected — <c>researching</c>,
    /// <c>studies</c> — so the spelling is resolved here, against the lexicon,
    /// rather than guessed at on the device (rule R1).
    ///
    /// <c>matchedText</c> tells the client which spelling actually answered, so
    /// the sheet can say "researching → research" instead of silently showing a
    /// different word's definition.
    ///
    /// Senses come back for the resolved word only: this is a definition, not a
    /// search, and it must not become a second way to walk the lexicon.
    /// </remarks>
    private static async Task<IResult> DefineAsync(
        string? w,
        WordOsDbContext db,
        CancellationToken ct)
    {
        var word = SearchTerm.Clean(w);
        if (word.Length is 0 or > 64)
            return Problems.BadRequest("BAD_WORD", "Provide a single word.");

        foreach (var candidate in SurfaceForms.CandidatesFor(word))
        {
            var senses = await db.LexiconEntries
                .Where(l => l.TextNormalized == candidate)
                .OrderBy(l => l.FrequencyRank)
                .ThenBy(l => l.SenseId)
                .Take(6)
                .Select(l => new WordCandidateResponse(
                    l.SenseId, l.Text, l.MeaningAr, l.DefinitionEn,
                    l.PartOfSpeech,
                    l.CefrLevel != null ? l.CefrLevel.Value.ToWire() : null,
                    false))
                .ToListAsync(ct);

            if (senses.Count > 0)
            {
                return Results.Ok(new
                {
                    query = word,
                    matchedText = senses[0].Text,
                    senses,
                });
            }
        }

        // A name, a number, or a word the lexicon does not carry. Answering
        // 200-with-nothing rather than 404 keeps this a normal outcome for the
        // client: the word can still be pronounced, it just has no entry.
        return Results.Ok(new
        {
            query = word,
            matchedText = (string?)null,
            senses = Array.Empty<WordCandidateResponse>(),
        });
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
        db.ActivityEvents.Add(ActivityEvent.Record(
            userId.Value, ActivityType.WordAdded, clock.GetUtcNow(),
            entityId: word.Id));
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToResponse(word));
    }

    private static async Task<IResult> ListAsync(
        string? state,
        string? q,
        // Nullable so the parameters are genuinely optional: a minimal API
        // rejects a missing non-nullable query value outright, which would
        // make `/api/words` itself a 400.
        int? page,
        int? pageSize,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        // A learner with a thousand words must not receive a thousand rows
        // (Part 3 §37); a client asking for an absurd page size does not get
        // to decide otherwise.
        var pageIndex = Math.Max(0, page ?? 0);
        var size = pageSize is null or <= 0 ? 50 : Math.Min(pageSize.Value, 100);

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

        // Searching your own vocabulary (Part 2 §46) — over the word and its
        // meaning, because a learner looking for "بحث" is looking for the same
        // row as one typing "research". Parameterised by EF Core; the term
        // never becomes SQL text.
        var term = SearchTerm.Clean(q);
        if (term.Length > 0)
        {
            if (term.Length > 64)
            {
                return Problems.BadRequest(
                    "QUERY_TOO_LONG", "Search term is too long.");
            }

            query = query.Where(w =>
                EF.Functions.ILike(w.Text, $"%{term}%") ||
                w.Meaning.Contains(term));
        }

        // Counted before paging: the client shows "12 words", not "12 on this
        // page", and needs to know whether there is more to fetch.
        var total = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(w => w.AddedAt)
            .Skip(pageIndex * size)
            .Take(size)
            .ToListAsync(ct);

        return Results.Ok(new
        {
            items = items.Select(ToResponse).ToList(),
            total,
            page = pageIndex,
            pageSize = size,
            hasMore = (pageIndex + 1) * size < total,
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
