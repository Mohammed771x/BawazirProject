using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;
using WordOs.Application.Abstractions;
using WordOs.Application.Sessions;
using WordOs.Application.Words;
using WordOs.Domain.Common;
using WordOs.Domain.Levels;
using WordOs.Domain.Sessions;
using WordOs.Domain.Users;
using WordOs.Domain.Words;
using WordOs.Infrastructure.Ai;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Endpoints;

/// <summary>
/// The five skill sessions.
/// </summary>
/// <remarks>
/// Gemini generates the <i>content</i> and reports <i>observations</i>. Every
/// decision that matters is made here:
///
/// <list type="bullet">
/// <item>whether an answer is correct — compared against the answer this
/// server issued, never against anything the client sent;</item>
/// <item>whether a word passed — first attempt only (§31);</item>
/// <item>what happens to the word afterwards — <see cref="Word.ApplySessionResult"/>
/// owns the state machine;</item>
/// <item>whether the level moves — <see cref="LevelEngine"/>.</item>
/// </list>
///
/// That separation is rule R2, and it is why an AI outage degrades content
/// quality without ever corrupting a learner's progress.
/// </remarks>
public static class SessionEndpoints
{
    public sealed record AnswerRequest(
        [property: Required] Guid ItemId,
        [property: Required, MaxLength(4000)] string Answer);

    public sealed record SpeakingTurnRequestDto(
        [property: Required, MaxLength(4000)] string Transcript);

    public static IEndpointRouteBuilder MapSessionEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sessions")
            .WithTags("Sessions")
            .RequireAuthorization();

        // The tight budget goes on the three routes that spend Gemini tokens.
        // Answering a multiple-choice question does not, and a fast learner
        // clearing a queue must not be throttled for it.
        group.MapPost("/{skill}/start", StartAsync)
            .RequireRateLimiting(RateLimitPolicies.Expensive);
        group.MapPost("/{id:guid}/answer", AnswerAsync);
        group.MapPost("/{id:guid}/writing", WritingAsync)
            .RequireRateLimiting(RateLimitPolicies.Expensive);
        group.MapPost("/{id:guid}/speaking/turn", SpeakingAsync)
            .RequireRateLimiting(RateLimitPolicies.Expensive);
        group.MapPost("/{id:guid}/warmup/answer", WarmupAnswerAsync);
        group.MapPost("/{id:guid}/level", ChangeLevelAsync)
            .RequireRateLimiting(RateLimitPolicies.Expensive);
        group.MapPost("/{id:guid}/complete", CompleteAsync);
        group.MapPost("/{id:guid}/abandon", AbandonAsync);
        group.MapGet("/{id:guid}", ResumeAsync);

        return app;
    }

    public sealed record ChangeLevelRequest(
        [property: Required, MaxLength(8)] string Level);

    /// <summary>One rung of the spelling hint ladder, on the wire.</summary>
    private sealed record HintWire(string Kind, string Text);

    /// <summary>One glossary row on the wire.</summary>
    private sealed record GlossaryWire(
        string Word,
        string Meaning,
        string PartOfSpeech);

    /// <summary>
    /// Turns the learner's words into what the generator is told about them.
    /// </summary>
    /// <remarks>
    /// The passage may write a noun in the plural — but only when the plural is
    /// the word plus <c>s</c>, which is why the dictionary is asked whether a
    /// differently-spelled plural exists (ADR-047). One query per session: it
    /// is a fact about the lexicon, not about the learner.
    /// </remarks>
    private static async Task<List<AiTargetWord>> DescribeWordsAsync(
        WordOsDbContext db,
        IReadOnlyList<Word> words,
        CancellationToken ct)
    {
        var lemmas = words
            .Where(w => string.Equals(w.PartOfSpeech, "n", StringComparison.OrdinalIgnoreCase))
            .Select(w => w.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var irregular = lemmas.Count == 0
            ? []
            : await db.LexiconEntries
                .Where(l => lemmas.Contains(l.Lemma) && l.SourceFlags.Contains("form=pl"))
                .Select(l => l.Lemma)
                .Distinct()
                .ToListAsync(ct);

        var irregularSet = irregular.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return words
            .Select(w => new AiTargetWord(
                w.Text, w.Meaning, w.DefinitionEn, w.PartOfSpeech,
                Form: WordForms.FormOf(w),
                MayPluralise: WordForms.MayPluralise(w, irregularSet.Contains(w.Text))))
            .ToList();
    }

    /// <summary>
    /// The other forms of each word, so a near miss can be named (ADR-050).
    /// </summary>
    /// <remarks>
    /// A learner practising <c>went</c> who says "I go there every year" has
    /// not used the word — but they are one step away, and "try to use
    /// <c>went</c>" does not say which step. Naming it needs to know that
    /// <c>go</c>, <c>going</c> and <c>gone</c> are the same verb, which the
    /// lexicon already records: every form of a word carries the lemma it was
    /// built from (ADR-045).
    /// </remarks>
    private static async Task<Dictionary<string, List<string>>> SiblingFormsAsync(
        WordOsDbContext db,
        IReadOnlyList<Word> words,
        CancellationToken ct)
    {
        // Only inflected entries have a form to miss. A learner practising the
        // plain word is not corrected for saying the plain word.
        var inflected = words.Where(w => WordForms.FormOf(w) is not null).ToList();
        if (inflected.Count == 0) return [];

        var senseIds = inflected.Select(w => w.SenseId).Distinct().ToList();

        var lemmas = await db.LexiconEntries
            .Where(l => senseIds.Contains(l.SenseId))
            .Select(l => new { l.SenseId, l.Lemma })
            .ToListAsync(ct);

        var lemmaOf = lemmas.ToDictionary(x => x.SenseId, x => x.Lemma);
        var wanted = lemmaOf.Values.Distinct().ToList();
        if (wanted.Count == 0) return [];

        var family = await db.LexiconEntries
            .Where(l => wanted.Contains(l.Lemma))
            .Select(l => new { l.Lemma, l.Text })
            .Distinct()
            .ToListAsync(ct);

        var byLemma = family
            .GroupBy(x => x.Lemma, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.Text).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var word in inflected)
        {
            if (!lemmaOf.TryGetValue(word.SenseId, out var lemma)) continue;
            if (!byLemma.TryGetValue(lemma, out var forms)) continue;

            // The word itself is not one of its own near misses.
            var others = forms
                .Where(f => !string.Equals(f, word.Text, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (others.Count > 0) result[word.Text] = others;
        }

        return result;
    }

    /// <summary>
    /// Whether the learner said <i>this</i> word.
    /// </summary>
    /// <remarks>
    /// The word, or — for a noun whose plural is just an <c>s</c> — its plural.
    /// "How many books do you read?" answered with "books" is the learner using
    /// <c>book</c>, and asking them again for it is the tutor not listening
    /// (ADR-049).
    ///
    /// Nothing else counts. Another form of a verb is another vocabulary item:
    /// a learner practising <c>play</c> has not practised it by saying
    /// <c>played</c>, which is its own entry with its own pipeline (ADR-045).
    /// Neither has one practising <c>mouse</c> by saying <c>mice</c>. This used
    /// to be a prefix match, which accepted <c>booking</c> for <c>book</c> and
    /// <c>looked</c> for <c>look</c>.
    /// </remarks>
    private static bool SpokenIn(string transcript, string word, bool allowPlural)
    {
        var needle = word.Trim();
        if (needle.Length == 0) return false;

        // A phrase — "go back" — is not a token, and is matched whole.
        if (needle.Contains(' '))
            return transcript.Contains(needle, StringComparison.OrdinalIgnoreCase);

        foreach (var token in transcript.Split(
                     [' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '"', '\''],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token, needle, StringComparison.OrdinalIgnoreCase))
                return true;

            if (allowPlural && IsRegularPluralOf(needle, token)) return true;
        }

        return false;
    }

    /// <summary>Whether <paramref name="token"/> is the plain plural of a word.</summary>
    private static bool IsRegularPluralOf(string word, string token) =>
        string.Equals(token, word + "s", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(token, word + "es", StringComparison.OrdinalIgnoreCase) ||
        (word.EndsWith('y') && word.Length > 1 &&
         string.Equals(token, word[..^1] + "ies", StringComparison.OrdinalIgnoreCase));

    /// <summary>The same letters, however they were spaced.</summary>
    private static string Despaced(string text) =>
        new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>
    /// The words a session is about.
    /// </summary>
    /// <remarks>
    /// From the session's own record (ADR-039). Sessions created before that
    /// existed carry none, so their items answer instead — which is what the
    /// whole codebase used to do, and which silently returns nothing for a
    /// conversation, because a conversation has no items.
    /// </remarks>
    private static async Task<List<Word>> LoadSessionWordsAsync(
        WordOsDbContext db,
        SkillSession session,
        CancellationToken ct)
    {
        var ids = session.WordIds.ToList();

        if (ids.Count == 0)
        {
            ids = session.Items
                .Where(i => i.WordId is not null)
                .Select(i => i.WordId!.Value)
                .Distinct()
                .ToList();
        }

        return ids.Count == 0
            ? []
            : await db.Words
                .Include(w => w.Skills)
                .Where(w => ids.Contains(w.Id))
                .ToListAsync(ct);
    }

    private static string? GlossaryJson(GeneratedContent content) =>
        content.Glossary is null || content.Glossary.Count == 0
            ? null
            : JsonSerializer.Serialize(content.Glossary
                .Select(g => new GlossaryWire(g.Word, g.Meaning, g.PartOfSpeech))
                .ToList());

    public sealed record WarmupAnswerRequest(
        [property: Required] Guid WordId,
        [property: Required, MaxLength(256)] string Answer);

    /// <summary>
    /// Marks one warm-up answer. Records nothing.
    /// </summary>
    /// <remarks>
    /// The marking is here because the client must never hold the answer key —
    /// the same reason every other answer is marked here. What is different is
    /// that nothing is written: no attempt, no event, no level. A learner who
    /// misses a word meets it again at the end of the loop and keeps going
    /// until they have them all, and none of it reaches their record.
    ///
    /// It is a warm-up, not a test. Its only job is that nobody walks into a
    /// conversation about words they cannot recall.
    /// </remarks>
    private static async Task<IResult> WarmupAnswerAsync(
        Guid id,
        WarmupAnswerRequest request,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(request, out var errors))
            return Results.ValidationProblem(errors);

        var (session, error) = await LoadSessionAsync(id, principal, db, ct);
        if (error is not null) return error;

        if (session!.Skill != SkillType.Speaking)
        {
            return Problems.BadRequest(
                "NO_WARMUP", "Only a speaking session has a warm-up.");
        }

        // Scoped to the caller's own word, from the session that belongs to
        // them — a word id in a request is never trusted on its own.
        var word = await db.Words.FirstOrDefaultAsync(
            w => w.Id == request.WordId && w.UserId == session.UserId, ct);

        if (word is null)
            return Problems.NotFound("WORD_NOT_FOUND", "Word not found.");

        return Results.Ok(new
        {
            wordId = word.Id,
            isCorrect = string.Equals(
                request.Answer.Trim(), word.Meaning.Trim(), StringComparison.Ordinal),
            correctAnswer = word.Meaning,
        });
    }

    // ── Changing the level of a passage ──────────────────────────────────────

    /// <summary>
    /// Re-tells this session's passage at a different CEFR level.
    /// </summary>
    /// <remarks>
    /// The same story in different language, not a new one on a similar topic
    /// — a learner who says "this is too hard" has already invested in this
    /// text, and swapping the story reads as though the app ignored them.
    ///
    /// Only before the questions begin. Afterwards the items they cleared would
    /// be about sentences that no longer exist, and their answers would vanish.
    ///
    /// The choice sticks. It is the same setting the learner can change in
    /// Settings, so changing it here writes it there too — a preference that
    /// silently reverted next session would be worse than no control at all,
    /// and a learner who has told us twice that B2 is too hard should not have
    /// to keep saying it.
    ///
    /// What it does <b>not</b> touch is the validated level, which is earned
    /// from performance and is the only thing allowed to drive progression and
    /// archiving (rule R6). A level a learner sets by tapping is a preference,
    /// not evidence.
    /// </remarks>
    private static async Task<IResult> ChangeLevelAsync(
        Guid id,
        ChangeLevelRequest request,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        IAiContentService ai,
        WordOsConfiguration config,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(request, out var errors))
            return Results.ValidationProblem(errors);

        var (session, error) = await LoadSessionAsync(id, principal, db, ct);
        if (error is not null) return error;

        if (session!.Skill is SkillType.Spelling)
        {
            // Spelling carries no level of its own; its content follows the
            // Reading level (ADR-008).
            return Problems.BadRequest(
                "LEVEL_NOT_ADJUSTABLE",
                "This session has no level to change.");
        }

        // Speaking and Writing have no passage to re-tell: the level is an
        // input to what happens next — the tutor's reply, or the rewrite the
        // learner is shown — so it can change at any point and takes effect
        // immediately. Reading and Listening have a text in front of the
        // learner, so theirs locks once the questions begin.
        var producesNoPassage =
            session.Skill is SkillType.Speaking or SkillType.Writing;

        if (!producesNoPassage && !session.CanChangeLevel)
        {
            return Problems.Conflict(
                "SESSION_STARTED",
                "The level cannot change once the questions have begun.");
        }

        var level = CefrLevelExtensions.TryFromWire(request.Level);
        if (level is null)
            return Problems.BadRequest("INVALID_LEVEL", "Unknown level.");

        if (!producesNoPassage && string.IsNullOrWhiteSpace(session.ContentText))
            return Problems.Conflict("NO_CONTENT", "This session has no passage.");

        var wordIds = session.Items
            .Where(i => i.WordId is not null)
            .Select(i => i.WordId!.Value)
            .Distinct()
            .ToList();

        var words = await db.Words
            .Where(w => wordIds.Contains(w.Id))
            .ToListAsync(ct);

        // ── No passage: set the level and change nothing else ─────────────
        //
        // Speaking uses it for the next turn; Writing uses it for the rewrite
        // the learner is shown, which is the whole point of the control there
        // (ADR-038). Neither has content to regenerate, so nothing the learner
        // has already done is disturbed.
        if (producesNoPassage)
        {
            RecordLevelChoice(db, user: await db.Users
                .Include(u => u.SkillLevels)
                .FirstAsync(u => u.Id == session.UserId, ct),
                skill: session.Skill, level: level.Value, now: clock.GetUtcNow());

            session.SetLevel(level.Value);
            await db.SaveChangesAsync(ct);

            return Results.Ok(ToSessionResponse(
                session, await LoadSessionWordsAsync(db, session, ct)));
        }

        GeneratedContent content;
        try
        {
            content = await ai.RelevelContentAsync(new RelevelRequest(
                session.ContentText!,
                session.LevelUsed,
                level.Value,
                await DescribeWordsAsync(db, words, ct),
                config.ComprehensionQuestionCount), ct);
        }
        catch (Exception e) when (e is AiServiceException or HttpRequestException
                                      or TaskCanceledException)
        {
            // The passage they are reading stays exactly as it is. A fallback
            // re-telling would be worse than the text they asked to improve.
            return Problems.Unavailable(
                "RELEVEL_UNAVAILABLE",
                "The passage could not be rewritten just now. Try again.");
        }

        RecordLevelChoice(db, user: await db.Users
            .Include(u => u.SkillLevels)
            .FirstAsync(u => u.Id == session.UserId, ct),
            skill: session.Skill, level: level.Value, now: clock.GetUtcNow());

        session.ReplaceContent(
            level.Value, content.Text, GlossaryJson(content));

        SessionContentBuilder.BuildComprehensionItems(
            session, content, words,
            session.Skill == SkillType.Listening, Random.Shared);

        session.RecordAiCall(
            content.PromptVersion, content.Model, content.Tokens);

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToSessionResponse(session, words));
    }

    /// <summary>
    /// Writes the learner's chosen difficulty where Settings reads it.
    /// </summary>
    /// <remarks>
    /// The same field the Settings control writes, because it is the same
    /// decision: a preference that reverted next session would be worse than
    /// no control at all. It moves <b>only</b> the user-selected level; the
    /// validated one is earned from performance and is the only thing allowed
    /// to drive progression and archiving (rule R6).
    ///
    /// Spelling carries no band of its own (ADR-008) and is left alone.
    /// </remarks>
    private static void RecordLevelChoice(
        WordOsDbContext db,
        User user,
        SkillType skill,
        CefrLevel level,
        DateTimeOffset now)
    {
        var skillLevel = user.LevelFor(skill);
        if (!skillLevel.CarriesCefrLevel) return;

        var previous = skillLevel.UserSelectedLevel;
        skillLevel.SetUserSelectedLevel(level);

        db.LevelChanges.Add(LevelChangeRecord.Create(
            user.Id, skill, previous, level,
            LevelChangeType.UserManualChange, now,
            reason: "changed during a session"));
    }

    // ── Start ────────────────────────────────────────────────────────────────

    private static async Task<IResult> StartAsync(
        string skill,
        bool? practice,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        IAiContentService ai,
        WordOsConfiguration config,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        if (!Enum.TryParse<SkillType>(skill, ignoreCase: true, out var skillType))
            return Problems.BadRequest("INVALID_SKILL", "Unknown skill.");

        var now = clock.GetUtcNow();

        // Whether the *caller* asked for practice, which is not the same as
        // whether they will get it — that also depends on the skill, and is
        // settled further down. What is decided here is which open session, if
        // any, they should be handed back.
        var isPracticeRequest = practice == true;

        // An unfinished session for this skill is resumed, not replaced. The
        // hub reports it as `activeSessionId`; starting again would throw away
        // answers the learner has already given and spend a fresh AI call to
        // produce a different passage.
        var open = await db.SkillSessions
            .Include(s => s.Items)
            .FirstOrDefaultAsync(
                s => s.UserId == userId && s.Skill == skillType && !s.IsComplete,
                ct);

        if (open is not null)
        {
            var openWords = await LoadSessionWordsAsync(db, open, ct);

            // A session about no words is not a session. It can only happen to
            // one saved before a session recorded what it was for (ADR-039),
            // and resuming it hands the learner an empty screen for ever,
            // because the hub keeps offering the skill and the resume keeps
            // returning this. Dropping it costs nothing — the words never left
            // the queue — and the request continues as a fresh start.
            //
            // The same is true of a session that never finished being built:
            // the slot below is claimed before the content is generated, so an
            // AI refusal or a process restart in between leaves a row with no
            // passage, no conversation and no items. Nothing was shown to
            // anybody, so it is cleared exactly like the one above — but only
            // once it is old enough that nobody can still be building it.
            if ((openWords.Count == 0 && !open.IsPractice)
                || IsAbandonedMidBuild(open, now, config)
                || IsStalePractice(open, now, config))
            {
                db.SkillSessions.Remove(open);
                await db.SaveChangesAsync(ct);
                open = null;
            }
            // Young enough that somebody is still building it — almost always
            // this learner's own previous tap, a second or two ago. Handing
            // back the half-built row would be an empty screen, and deleting it
            // would break the request that is filling it, so the honest answer
            // is that it is on its way.
            else if (IsUnbuilt(open))
            {
                return Problems.Conflict(
                    "SESSION_STARTING",
                    "This session is still being prepared. Try again in a moment.");
            }
            // A practice session standing in the way of a request for real
            // words is not resumed — but it is not dropped here either. Whether
            // it should give way depends on there being something to give way
            // *to*, and that is not known until the due words are counted a few
            // lines below. See the second half of this decision there.
            else if (!(open.IsPractice && !isPracticeRequest))
            {
                return Results.Ok(ToSessionResponse(open, openWords));
            }
        }

        var user = await db.Users
            .Include(u => u.Interests)
            .Include(u => u.SkillLevels)
            .FirstAsync(u => u.Id == userId, ct);

        var level = user.LevelFor(skillType);

        // Which words are due is a server decision: it depends on the spaced
        // gap and each word's current skill (rule R1).
        var candidates = await db.Words
            .Where(w => w.UserId == userId && w.State == WordState.Learning
                        && w.CurrentSkill == skillType)
            .Include(w => w.Skills)
            .ToListAsync(ct);

        var due = candidates
            .Where(w => w.IsEligibleFor(skillType, now))
            .OrderBy(w => w.SkillState(skillType).AvailableAt ?? w.AddedAt)
            .ThenBy(w => w.AddedAt)
            // Capped by the learner's daily target for this skill.
            .Take(level.DailyTargetWords)
            .ToList();

        // Practice keeps a learner reading on a day when the pipeline is empty
        // (Part 2 §5). It is offered only for the two skills that make sense
        // without vocabulary — a speaking or spelling session with no words is
        // not an activity, it is an empty screen — and it is always the
        // learner's choice, never a silent substitution for what they asked for.
        var isPractice = isPracticeRequest
                         && skillType is SkillType.Reading or SkillType.Listening;

        // Nothing is due and this is not a practice request, so there is
        // nothing to start. Note the order: any practice session left open is
        // still untouched at this point, which is deliberate — a learner with
        // no words due who taps the skill must not lose the practice they were
        // half-way through as the price of being told there is nothing to do.
        if (due.Count == 0 && !isPractice)
        {
            return Problems.Conflict(
                "NO_WORDS_DUE", "No words are due for this skill yet.");
        }

        // The other half of the decision left open above. There are words due
        // and the learner asked for them, so the practice session holding this
        // skill gives way. It measures nothing, so nothing measurable is lost —
        // and the alternative is what used to happen: real words that could not
        // be started until the learner finished or abandoned a practice session
        // they may not even remember opening.
        //
        // The unique index means this delete and the insert below have to be
        // the same story; they are, because both are this one request.
        if (open is not null)
        {
            db.SkillSessions.Remove(open);
            await db.SaveChangesAsync(ct);
        }

        // Spelling carries no CEFR band of its own, so its content difficulty
        // follows Reading: whether an English definition is a usable clue is a
        // reading-comprehension question (ADR-008).
        var contentLevel = level.UserSelectedLevel
                           ?? user.LevelFor(SkillType.Reading).UserSelectedLevel
                           ?? CefrLevel.B1;

        var session = SkillSession.Start(
            userId.Value, skillType, contentLevel, now, isPractice);

        // What this session is for, recorded now rather than inferred later
        // from its items — a conversation has no items, so a resumed Speaking
        // session used to come back with no words at all (ADR-039).
        session.SetWords(due.Select(w => w.Id));

        // ── Claim the slot, before spending anything ─────────────────────────
        //
        // The row is written here rather than at the end, and that ordering is
        // the whole fix (ADR-063). The expensive part of this method is the AI
        // call below; saving afterwards would let four concurrent requests all
        // generate a passage and only then discover that three of them lose —
        // the money is spent by the time the index objects.
        //
        // Claiming first inverts it: the losers are turned away in a
        // millisecond, having spent nothing, and are handed the winner's
        // session — which is what they were asking for anyway.
        db.SkillSessions.Add(session);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (IsOpenSessionConflict(e))
        {
            // Somebody else claimed this skill between our look and our write.
            // Their session is the one that exists, so it is the one to return:
            // "resume rather than fork", finally true under concurrency.
            db.Entry(session).State = EntityState.Detached;

            var winner = await db.SkillSessions
                .Include(s => s.Items)
                .FirstOrDefaultAsync(
                    s => s.UserId == userId && s.Skill == skillType && !s.IsComplete,
                    ct);

            // Vanishingly unlikely — the winner completed or abandoned in the
            // microseconds since. Asking again is honest and costs nothing.
            if (winner is null)
            {
                return Problems.Conflict(
                    "SESSION_RACE", "Another session was starting. Try again.");
            }

            // Usually the winner is still generating: it claimed its row a
            // moment ago and the passage takes seconds. Returning it now hands
            // back a session with no content and no items, which is an empty
            // screen — measured, five of six simultaneous starts got exactly
            // that. The same answer the resume path gives applies here.
            if (IsUnbuilt(winner))
            {
                return Problems.Conflict(
                    "SESSION_STARTING",
                    "This session is still being prepared. Try again in a moment.");
            }

            return Results.Ok(ToSessionResponse(
                winner, await LoadSessionWordsAsync(db, winner, ct)));
        }

        var random = Random.Shared;

        // Active vocabulary to re-encounter, least-met first (rule R8). Loaded
        // for every skill: a passage can reuse them, and so can a conversation.
        var activeWords = await db.Words
            .Where(w => w.UserId == userId && w.State == WordState.Active)
            .OrderBy(w => w.ExposureCount)
            .ThenBy(w => w.LastReviewedAt ?? w.ActivatedAt)
            .Take(config.ActiveReuseWordsPerSession)
            .ToListAsync(ct);

        switch (skillType)
        {
            case SkillType.Reading:
            case SkillType.Listening:
            {
                var listening = skillType == SkillType.Listening;
                var content = await ai.GenerateContentAsync(new ContentRequest(
                    Level: contentLevel,
                    Interests: user.Interests.Select(i => i.Interest).ToList(),
                    Words: await DescribeWordsAsync(db, due, ct),
                    Listening: listening,
                    ComprehensionCount: config.ComprehensionQuestionCount,
                    ReuseWords: await DescribeWordsAsync(db, activeWords, ct)), ct);

                session.SetContent(
                    content.Text, content.PromptVersion, content.Model,
                    content.Tokens, content.FromFallback,
                    GlossaryJson(content));

                SessionContentBuilder.BuildComprehensionItems(
                    session, content, due, listening, random);

                await CreditExposureAsync(
                    db, session.Id, activeWords, content.Text, now, ct);
                break;
            }

            case SkillType.Writing:
                SessionContentBuilder.BuildWritingItems(session, due);
                break;

            case SkillType.Spelling:
            {
                // Synonyms come out of the lexicon we already have. A WordNet
                // gloss belongs to the synset, not the word, so every lemma
                // sharing a definition *is* a synonym — "bottom", "rear" and
                // "backside" carry one identical gloss between them. A B1 clue
                // uses one instead of a definition written above the learner's
                // level (Part 2 §39).
                var definitions = due.Select(w => w.DefinitionEn).ToList();
                var lemmas = await db.LexiconEntries
                    .Where(l => definitions.Contains(l.DefinitionEn))
                    .OrderBy(l => l.FrequencyRank)
                    .Select(l => new { l.DefinitionEn, l.PartOfSpeech, l.Text })
                    .ToListAsync(ct);

                var synonyms = due
                    .Select(w => (w.Id, Synonym: lemmas.FirstOrDefault(l =>
                        l.DefinitionEn == w.DefinitionEn &&
                        l.PartOfSpeech == w.PartOfSpeech &&
                        !string.Equals(l.Text, w.Text,
                            StringComparison.OrdinalIgnoreCase))?.Text))
                    .Where(x => x.Synonym is not null && x.Synonym.Length > 0)
                    .ToDictionary(x => x.Id, x => x.Synonym!);

                SessionContentBuilder.BuildSpellingItems(
                    session, due, contentLevel, level.SpellingSupportMode,
                    random, synonyms);
                break;
            }

            case SkillType.Speaking:
            {
                // A conversation, not a list of questions — it has no items.
                var opening = await ai.SpeakingTurnAsync(new SpeakingTurnRequest(
                    LearnerName: user.DisplayName,
                    Level: contentLevel,
                    RemainingWords: due.Select(w => w.Text).ToList(),
                    UsedWords: [],
                    Transcript: [],
                    Interests: user.Interests.Select(i => i.Interest).ToList()), ct);

                if (opening.FromFallback) session.MarkFallbackUsed();
                session.RecordAiCall(
                    opening.PromptVersion, opening.Model, opening.Tokens);
                session.SetTranscript(JsonSerializer.Serialize(new[]
                {
                    new TranscriptEntry(true, opening.Reply),
                }));

                await CreditExposureAsync(
                    db, session.Id, activeWords, opening.Reply, now, ct);
                break;
            }
        }

        // Already added and saved above, where the slot was claimed. What
        // follows is the second half of the same start: the content that was
        // just built, and the record that a session began.
        db.ActivityEvents.Add(ActivityEvent.Record(
            userId.Value,
            isPractice ? ActivityType.PracticeStarted : ActivityType.SessionStarted,
            now, skillType, session.Id));

        foreach (var word in due) word.RecordSkillStarted(skillType, now);

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToSessionResponse(session, due));
    }

    // ── Answer (comprehension, target word, spelling) ────────────────────────

    private static async Task<IResult> AnswerAsync(
        Guid id,
        AnswerRequest request,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        WordOsConfiguration config,
        CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(request, out var errors))
            return Results.ValidationProblem(errors);

        var (session, error) = await LoadSessionAsync(id, principal, db, ct);
        if (error is not null) return error;

        var item = session!.Items.FirstOrDefault(i => i.Id == request.ItemId);
        if (item is null)
            return Problems.NotFound("ITEM_NOT_FOUND", "Question not found.");

        // A retry after a dropped connection, or a client out of step.
        // Rejecting keeps the attempt counters honest.
        if (session.CurrentItemId != item.Id)
        {
            return Problems.Conflict(
                "ITEM_NOT_CURRENT", "That question is no longer the active one.");
        }

        if (item.Type is SessionItemType.WritingTask or SessionItemType.SpeakingTurn)
            return Problems.BadRequest("WRONG_ITEM_TYPE", "Use the writing endpoint.");

        // Correctness is decided against the answer this server issued.
        //
        // Spelling ignores how the spaces fell: "alarm clock" and "alarmclock"
        // are the same spelling of the same word, and the exercise is the
        // letters. A learner who has every letter right should not be failed by
        // a gap — especially since a space is a tile they may simply have
        // missed on a small screen (ADR-042).
        var isCorrect = item.Type == SessionItemType.SpellingTask
            ? string.Equals(
                Despaced(request.Answer), Despaced(item.CorrectAnswer),
                StringComparison.OrdinalIgnoreCase)
            : string.Equals(request.Answer, item.CorrectAnswer, StringComparison.Ordinal);

        item.SetAnswer(request.Answer);
        var requeued = session.RecordAttempt(
            item, isCorrect, config, session.ClearedCount);

        await db.SaveChangesAsync(ct);

        var word = item.WordId is null
            ? null
            : await db.Words.FirstOrDefaultAsync(w => w.Id == item.WordId, ct);

        return Results.Ok(new
        {
            itemId = item.Id,
            isCorrect,
            correctAnswer = item.CorrectAnswer,
            wordId = item.WordId,
            // Shown after every attempt, right or wrong — the point is to leave
            // the item understanding the word, not merely scored (§28).
            explanation = word is null
                ? null
                : $"\"{word.Text}\" means {word.Meaning}. {word.DefinitionEn}",
            requeued,
            attemptNumber = item.Attempts,
            progress = Progress(session),
        });
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    private static async Task<IResult> WritingAsync(
        Guid id,
        AnswerRequest request,
        ClaimsPrincipal principal,
        HttpRequest http,
        WordOsDbContext db,
        IAiContentService ai,
        WordOsConfiguration config,
        CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(request, out var errors))
            return Results.ValidationProblem(errors);

        var (session, error) = await LoadSessionAsync(id, principal, db, ct);
        if (error is not null) return error;

        var item = session!.Items.FirstOrDefault(i => i.Id == request.ItemId);
        if (item is null || item.Type != SessionItemType.WritingTask)
            return Problems.NotFound("ITEM_NOT_FOUND", "Task not found.");

        if (session.CurrentItemId != item.Id)
        {
            return Problems.Conflict(
                "ITEM_NOT_CURRENT", "That task is no longer the active one.");
        }

        var sentence = request.Answer.Trim();
        if (sentence.Length == 0)
            return Problems.BadRequest("EMPTY_ANSWER", "Write a sentence first.");

        var word = await db.Words.FirstAsync(w => w.Id == item.WordId, ct);

        var observation = await ai.EvaluateWritingAsync(new WritingEvaluationRequest(
            word.Text, word.Meaning, word.DefinitionEn,
            session.LevelUsed, sentence,
            // The feedback is the app talking to the learner, so it is written
            // in the language they read the app in (ADR-035).
            LearnerLanguage.From(http)), ct);

        if (observation.FromFallback) session.MarkFallbackUsed();
        session.RecordAiCall(
            observation.PromptVersion, observation.Model, observation.Tokens);

        // ── The rule, applied here and not by the AI ─────────────────────────
        //
        // MVP Core §32: a small grammar slip must not fail correct usage. The
        // model reports `grammar_note`; this line decides what it costs. Living
        // in C# means a prompt edit cannot silently change what passing means.
        var passed = observation.UsedWord && observation.Understandable;

        item.SetAnswer(sentence);
        var requeued = session.RecordAttempt(
            item, passed, config, session.ClearedCount);

        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            itemId = item.Id,
            passed,
            observation.UsedWord,
            observation.MeaningCorrect,
            observation.UsageCorrect,
            observation.Understandable,
            observation.GrammarNote,
            observation.Feedback,
            observation.Suggestion,
            requeued,
            attemptNumber = item.Attempts,
            progress = Progress(session),
        });
    }

    // ── Speaking ─────────────────────────────────────────────────────────────

    private static async Task<IResult> SpeakingAsync(
        Guid id,
        SpeakingTurnRequestDto request,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        IAiContentService ai,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!MiniValidator.TryValidate(request, out var errors))
            return Results.ValidationProblem(errors);

        var (session, error) = await LoadSessionAsync(id, principal, db, ct);
        if (error is not null) return error;

        if (session!.Skill != SkillType.Speaking)
            return Problems.BadRequest("WRONG_SKILL", "This is not a speaking session.");

        var transcript = request.Transcript.Trim();
        if (transcript.Length == 0)
            return Problems.BadRequest("EMPTY_TURN", "Say something before sending.");

        var user = await db.Users
            .Include(u => u.Interests)
            .FirstAsync(u => u.Id == session.UserId, ct);
        // The words *this session* was opened for — its own record, never
        // "whatever sits at Speaking right now" (ADR-039).
        //
        // Reading the queue here was a real defect: a session about two words
        // held a conversation about twelve, because every word still parked at
        // Speaking joined it — words already passed, and words the learner
        // added while the conversation was open, which ADR-048 says must wait
        // for the next one. The learner saw a session announce two words and
        // then ask about a dozen, and the same word twice.
        var words = await LoadSessionWordsAsync(db, session!, ct);

        var history = JsonSerializer.Deserialize<List<TranscriptEntry>>(
            session.TranscriptJson ?? "[]") ?? [];
        history.Add(new TranscriptEntry(false, transcript));

        // Which words the learner has genuinely used, accumulated across turns.
        //
        // Searching the transcript for the word is not the same question: "can
        // you change the topic so I can use hook" contains it and uses nothing,
        // and counting that made the tutor drop the word for the rest of the
        // conversation (ADR-040). What is kept is the verdict from each turn,
        // which the AI judges and this endpoint verifies.
        var alreadyUsed = session.UsedWords.ToList();

        var remaining = words
            .Where(w => !alreadyUsed.Contains(w.Text, StringComparer.OrdinalIgnoreCase))
            .Select(w => w.Text)
            .ToList();

        // What each remaining word *is* — the same shape fact Reading uses
        // before it writes a sentence (ADR-047). A tutor asking for `went`
        // without knowing it is the past tense of `go` writes "do you go there
        // often?", which cannot be answered with the word being practised.
        var shapes = await DescribeWordsAsync(db, words, ct);
        var mayPluralise = shapes
            .GroupBy(w => w.Text, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Any(w => w.MayPluralise),
                StringComparer.OrdinalIgnoreCase);

        // Words they reached for and got the form wrong (ADR-050). Textual, so
        // it is settled before the tutor writes — the reply can then name the
        // step they missed in the same breath instead of a turn later.
        var siblings = await SiblingFormsAsync(db, words, ct);
        var reminders = new List<SpeakingFormReminder>();

        foreach (var word in words.Where(w => remaining.Contains(
                     w.Text, StringComparer.OrdinalIgnoreCase)))
        {
            if (SpokenIn(transcript, word.Text,
                    allowPlural: mayPluralise.GetValueOrDefault(word.Text)))
                continue;

            if (!siblings.TryGetValue(word.Text, out var others)) continue;

            var said = others.FirstOrDefault(f =>
                SpokenIn(transcript, f, allowPlural: false));

            if (said is null) continue;

            reminders.Add(new SpeakingFormReminder(
                word.Text, WordForms.FormOf(word) ?? "form", said));
        }

        var turn = await ai.SpeakingTurnAsync(new SpeakingTurnRequest(
            user.DisplayName, session.LevelUsed, remaining, alreadyUsed,
            history.Select(h => new SpeakingTranscriptTurn(h.FromAi, h.Text))
                .ToList(),
            user.Interests.Select(i => i.Interest).ToList(),
            RemainingShapes: shapes
                .Where(w => remaining.Contains(w.Text, StringComparer.OrdinalIgnoreCase))
                .ToList(),
            FormReminders: reminders), ct);

        if (turn.FromFallback) session.MarkFallbackUsed();
        session.RecordAiCall(turn.PromptVersion, turn.Model, turn.Tokens);

        // Which words the learner just used, decided from what they said.
        //
        // The transcript cannot be wrong about whether a word is in it, so it
        // settles that; the model is asked only what the text cannot say —
        // whether a word that appears there was being *named* rather than used
        // ("let me use hook in a sentence"). Asking the model the whole
        // question instead meant a turn was sometimes not counted at all, and
        // the tutor asked for a word the learner had just said (ADR-048).
        // Whether the plural counts is the same question the passage asks
        // before it writes one (ADR-047), answered from the same place.
        //
        // Grouped by spelling, not keyed by it: a learner may hold two senses
        // of the same word — `book` the object and `book` the verb — and a
        // transcript cannot tell them apart. One spelling is one thing to say.
        var confirmed = words
            .Select(w => w.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(text => SpokenIn(transcript, text,
                allowPlural: mayPluralise.GetValueOrDefault(text)))
            .Where(text => !turn.WordsOnlyNamed.Contains(text, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (confirmed.Count > 0)
        {
            session.RecordWordsUsed(confirmed);
            alreadyUsed = session.UsedWords.ToList();
            remaining = words
                .Where(w => !alreadyUsed.Contains(w.Text, StringComparer.OrdinalIgnoreCase))
                .Select(w => w.Text)
                .ToList();

            // The reply was written before this turn was judged, so it is a
            // question about a word the learner has just finished — which is
            // how a conversation ended on "try to use the word loop" one line
            // after they used it. With the list now empty, the closing turn is
            // asked for properly: what they did well, and goodbye (ADR-042).
            //
            // One extra call, once, at the end of a conversation.
            if (remaining.Count == 0)
            {
                try
                {
                    var closing = await ai.SpeakingTurnAsync(new SpeakingTurnRequest(
                        user.DisplayName, session.LevelUsed, [], alreadyUsed,
                        history.Select(h => new SpeakingTranscriptTurn(h.FromAi, h.Text))
                            .ToList(),
                        user.Interests.Select(i => i.Interest).ToList()), ct);

                    turn = turn with { Reply = closing.Reply };
                }
                catch (Exception e) when (e is AiServiceException or HttpRequestException
                                              or TaskCanceledException)
                {
                    // The conversation is over either way; the learner keeps
                    // the reply they have rather than seeing an error at the
                    // moment they finished.
                }
            }
        }

        history.Add(new TranscriptEntry(true, turn.Reply));
        session.SetTranscript(JsonSerializer.Serialize(history));

        // A conversation is generated content too. Keyed by session, so a word
        // the AI keeps returning to across ten turns is still one exposure.
        var activeWords = await db.Words
            .Where(w => w.UserId == session.UserId && w.State == WordState.Active)
            .ToListAsync(ct);

        await CreditExposureAsync(
            db, session.Id, activeWords, turn.Reply, clock.GetUtcNow(), ct);

        // Enough turns for one per word plus room for follow-ups.
        var learnerTurns = history.Count(t => !t.FromAi);
        var isFinal = remaining.Count == 0 || learnerTurns >= words.Count + 3;

        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            aiMessage = turn.Reply,
            isFinal,
            wordsUsed = alreadyUsed,
            remaining,
        });
    }

    // ── Complete ─────────────────────────────────────────────────────────────

    private static async Task<IResult> CompleteAsync(
        Guid id,
        ClaimsPrincipal principal,
        HttpRequest http,
        WordOsDbContext db,
        IAiContentService ai,
        LevelEngine levels,
        WordOsConfiguration config,
        TimeProvider clock,
        CancellationToken ct)
    {
        var (session, error) = await LoadSessionAsync(id, principal, db, ct);
        if (error is not null) return error;

        var now = clock.GetUtcNow();

        // The words this session was for — its own record, not "whatever is at
        // this skill now", which drifts as soon as anything else moves
        // (ADR-039).
        var words = await LoadSessionWordsAsync(db, session!, ct);
        var wordIds = words.Select(w => w.Id).ToHashSet();

        // Speaking is judged once, here, on the whole conversation — not turn by
        // turn. A learner who fumbles a word early and uses it well later is
        // judged on the exchange as a whole, and per-turn evaluation would
        // multiply the cost of a session by however much they said.
        var spoken = new Dictionary<Guid, SpeakingWordObservation>();
        var speakingSummary = string.Empty;

        var transcript = JsonSerializer
            .Deserialize<List<TranscriptEntry>>(session!.TranscriptJson ?? "[]")
            ?? [];

        // Whether the learner said anything at all. It decides both whether
        // there is a conversation worth paying to evaluate and — below —
        // whether a Speaking session may pass judgement on its words.
        var learnerSpoke = transcript.Any(t => !t.FromAi);

        if (session.Skill == SkillType.Speaking)
        {
            var spokenWords = words.Where(w => wordIds.Contains(w.Id)).ToList();

            // Nothing to judge if the learner never said anything.
            if (spokenWords.Count > 0 && learnerSpoke)
            {
                var learner = await db.Users
                    .FirstAsync(u => u.Id == session.UserId, ct);

                var evaluation = await ai.EvaluateSpeakingAsync(
                    new SpeakingEvaluationRequest(
                        learner.DisplayName,
                        session.LevelUsed,
                        await DescribeWordsAsync(db, spokenWords, ct),
                        transcript
                            .Select(t => new SpeakingTranscriptTurn(t.FromAi, t.Text))
                            .ToList(),
                        LearnerLanguage.From(http)),
                    ct);

                if (evaluation.FromFallback) session.MarkFallbackUsed();
                session.RecordAiCall(
                    evaluation.PromptVersion, evaluation.Model, evaluation.Tokens);

                speakingSummary = evaluation.Summary;

                foreach (var word in spokenWords)
                {
                    var observed = evaluation.Words.FirstOrDefault(o =>
                        string.Equals(o.Word, word.Text,
                            StringComparison.OrdinalIgnoreCase));

                    if (observed is not null) spoken[word.Id] = observed;
                }
            }
        }

        var outcomes = new List<object>();
        var passedCount = 0;

        foreach (var word in words.Where(w => wordIds.Contains(w.Id)))
        {
            // The word may have moved on since this session was opened — the
            // learner finished the same word elsewhere, or the Owner brought a
            // schedule forward (ADR-037). The domain refuses to apply a
            // Spelling result to a word that is now at Writing, and rightly so;
            // what it must not do is leave the session unclosable, which is
            // what an exception here means: it is resumed on every visit,
            // answered through, and fails to finish for ever (ADR-043).
            if (word.State != WordState.Learning || word.CurrentSkill != session.Skill)
            {
                outcomes.Add(new
                {
                    wordId = word.Id,
                    text = word.Text,
                    meaning = word.Meaning,
                    passed = false,
                    newStatus = word.CurrentSkill?.ToWire() ?? word.State.ToWire(),
                    nextSkill = word.CurrentSkill?.ToWire(),
                    nextEligibleAt = (DateTimeOffset?)null,
                    becameActive = false,
                    firstAttemptCorrect = false,
                    attemptsInSession = session.AttemptsFor(word.Id),
                    // Nothing was applied: this session is no longer the one
                    // deciding what happens to this word.
                    superseded = true,
                });
                continue;
            }

            // A word this session never actually asked about cannot have failed
            // it. Completing early — a client that posts /complete with items
            // still queued, or a Speaking session the learner never spoke a
            // word into — used to run every untouched word through the state
            // machine as a failure: measured, eight words came back
            // `FAILED` with `attemptsInSession: 0` and a two-day wait, having
            // never been shown to anybody.
            //
            // That is worse than a rude edge case. Rule R8 aside, this service
            // exists to measure whether the pipeline works
            // (docs/00-PROJECT-PLAN.md §1), and a failure nobody was asked for
            // is a false reading in the only data the experiment has. The word
            // is left exactly as it was — still due, at the same skill, with
            // the same date — which is what abandoning would have done.
            if (!AskedAbout(session, word.Id, learnerSpoke))
            {
                outcomes.Add(new
                {
                    wordId = word.Id,
                    text = word.Text,
                    meaning = word.Meaning,
                    passed = false,
                    newStatus = word.SkillState(session.Skill).Status.ToWire(),
                    nextSkill = word.CurrentSkill?.ToWire(),
                    nextEligibleAt = word.SkillState(session.Skill).AvailableAt,
                    becameActive = false,
                    firstAttemptCorrect = false,
                    attemptsInSession = 0,
                    // Distinct from `superseded`: that word moved on elsewhere,
                    // this one simply never came up. Both mean "nothing was
                    // applied", and the client says neither passed nor failed.
                    untouched = true,
                });
                continue;
            }

            var passed = session.Skill == SkillType.Speaking
                ? SpeakingPassed(session, word, spoken)
                : session.PassedFor(word.Id);

            // The domain state machine decides what happens next: a failure
            // reschedules only this skill and never touches the ones already
            // passed (rule R5).
            var outcome = word.ApplySessionResult(session.Skill, passed, config, now);
            if (passed) passedCount++;

            // What the conversation showed about this word, in the learner's
            // own words — computed for the verdict and, until now, thrown away.
            // A learner told only that a word failed has learned that they
            // failed, and nothing else (ADR-048).
            spoken.TryGetValue(word.Id, out var observation);

            outcomes.Add(new
            {
                wordId = word.Id,
                text = word.Text,
                meaning = word.Meaning,
                passed,
                newStatus = outcome.NewStatus.ToWire(),
                nextSkill = outcome.NextSkill?.ToWire(),
                nextEligibleAt = outcome.NextEligibleAt,
                becameActive = outcome.BecameActive,
                firstAttemptCorrect = passed,
                attemptsInSession = session.AttemptsFor(word.Id),
                feedback = observation?.Feedback,
                evidence = observation?.Evidence,
                better = string.IsNullOrWhiteSpace(observation?.Better)
                    ? null
                    : observation!.Better,
            });
        }

        // Comprehension accuracy drives the level engine; it measures whether
        // the *content level* was right, which target-word results do not.
        var comprehension = session.Items
            .Where(i => i.Type == SessionItemType.Comprehension)
            .ToList();

        var accuracy = comprehension.Count > 0
            ? (double)comprehension.Count(i => i.FirstAttemptCorrect == true)
              / comprehension.Count
            : wordIds.Count == 0 ? 0 : (double)passedCount / wordIds.Count;

        var user = await db.Users
            .Include(u => u.SkillLevels)
            .FirstAsync(u => u.Id == session.UserId, ct);

        var level = user.LevelFor(session.Skill);

        // A practice session is evidence of nothing (Part 2 §5). It owns no
        // words, so no word can pass or fail; and it must not reach the level
        // engine either, because a promotion archives Active vocabulary — which
        // would let an activity the learner chose *instead of* their pipeline
        // quietly change it.
        if (!session.IsPractice) level.RecordSession(accuracy);

        // With fresh evidence in hand, ask whether the validated level moves —
        // and if it rose, whether any Active words have been outgrown.
        var decision = session.IsPractice ? null : levels.Evaluate(level);
        if (decision is not null)
        {
            levels.Apply(level, decision);

            if (decision.Moved)
            {
                db.LevelChanges.Add(LevelChangeRecord.Create(
                    user.Id, session.Skill, decision.Previous, decision.Next,
                    LevelChangeType.SystemValidatedChange, now,
                    reason: "session performance",
                    sessionsConsidered: decision.SessionsConsidered,
                    accuracy: decision.Accuracy));

                if (decision.IsPromotion)
                {
                    var active = await db.Words
                        .Where(w => w.UserId == user.Id && w.State == WordState.Active)
                        .ToListAsync(ct);
                    levels.ArchiveOutgrown(active, user.SkillLevels, now);
                }
            }
        }

        session.Complete(now);
        db.ActivityEvents.Add(ActivityEvent.Record(
            session.UserId,
            session.IsPractice
                ? ActivityType.PracticeCompleted
                : ActivityType.SessionCompleted,
            now, session.Skill, session.Id));
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            sessionId = session.Id,
            skill = session.Skill.ToWire(),
            isPractice = session.IsPractice,
            comprehension = new
            {
                correct = comprehension.Count(i => i.FirstAttemptCorrect == true),
                total = comprehension.Count,
            },
            words = outcomes,
            // How the conversation went as a whole. Empty for every other
            // skill, which has no conversation to summarise.
            summary = string.IsNullOrWhiteSpace(speakingSummary) ? null : speakingSummary,
            durationMs = (int)(now - session.StartedAt).TotalMilliseconds,
            usedAiFallback = session.UsedAiFallback,
        });
    }

    /// <summary>
    /// Returns a session as it stands.
    /// </summary>
    /// <remarks>
    /// The client is stateless about sessions by design (rule R1): after a
    /// crash, a backgrounded app or a lost connection it asks the server where
    /// it was rather than reconstructing it locally. Regenerating the content
    /// would produce a different passage, so it is read back as stored.
    /// </remarks>
    private static async Task<IResult> ResumeAsync(
        Guid id,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        CancellationToken ct)
    {
        var (session, error) = await LoadSessionAsync(id, principal, db, ct);
        if (error is not null) return error;

        var wordIds = session!.Items
            .Where(i => i.WordId is not null)
            .Select(i => i.WordId!.Value)
            .Distinct()
            .ToList();

        var words = await db.Words
            .Where(w => wordIds.Contains(w.Id))
            .ToListAsync(ct);

        return Results.Ok(ToSessionResponse(session, words));
    }

    private static async Task<IResult> AbandonAsync(
        Guid id,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return Results.Unauthorized();

        // Leaving early must not consume the words: the session is dropped and
        // they stay due.
        var session = await db.SkillSessions
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);

        // A finished session is not abandonable, and this is the one route that
        // used to say otherwise: it did not go through LoadSessionAsync, so it
        // alone skipped the `IsComplete` check every other route makes. The
        // effect was that abandoning an already-completed session deleted it —
        // the row, its items, and with them the comprehension score, the token
        // cost, the prompt version and the level used.
        //
        // The word outcomes had already been applied, so nothing about the
        // learner's progress changed; what disappeared was the evidence for
        // *why* it changed. This service exists to measure its own algorithm
        // (docs/00-PROJECT-PLAN.md §1), and a client that fires abandon on
        // dispose — after the session completed — was quietly deleting the
        // measurement. Answering 409, exactly as the other routes do, keeps it.
        if (session is not null && session.IsComplete)
        {
            return Problems.Conflict(
                "SESSION_COMPLETE", "This session is already finished.");
        }

        if (session is not null)
        {
            db.SkillSessions.Remove(session);
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Credits an exposure for every Active word the generated content actually
    /// reused.
    /// </summary>
    /// <remarks>
    /// The whole point sits in the two guards:
    ///
    /// <list type="number">
    /// <item><see cref="ActiveWordReuseDetector"/> reads the content the server
    /// received. The model's own claim about which words it used is never
    /// consulted, and no client may report an exposure at all.</item>
    /// <item>The written <see cref="WordExposure"/> row is unique per
    /// (word, source, session), so a word occurring three times in one passage —
    /// or once per turn across a whole conversation — counts exactly once. A
    /// later session is a genuinely new encounter and counts again.</item>
    /// </list>
    ///
    /// Nothing here can retire a word: exposure prioritises, it never limits
    /// (rule R8). It is written in the caller's transaction, so content and its
    /// exposures are persisted together or not at all.
    /// </remarks>
    private static async Task CreditExposureAsync(
        WordOsDbContext db,
        Guid sessionId,
        IReadOnlyList<Word> activeWords,
        string? content,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (activeWords.Count == 0) return;

        var reused = ActiveWordReuseDetector.Detect(content, activeWords, w => w.Text);
        if (reused.Count == 0) return;

        // Already credited in an earlier turn of this same session.
        var counted = await db.WordExposures
            .Where(e => e.SourceId == sessionId
                        && e.Source == ExposureSource.AiContentReuse)
            .Select(e => e.WordId)
            .ToListAsync(ct);

        var seen = counted.ToHashSet();

        foreach (var word in reused)
        {
            // The set also absorbs a repeat inside this one unit of work; the
            // unique index is the final backstop.
            if (!seen.Add(word.Id)) continue;

            db.WordExposures.Add(WordExposure.Record(
                word.Id, ExposureSource.AiContentReuse, sessionId, now));
            word.RecordExposure(now);
        }
    }

    /// <summary>
    /// Whether a word passed Speaking.
    /// </summary>
    /// <remarks>
    /// The verdict is <see cref="SpeakingRules"/>', applied to what the model
    /// observed across the whole conversation (ADR-019). If the evaluation
    /// produced nothing for this word — an AI outage with no usable fallback, or
    /// a learner who never spoke — it falls back to the older, cruder test:
    /// the word appearing in a turn substantial enough to be a real sentence
    /// (ADR-016). Failing closed instead would punish a learner for an outage.
    /// </remarks>
    /// <summary>
    /// A session whose row exists but whose content was never built.
    /// </summary>
    /// <remarks>
    /// Every real session has at least one of three things: a passage, a
    /// conversation, or items. A row with none of them is one whose start
    /// claimed its slot (ADR-063) and then failed before the content landed —
    /// the AI gate refusing at capacity, or the process going away.
    ///
    /// This is a state, not a verdict. A session being built <i>right now</i>
    /// looks identical, which is why nothing acts on it without also asking how
    /// old it is — see <see cref="IsAbandonedMidBuild"/>.
    /// </remarks>
    private static bool IsUnbuilt(SkillSession session) =>
        session.ContentText is null
        && session.TranscriptJson is null
        && session.Items.Count == 0;

    /// <summary>
    /// An unbuilt session old enough that nobody can still be building it.
    /// </summary>
    /// <remarks>
    /// The age check is the whole point. Without it the recovery path for a
    /// dead start destroys a live one: a second request arriving during the
    /// first request's generation sees an unbuilt row, deletes it, takes the
    /// skill for itself, and the first fails saving content into a row that is
    /// no longer there. Measured as one start in six answering 500 under load.
    /// </remarks>
    private static bool IsAbandonedMidBuild(
        SkillSession session,
        DateTimeOffset now,
        WordOsConfiguration config) =>
        IsUnbuilt(session)
        && session.StartedAt
           <= now.AddSeconds(-config.SessionBuildGraceSeconds);

    /// <summary>
    /// An open practice session too old to be worth resuming.
    /// </summary>
    /// <remarks>
    /// Nothing expired before this: a session left open in April was still what
    /// the skill handed back in August, because a start resumes rather than
    /// replaces. For practice that is pure obstruction — it measures nothing
    /// (Part 2 §5), so an abandoned one is not work anybody needs returned.
    ///
    /// The window is deliberately generous (<see
    /// cref="WordOsConfiguration.PracticeSessionExpiryHours"/>): a learner who
    /// steps away mid-practice and comes back the same day still gets their
    /// place back, which is the case this must not break.
    ///
    /// Real sessions are never dropped on age. They hold answers a learner
    /// actually gave, and losing those is worse than any staleness.
    /// </remarks>
    private static bool IsStalePractice(
        SkillSession open,
        DateTimeOffset now,
        WordOsConfiguration config) =>
        open.IsPractice
        && open.StartedAt <= now.AddHours(-config.PracticeSessionExpiryHours);

    /// <summary>
    /// Whether this write lost the race for the one open session per skill.
    /// </summary>
    private static bool IsOpenSessionConflict(DbUpdateException e) =>
        UniqueViolation.On(e, "ix_skill_sessions_one_open_per_skill");

    /// <summary>
    /// Whether this session actually put this word in front of the learner.
    /// </summary>
    /// <remarks>
    /// Only a word that was asked about can be judged, and the two shapes of
    /// session ask differently:
    ///
    /// <list type="bullet">
    /// <item>Reading, Listening, Writing and Spelling ask through items. The
    /// word was asked about when at least one of its items was attempted —
    /// <i>attempted</i>, not cleared, because a wrong answer is an answer.</item>
    /// <item>Speaking has no items at all: it asks by holding a conversation.
    /// The word was asked about when the learner took a turn in it. A
    /// conversation the learner never spoke into tested nothing.</item>
    /// </list>
    ///
    /// A word this returns false for is left untouched at completion — see the
    /// <c>untouched</c> branch in <see cref="CompleteAsync"/>.
    /// </remarks>
    private static bool AskedAbout(
        SkillSession session, Guid wordId, bool learnerSpoke) =>
        session.Skill == SkillType.Speaking
            ? learnerSpoke
            : session.Items.Any(i => i.WordId == wordId && i.Attempts > 0);

    private static bool SpeakingPassed(
        SkillSession session,
        Word word,
        IReadOnlyDictionary<Guid, SpeakingWordObservation> observed)
    {
        // Whether they used the word is a fact this session watched happen and
        // recorded, turn by turn, with the sentence in front of it. Whether
        // they used it *well* is the end-of-conversation judgement.
        //
        // So a verdict of "never used" about a word this session saw used is
        // not a verdict about quality at all — it is a disagreement over a fact
        // we hold the evidence for. The observation is set aside and the
        // evidence answers instead, rather than failing a learner for a word
        // they were recorded saying (ADR-040).
        var recordedAsUsed = session.UsedWords
            .Contains(word.Text, StringComparer.OrdinalIgnoreCase);

        if (observed.TryGetValue(word.Id, out var observation)
            && (observation.Used || !recordedAsUsed))
        {
            return SpeakingRules.Passed(new SpokenWordObservation(
                observation.Used,
                observation.MeaningCorrect,
                observation.Understandable,
                observation.GrammarAcceptable,
                observation.MajorGrammarProblem));
        }

        var history = JsonSerializer.Deserialize<List<TranscriptEntry>>(
            session.TranscriptJson ?? "[]") ?? [];

        return history
            .Where(t => !t.FromAi)
            .Any(t => t.Text.Contains(word.Text, StringComparison.OrdinalIgnoreCase)
                      && t.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .Length >= 5);
    }

    private static async Task<(SkillSession? Session, IResult? Error)> LoadSessionAsync(
        Guid id,
        ClaimsPrincipal principal,
        WordOsDbContext db,
        CancellationToken ct)
    {
        var userId = principal.UserId();
        if (userId is null) return (null, Results.Unauthorized());

        // Scoped by the caller's own id — another learner's session is not
        // addressable even with its id (docs/07-SECURITY.md §4).
        var session = await db.SkillSessions
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);

        if (session is null)
        {
            return (null, Problems.NotFound(
                "SESSION_NOT_FOUND", "This session has expired. Please start again."));
        }

        if (session.IsComplete)
        {
            return (null, Problems.Conflict(
                "SESSION_COMPLETE", "This session is already finished."));
        }

        return (session, null);
    }

    private static object Progress(SkillSession session) => new
    {
        nextItemId = session.CurrentItemId,
        remaining = session.Queue.Count,
        answered = session.ClearedCount,
        total = session.TotalItems,
        // Whether the learner has attempted anything at all — which is not the
        // same as `answered`, because a wrong answer requeues the item and
        // clears nothing. A resumed session uses this to decide whether the
        // passage or audio step is already behind the learner; keying that off
        // `answered` sends anyone who missed their first question back to the
        // start.
        attempted = session.Items.Any(i => i.Attempts > 0),
    };

    private static object ToSessionResponse(
        SkillSession session,
        IReadOnlyList<Word> words) => new
    {
        id = session.Id,
        skill = session.Skill.ToWire(),
        levelUsed = session.LevelUsed.ToWire(),
        // The client says so on screen: a learner should never be unsure
        // whether what they just did counted (§5).
        isPractice = session.IsPractice,
        content = session.ContentText is null ? null : new
        {
            text = session.ContentText,
            // Listening hides the transcript until the test is over.
            revealTextAfterTest = session.Skill == SkillType.Listening,
            // Every word of the passage with the meaning it carries here, so
            // a tap answers instantly and answers about *this* sentence.
            glossary = session.GlossaryJson is null
                ? null
                : JsonSerializer.Deserialize<List<GlossaryWire>>(
                    session.GlossaryJson),
            // Whether the learner may still re-tell this passage at another
            // level: only before they have answered anything.
            canChangeLevel = session.CanChangeLevel,
            // Where the session's own words sit inside the generated text, so
            // the client can mark them without searching for them itself
            // (Part 2 §16, rule R1). Ordered and non-overlapping, because the
            // renderer walks them in one pass.
            targetSpans = words
                .SelectMany(w => ActiveWordReuseDetector
                    .Locate(session.ContentText, w.Text)
                    .Select(span => new
                    {
                        wordId = w.Id,
                        start = span.Start,
                        // Length, not an end offset — the wire contract and the
                        // client model have always spoken in lengths.
                        length = span.End - span.Start,
                    }))
                .OrderBy(s => s.start)
                .ToList(),
        },
        targetWords = words.Select(w => new
        {
            wordId = w.Id,
            text = w.Text,
            meaning = w.Meaning,
        }).ToList(),
        // The warm-up Speaking opens with: each word, four meanings, shuffled
        // here (rule R7). The right answer is deliberately absent — the client
        // asks, it does not mark.
        //
        // Empty when the session has no words, which is the whole of §7: a
        // learner with nothing due walks straight into the conversation.
        warmup = session.Skill != SkillType.Speaking
            ? null
            : SessionContentBuilder.BuildWarmup(words, Random.Shared)
                .Select(w => new
                {
                    wordId = w.WordId,
                    text = w.Text,
                    options = w.Options,
                })
                .ToList(),
        items = session.Items.Select(i => new
        {
            id = i.Id,
            type = i.Type.ToWire(),
            wordId = i.WordId,
            prompt = i.Prompt,
            // The instruction as a key, so the client can say it in the
            // learner's own language. Null for anything written for this
            // session — content is not translated (ADR-035).
            promptKey = i.PromptKey?.ToWire(),
            // Already shuffled server-side (rule R7). The correct answer is
            // deliberately absent.
            options = JsonSerializer.Deserialize<List<string>>(i.OptionsJson),
            context = i.ContextJson is null
                ? (JsonElement?)null
                : JsonSerializer.Deserialize<JsonElement>(i.ContextJson),
            audioText = i.AudioText,
            clue = i.Clue,
            clueKind = i.ClueKind?.ToWire(),
            // The whole ladder, easiest last. The client reveals one rung per
            // press; what each rung says was decided here, from the learner's
            // level and what the lexicon holds (rule R1).
            hints = i.HintsJson is null
                ? null
                : JsonSerializer.Deserialize<List<HintWire>>(i.HintsJson),
            letters = i.LettersJson is null
                ? null
                : JsonSerializer.Deserialize<List<string>>(i.LettersJson),
            inputMode = i.InputMode?.ToWire(),
        }).ToList(),
        conversation = session.TranscriptJson is null ? null : new
        {
            opening = (JsonSerializer.Deserialize<List<TranscriptEntry>>(
                session.TranscriptJson) ?? []).FirstOrDefault()?.Text,
            // The whole exchange, not just the opening line. A resumed
            // conversation has to come back with its history: the client holds
            // no session state (rule R1), so anything omitted here is simply
            // lost to the learner even though the server still has it.
            turns = (JsonSerializer.Deserialize<List<TranscriptEntry>>(
                    session.TranscriptJson) ?? [])
                .Select(t => new { fromAi = t.FromAi, text = t.Text })
                .ToList(),
        },
        progress = Progress(session),
        usedAiFallback = session.UsedAiFallback,
    };

    private sealed record TranscriptEntry(bool FromAi, string Text);
}
