using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WordOs.Application.Abstractions;
using WordOs.Infrastructure.Ai;
using WordOs.Application.Lexicon;
using WordOs.Application.Words;
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
    /// <param name="SenseId">
    /// The lexicon sense being added — the ordinary path, and the only one that
    /// results in a <see cref="MeaningSource.Lexicon"/> meaning. Optional now
    /// only because the other two paths identify the word by its text instead
    /// (ADR-072, ADR-073); one of the three must still say which word.
    /// </param>
    /// <param name="CustomMeaning">
    /// The Arabic meaning the learner typed for themselves (ADR-072). The word
    /// need not be one the lexicon holds (ADR-075) — but it must still be a
    /// word: the checker is asked, and a string it does not recognise as
    /// English is refused with the spelling it thinks was meant.
    /// </param>
    /// <param name="AcceptAnyway">
    /// Save the written meaning even though the checker rejected it (ADR-074).
    /// Set only after the learner has been shown what the checker said and has
    /// chosen to keep their own wording — the check still ran, and the
    /// disagreement is recorded.
    /// </param>
    /// <param name="FromSessionId">
    /// Add the word with the meaning it carries <i>in this session's passage</i>
    /// (ADR-073). The meaning is read from the glossary this server stored when
    /// it generated the passage, never from the request: what the client sends
    /// is which session and which word, and the server answers what that word
    /// meant there.
    /// </param>
    public sealed record AddWordRequest(
        [property: MaxLength(64)] string? SenseId,
        [property: MaxLength(128)] string? Text,
        [property: MaxLength(256)] string? Meaning,
        [property: MaxLength(256)] string? CustomMeaning = null,
        Guid? FromSessionId = null,
        bool AcceptAnyway = false);

    /// <summary>
    /// One row of a stored passage glossary, as <c>SessionEndpoints</c> wrote it.
    /// </summary>
    /// <remarks>
    /// Declared again rather than shared: this is a persisted JSON shape, and
    /// two endpoints reading the same stored bytes is exactly the case where a
    /// shared record turns a display tweak in one into a silent parse change in
    /// the other.
    /// </remarks>
    private sealed record StoredGlossaryRow(
        string? Word,
        string? Meaning,
        string? PartOfSpeech);

    private static readonly JsonSerializerOptions GlossaryJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    /// <summary>The lexicon facts a word needs, however its meaning was chosen.</summary>
    private sealed record LexiconFacts(
        string SenseId,
        string Text,
        string MeaningAr,
        string DefinitionEn,
        string PartOfSpeech,
        CefrLevel? CefrLevel);

    /// <param name="Form">
    /// Which form of the word this entry is — <c>past</c>, <c>pastParticiple</c>,
    /// <c>ing</c>, <c>plural</c> — or null when it is the word itself
    /// (ADR-056).
    ///
    /// <para>Sent as a stable key, not a sentence: the client says it in the
    /// learner's language (ADR-035). Derived from the sense id the entry was
    /// built from rather than stored twice (ADR-045).</para>
    /// </param>
    public sealed record WordResponse(
        Guid Id,
        string SenseId,
        string Text,
        string Meaning,
        string DefinitionEn,
        string PartOfSpeech,
        string? Form,
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
        group.MapDelete("/{id:guid}", DeleteAsync);

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
    /// Three ways in, and they differ only in <b>where the Arabic meaning comes
    /// from</b>. The word itself is resolved against the lexicon every time: the
    /// meaning is what the learner may choose, not the spelling, because a CEFR
    /// level, a part of speech and a definition are needed to generate content
    /// about it and nothing can supply those for a string nobody recognises.
    ///
    /// <list type="number">
    /// <item><b>A lexicon sense</b> (<c>senseId</c>) — the original path,
    /// unchanged. The body is a <i>lookup key</i>: the sense is re-resolved and
    /// the stored row is copied, so a forged level, definition or meaning is
    /// discarded (ADR-012, docs/07-SECURITY.md §5).</item>
    ///
    /// <item><b>A meaning the learner typed</b> (<c>customMeaning</c>) — ADR-072.
    /// The one thing the client may genuinely author, and it is theirs to
    /// author: the Arabic glosses are a machine join of three datasets and
    /// <c>sell</c> offers "أَقْنَعَ بِـ" before "باع". A learner who knows what they
    /// meant should not have to accept the join's guess.</item>
    ///
    /// <item><b>The meaning from a passage</b> (<c>fromSessionId</c>) — ADR-073.
    /// The client says which session and which word; the meaning is read from
    /// the glossary <i>this server stored</i> when it generated the passage.
    /// Nothing about the meaning is taken from the request, which is the point:
    /// the client used to fetch the senses and pick the closest one itself, and
    /// picked wrong often enough that words were filed under meanings the
    /// learner had never seen.</item>
    /// </list>
    /// </remarks>
    private static async Task<IResult> AddAsync(
        AddWordRequest request,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        WordOsConfiguration config,
        IAiContentService ai,
        HttpContext http,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(request, out var errors))
            return Results.ValidationProblem(errors);

        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        var text = SearchTerm.Clean(request.Text);
        var custom = SearchTerm.Clean(request.CustomMeaning);

        string senseId;
        string storedText;
        string storedMeaning;
        string definitionEn;
        string partOfSpeech;
        CefrLevel? level;
        MeaningSource source;
        MeaningCheckResult? check = null;

        if (request.FromSessionId is { } sessionId)
        {
            // ── The word as this passage used it (ADR-073) ──────────────────
            if (text.Length == 0)
                return Problems.BadRequest("BAD_WORD", "Provide a word.");

            // Scoped to the caller: another learner's session is not
            // addressable, and answers 404 rather than 403 so the id itself
            // reveals nothing (docs/07-SECURITY.md §4).
            var session = await db.SkillSessions.FirstOrDefaultAsync(
                s => s.Id == sessionId && s.UserId == userId, ct);

            if (session?.GlossaryJson is null)
            {
                return Problems.NotFound(
                    "SESSION_NOT_FOUND", "That passage is no longer available.");
            }

            // Case-insensitive, and every field treated as nullable despite the
            // record saying otherwise. This JSON was written by an older build
            // of this service and sits in the database until the session ages
            // out; `Deserialize` will happily hand back a row of nulls for a
            // shape it does not recognise, and the `.Trim()` below would then be
            // a 500 rather than a miss.
            var glossary =
                JsonSerializer.Deserialize<List<StoredGlossaryRow>>(
                    session.GlossaryJson, GlossaryJsonOptions) ?? [];

            var row = glossary.FirstOrDefault(g =>
                string.Equals(g.Word?.Trim(), text, StringComparison.OrdinalIgnoreCase));

            // The generator glosses every content word, but not every word: a
            // name or a number has no entry. Refusing here is what sends the
            // client back to the ordinary dictionary sheet, which is the right
            // answer for a word this passage never explained.
            if (row is null || string.IsNullOrWhiteSpace(row.Meaning))
            {
                return Problems.NotFound(
                    "NOT_IN_PASSAGE",
                    "This passage has no meaning recorded for that word.");
            }

            var facts = await ResolveLexiconAsync(text, db, ct);
            if (facts is null) return LexiconMiss(text);

            storedText = facts.Text;
            storedMeaning = row.Meaning!.Trim();
            definitionEn = facts.DefinitionEn;
            // The glossary knows the word's role *in this sentence*, which the
            // lexicon's commonest sense does not: "will" is an auxiliary here
            // and a noun three entries up.
            partOfSpeech = string.IsNullOrWhiteSpace(row.PartOfSpeech)
                ? facts.PartOfSpeech
                : row.PartOfSpeech.Trim();
            level = facts.CefrLevel;
            source = MeaningSource.Passage;
            senseId = CustomSenses.For(storedText, storedMeaning);
        }
        else if (custom.Length > 0)
        {
            // ── A meaning the learner wrote (ADR-072) ───────────────────────
            if (text.Length == 0)
                return Problems.BadRequest("BAD_WORD", "Provide a word.");

            if (!ArabicText.ContainsArabic(custom))
            {
                // The whole pipeline asks "what does this mean?" and marks the
                // answer against this string. A meaning written in English
                // would make every Reading and Listening question unanswerable
                // by its own key, so it is refused here rather than discovered
                // two days later in a session.
                return Problems.BadRequest(
                    "MEANING_NOT_ARABIC",
                    "Write the meaning in Arabic.");
            }

            // Null is no longer a refusal (ADR-075). The lexicon is a machine
            // join of WordNet and a CEFR list and it has holes — and a word it
            // has never heard of is precisely the word a learner is most likely
            // to want to write their own meaning for. What the lexicon used to
            // settle — is this an English word, what kind of word, how hard —
            // the checker is asked instead.
            var facts = await ResolveLexiconAsync(text, db, ct);

            // The one place in this service where a model is asked about
            // something the learner typed *before* it is stored (ADR-074).
            // Everything downstream marks answers against this string, so a
            // meaning that is wrong or misspelled is not a cosmetic problem —
            // it is five sessions asking the wrong question.
            MeaningCheck verdict;
            try
            {
                verdict = await ai.CheckMeaningAsync(
                    new MeaningCheckRequest(
                        facts?.Text ?? text,
                        // Nothing to judge against for an unknown word, and
                        // that is the signal: the checker answers from its own
                        // knowledge instead of from a list this service does
                        // not have.
                        facts is null ? [] : await SensesOfAsync(facts.Text, db, ct),
                        facts?.PartOfSpeech ?? string.Empty,
                        custom,
                        LearnerLanguage.From(http.Request),
                        KnownWord: facts is not null),
                    ct);
            }
            catch (Exception e) when (e is AiServiceException
                                          or HttpRequestException
                                          or TaskCanceledException)
            {
                // No fallback exists for this question, so the honest answer is
                // "not now" — the learner's session is untouched and the word
                // is unsaved (ADR-074). 503 rather than 500: it clears.
                return Problems.Unavailable(
                    "MEANING_CHECK_UNAVAILABLE",
                    "Could not check that meaning just now. Try again in a moment.");
            }

            // Asked first, and not overridable (ADR-075). "What does this mean"
            // is a question about a word, so there is nothing to ask about a
            // string that is not one — and unlike a contested meaning, nobody
            // is served by `asdfgh` entering a pipeline that will spend five
            // sessions on it. The learner is told the spelling we think they
            // meant and can add that instead, which is a smaller, better ask
            // than "are you sure?".
            if (facts is null && !verdict.WordRecognized)
            {
                return Results.Json(
                    new
                    {
                        error = new
                        {
                            code = "WORD_NOT_RECOGNIZED",
                            message = verdict.Note,
                            correctedWord = verdict.CorrectedWord,
                        },
                    },
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (!verdict.Matches && !request.AcceptAnyway)
            {
                // Not an error — a second question. The learner is shown what
                // the checker said and what it would accept, and may still
                // insist, which comes back with `acceptAnyway`.
                return Results.Json(
                    new
                    {
                        error = new
                        {
                            code = "MEANING_REJECTED",
                            message = verdict.Note,
                            suggestions = verdict.Suggestions,
                            corrected = verdict.Corrected,
                        },
                    },
                    statusCode: StatusCodes.Status409Conflict);
            }

            // A meaning the checker accepted but would spell differently is the
            // same refusal with one suggestion: silently storing the correction
            // would put words in the learner's mouth, and silently storing the
            // misspelling would teach it.
            if (verdict.Matches
                && verdict.Corrected is { Length: > 0 } corrected
                && !string.Equals(corrected, custom, StringComparison.Ordinal)
                && !request.AcceptAnyway)
            {
                return Results.Json(
                    new
                    {
                        error = new
                        {
                            code = "MEANING_REJECTED",
                            message = verdict.Note,
                            suggestions = new[] { corrected },
                            corrected,
                        },
                    },
                    statusCode: StatusCodes.Status409Conflict);
            }

            storedText = facts?.Text ?? text;
            storedMeaning = custom;
            // For a word the lexicon has, its own facts — they come from
            // WordNet and a published CEFR list and are worth more than a
            // model's recollection of them. For one it does not, the checker's,
            // filtered: the model reports and this decides (rule R2), so a part
            // of speech outside the set this app can label and a band outside
            // the ladder are dropped rather than stored.
            definitionEn = facts?.DefinitionEn ?? verdict.DefinitionEn ?? string.Empty;
            partOfSpeech = facts?.PartOfSpeech ?? KnownPartOfSpeech(verdict.PartOfSpeech);
            level = facts?.CefrLevel ?? CefrLevelExtensions.TryFromWire(
                verdict.Level?.Trim().ToUpperInvariant());
            source = MeaningSource.Learner;
            // Recorded, not just acted on: "the model said no and the learner
            // said yes anyway" is the fact that explains a word failing
            // Spelling a fortnight later.
            check = verdict.Matches
                ? MeaningCheckResult.Approved
                : MeaningCheckResult.Overridden;
            senseId = CustomSenses.For(storedText, storedMeaning);
        }
        else
        {
            // ── A lexicon sense: unchanged (ADR-012) ────────────────────────
            if (string.IsNullOrWhiteSpace(request.SenseId))
            {
                return Problems.BadRequest(
                    "BAD_REQUEST",
                    "Choose a meaning or write one.");
            }

            // A client must not be able to mint a `custom:` id and post it as
            // though the lexicon had issued it — that is the one way a forged
            // meaning could reach the database through this path.
            if (CustomSenses.IsCustom(request.SenseId))
            {
                return Problems.BadRequest(
                    "BAD_SENSE", "That is not a dictionary meaning.");
            }

            var entry = await db.LexiconEntries
                .FirstOrDefaultAsync(l => l.SenseId == request.SenseId, ct);

            if (entry is null)
            {
                return Problems.NotFound(
                    "WORD_NOT_FOUND",
                    "That word and meaning are not in the dictionary.");
            }

            senseId = entry.SenseId;
            storedText = entry.Text;
            storedMeaning = entry.MeaningAr;
            definitionEn = entry.DefinitionEn;
            partOfSpeech = entry.PartOfSpeech;
            level = entry.CefrLevel;
            source = MeaningSource.Lexicon;
        }

        // Duplicate identity is the sense, scoped to this learner. Deleted rows
        // are invisible here — the query filter drops them (ADR-071) — so a word
        // the learner removed and wants back is added afresh rather than refused.
        var duplicate = await db.Words.AnyAsync(
            w => w.UserId == userId && w.SenseId == senseId, ct);

        // A written meaning can land on the same text and gloss as a lexicon
        // sense — type "باع" for `sell` and it reads identically to
        // `sell%2:40:00::`. Those are different sense ids, so the index would
        // let both in and the learner would own the same card twice. Checked
        // here because it is the only place that can see both.
        if (!duplicate && source != MeaningSource.Lexicon)
        {
            duplicate = await db.Words.AnyAsync(
                w => w.UserId == userId
                     && w.Meaning == storedMeaning
                     && EF.Functions.ILike(w.Text, storedText), ct);
        }

        if (duplicate)
        {
            return Problems.Conflict(
                "WORD_ALREADY_ADDED",
                "You have already added this word with this meaning.");
        }

        var word = Word.Add(
            userId.Value,
            senseId,
            storedText,
            storedMeaning,
            definitionEn,
            partOfSpeech,
            // A sense with no CEFR band still enters the pipeline; B1 is the
            // neutral default for content generation, and the level engine
            // corrects it from real performance.
            level ?? CefrLevel.B1,
            config,
            clock.GetUtcNow(),
            source,
            check);

        db.Words.Add(word);
        db.ActivityEvents.Add(ActivityEvent.Record(
            userId.Value, ActivityType.WordAdded, clock.GetUtcNow(),
            entityId: word.Id));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        // Two taps on "add" arriving together: the duplicate check above passed
        // for both, and the index refused the second. It is the same answer,
        // reached a different way.
        catch (DbUpdateException e)
            when (UniqueViolation.On(e, "IX_words_UserId_SenseId"))
        {
            return Problems.Conflict(
                "WORD_ALREADY_ADDED",
                "You have already added this word with this meaning.");
        }

        return Results.Ok(ToResponse(word));
    }

    /// <summary>
    /// A part of speech this app can actually say, or nothing (ADR-075).
    /// </summary>
    /// <remarks>
    /// The checker is asked for one of a fixed list and generally returns one,
    /// but "generally" is not a contract and the field is stored rather than
    /// displayed once and forgotten: it decides how Speaking invites the word
    /// and how Spelling clues it. Anything unrecognised becomes empty, which
    /// every reader already handles — a lexicon row can lack one too.
    /// </remarks>
    private static string KnownPartOfSpeech(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "noun" or "n" => "noun",
            "verb" or "v" => "verb",
            "adjective" or "adj" or "a" or "s" => "adjective",
            "adverb" or "adv" or "r" => "adverb",
            "pronoun" or "pron" => "pronoun",
            "preposition" or "prep" => "preposition",
            "conjunction" or "conj" => "conjunction",
            "determiner" or "det" => "determiner",
            "interjection" or "intj" => "interjection",
            "numeral" or "num" => "numeral",
            _ => string.Empty,
        };

    private static IResult LexiconMiss(string text) =>
        Problems.NotFound(
            "WORD_NOT_FOUND",
            $"'{text}' is not in the dictionary.");

    /// <summary>
    /// What the lexicon knows about a word, whatever meaning is being attached
    /// to it.
    /// </summary>
    /// <remarks>
    /// The level, the part of speech and the English definition are not the
    /// learner's to write: they drive which passages a word appears in, how
    /// Spelling clues it, and whether the level engine reads a failure as
    /// evidence. So a hand-written meaning is hung on a real entry whenever
    /// there is one to hang it on.
    ///
    /// <para>Returns null for a word the lexicon does not hold, which is no
    /// longer the end of the matter (ADR-075): the caller asks the checker for
    /// the same three facts instead. What used to keep <c>asdfgh</c> out of the
    /// pipeline was this returning null; what keeps it out now is the checker
    /// being asked whether it is a word.</para>
    ///
    /// <para>Resolved through <see cref="SurfaceForms"/> for the same reason
    /// <c>/define</c> is: the learner types the word as they met it. The
    /// commonest sense wins — <c>FrequencyRank</c> first — because it is only
    /// being asked for the word's grammar, not for its meaning.</para>
    /// </remarks>
    private static async Task<LexiconFacts?> ResolveLexiconAsync(
        string text,
        WordOsDbContext db,
        CancellationToken ct)
    {
        foreach (var candidate in SurfaceForms.CandidatesFor(text))
        {
            var entry = await db.LexiconEntries
                .Where(l => l.TextNormalized == candidate)
                // A row that carries a CEFR band is worth more here than a
                // marginally commoner one that does not: the band is half of
                // what this lookup exists to find.
                .OrderBy(l => l.CefrLevel == null ? 1 : 0)
                .ThenBy(l => l.FrequencyRank)
                .ThenBy(l => l.SenseId)
                .FirstOrDefaultAsync(ct);

            if (entry is not null)
            {
                return new LexiconFacts(
                    entry.SenseId, entry.Text, entry.MeaningAr,
                    entry.DefinitionEn, entry.PartOfSpeech, entry.CefrLevel);
            }
        }

        return null;
    }

    /// <summary>
    /// The English senses of a word, spread across its parts of speech.
    /// </summary>
    /// <remarks>
    /// What the meaning checker is judged against (ADR-074), and the spread is
    /// the whole point.
    ///
    /// <para>Taking the commonest eight looked obviously right and was wrong,
    /// which is why it is worth the words: <c>book</c> has nine noun senses and
    /// three verb senses, and the nine nouns rank higher — so the checker was
    /// handed eight nouns, never learned that <c>book</c> is also a verb, and
    /// rejected a learner who wrote <c>يحجز</c>. That is precisely the meaning
    /// ADR-072 exists to let them write, refused by the feature meant to help
    /// them.</para>
    ///
    /// <para>So: a few per part of speech, commonest first within each. A
    /// learner who means the rare sense of a common word is still served,
    /// because the model is told the word has that part of speech at all —
    /// which is the thing it cannot guess and the thing it was missing.</para>
    /// </remarks>
    private static async Task<List<string>> SensesOfAsync(
        string text,
        WordOsDbContext db,
        CancellationToken ct)
    {
        var normalized = text.Trim().ToLowerInvariant();

        var rows = await db.LexiconEntries
            .Where(l => l.TextNormalized == normalized
                        && l.DefinitionEn != null
                        && l.DefinitionEn != "")
            .OrderBy(l => l.FrequencyRank)
            .ThenBy(l => l.SenseId)
            .Select(l => new { l.PartOfSpeech, l.DefinitionEn })
            // Bounded before grouping: a word with a long tail should not pull
            // its whole entry into memory to throw most of it away.
            .Take(40)
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.PartOfSpeech)
            .SelectMany(g => g.Take(3))
            .Select(r => $"{r.PartOfSpeech}: {r.DefinitionEn}")
            .Distinct()
            .Take(10)
            .ToList();
    }

    /// <summary>
    /// Removes a word from the learner's vocabulary (ADR-071).
    /// </summary>
    /// <remarks>
    /// A state change rather than a <c>DELETE</c>, for reasons set out on
    /// <see cref="Word.Delete"/>: the learner sees it gone everywhere, the
    /// evidence stays for the Owner.
    ///
    /// <para>An open session that included this word is deliberately left
    /// alone. It finishes normally and simply applies nothing to the word —
    /// completion already handles a word that has moved on since the session
    /// opened (ADR-043), and a deleted word is that case. Tearing the session
    /// down instead would lose the answers the learner had already given on the
    /// <i>other</i> words in it.</para>
    ///
    /// <para>Idempotent: deleting an already-deleted word answers 204 rather
    /// than 404, because the second call is what a retried request looks like
    /// and the learner's intent is satisfied either way.</para>
    /// </remarks>
    private static async Task<IResult> DeleteAsync(
        Guid id,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        // `IgnoreQueryFilters` so that a repeat of this very request finds the
        // row it already deleted instead of 404-ing on it. Still scoped to the
        // caller's own id: another learner's word is not addressable.
        var word = await db.Words
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Id == id && w.UserId == userId, ct);

        if (word is null)
            return Problems.NotFound("WORD_NOT_FOUND", "Word not found.");

        if (word.State != WordState.Deleted)
        {
            word.Delete(clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
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
        var paging = Page.From(page, pageSize, maxSize: 100);

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
            .Skip(paging.Offset)
            .Take(paging.Size)
            .ToListAsync(ct);

        return Results.Ok(new
        {
            items = items.Select(ToResponse).ToList(),
            total,
            page = paging.Index,
            pageSize = paging.Size,
            hasMore = paging.HasMore(total),
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
            WordForms.FormKey(w),
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
