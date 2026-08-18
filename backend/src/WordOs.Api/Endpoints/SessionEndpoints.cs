using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

        if (session!.Skill is not (SkillType.Reading or SkillType.Listening
            or SkillType.Speaking))
        {
            return Problems.BadRequest(
                "LEVEL_NOT_ADJUSTABLE",
                "This session has no level to change.");
        }

        // A conversation has no passage to re-tell: the level is what the next
        // turn is generated at, so changing it takes effect on the very next
        // thing the tutor says. Reading and Listening have a text in front of
        // the learner, so theirs is locked once the questions begin.
        var isConversation = session.Skill == SkillType.Speaking;

        if (!isConversation && !session.CanChangeLevel)
        {
            return Problems.Conflict(
                "SESSION_STARTED",
                "The level cannot change once the questions have begun.");
        }

        var level = CefrLevelExtensions.TryFromWire(request.Level);
        if (level is null)
            return Problems.BadRequest("INVALID_LEVEL", "Unknown level.");

        if (!isConversation && string.IsNullOrWhiteSpace(session.ContentText))
            return Problems.Conflict("NO_CONTENT", "This session has no passage.");

        var wordIds = session.Items
            .Where(i => i.WordId is not null)
            .Select(i => i.WordId!.Value)
            .Distinct()
            .ToList();

        var words = await db.Words
            .Where(w => wordIds.Contains(w.Id))
            .ToListAsync(ct);

        // ── The conversation case: set the level, say nothing new ─────────
        if (isConversation)
        {
            RecordLevelChoice(db, user: await db.Users
                .Include(u => u.SkillLevels)
                .FirstAsync(u => u.Id == session.UserId, ct),
                skill: session.Skill, level: level.Value, now: clock.GetUtcNow());

            session.SetLevel(level.Value);
            await db.SaveChangesAsync(ct);

            return Results.Ok(ToSessionResponse(session, await db.Words
                .Where(w => w.UserId == session.UserId
                            && w.State == WordState.Learning
                            && w.CurrentSkill == session.Skill)
                .ToListAsync(ct)));
        }

        GeneratedContent content;
        try
        {
            content = await ai.RelevelContentAsync(new RelevelRequest(
                session.ContentText!,
                session.LevelUsed,
                level.Value,
                words.Select(w => new AiTargetWord(
                    w.Text, w.Meaning, w.DefinitionEn, w.PartOfSpeech)).ToList(),
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
            var openWordIds = open.Items
                .Where(i => i.WordId is not null)
                .Select(i => i.WordId!.Value)
                .Distinct()
                .ToList();

            var openWords = await db.Words
                .Where(w => openWordIds.Contains(w.Id))
                .ToListAsync(ct);

            return Results.Ok(ToSessionResponse(open, openWords));
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
        var isPractice = practice == true
                         && skillType is SkillType.Reading or SkillType.Listening;

        if (due.Count == 0 && !isPractice)
        {
            return Problems.Conflict(
                "NO_WORDS_DUE", "No words are due for this skill yet.");
        }

        // Spelling carries no CEFR band of its own, so its content difficulty
        // follows Reading: whether an English definition is a usable clue is a
        // reading-comprehension question (ADR-008).
        var contentLevel = level.UserSelectedLevel
                           ?? user.LevelFor(SkillType.Reading).UserSelectedLevel
                           ?? CefrLevel.B1;

        var session = SkillSession.Start(
            userId.Value, skillType, contentLevel, now, isPractice);
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
                    Words: due.Select(w => new AiTargetWord(
                        w.Text, w.Meaning, w.DefinitionEn, w.PartOfSpeech)).ToList(),
                    Listening: listening,
                    ComprehensionCount: config.ComprehensionQuestionCount,
                    ReuseWords: activeWords.Select(w => w.Text).ToList()), ct);

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
                    Transcript: []), ct);

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

        db.SkillSessions.Add(session);
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
        var isCorrect = item.Type == SessionItemType.SpellingTask
            ? string.Equals(request.Answer.Trim(), item.CorrectAnswer.Trim(),
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

        var user = await db.Users.FirstAsync(u => u.Id == session.UserId, ct);
        var words = await db.Words
            .Where(w => w.UserId == session.UserId
                        && w.State == WordState.Learning
                        && w.CurrentSkill == SkillType.Speaking)
            .ToListAsync(ct);

        var history = JsonSerializer.Deserialize<List<TranscriptEntry>>(
            session.TranscriptJson ?? "[]") ?? [];
        history.Add(new TranscriptEntry(false, transcript));

        // Which words the learner has genuinely used, accumulated across turns.
        var alreadyUsed = history
            .Where(t => !t.FromAi)
            .SelectMany(t => words.Where(w =>
                t.Text.Contains(w.Text, StringComparison.OrdinalIgnoreCase)))
            .Select(w => w.Text)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var remaining = words
            .Where(w => !alreadyUsed.Contains(w.Text, StringComparer.OrdinalIgnoreCase))
            .Select(w => w.Text)
            .ToList();

        var turn = await ai.SpeakingTurnAsync(new SpeakingTurnRequest(
            user.DisplayName, session.LevelUsed, remaining, alreadyUsed,
            history.Select(h => new SpeakingTranscriptTurn(h.FromAi, h.Text))
                .ToList()), ct);

        if (turn.FromFallback) session.MarkFallbackUsed();
        session.RecordAiCall(turn.PromptVersion, turn.Model, turn.Tokens);

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

        var words = await db.Words
            .Where(w => w.UserId == session!.UserId
                        && w.State == WordState.Learning
                        && w.CurrentSkill == session.Skill)
            .Include(w => w.Skills)
            .ToListAsync(ct);

        // Which words this session actually covered.
        var wordIds = session!.Skill == SkillType.Speaking
            ? words.Select(w => w.Id).ToHashSet()
            : session.Items.Where(i => i.WordId is not null)
                .Select(i => i.WordId!.Value).ToHashSet();

        // Speaking is judged once, here, on the whole conversation — not turn by
        // turn. A learner who fumbles a word early and uses it well later is
        // judged on the exchange as a whole, and per-turn evaluation would
        // multiply the cost of a session by however much they said.
        var spoken = new Dictionary<Guid, SpeakingWordObservation>();
        var speakingSummary = string.Empty;

        if (session.Skill == SkillType.Speaking)
        {
            var transcript = JsonSerializer
                .Deserialize<List<TranscriptEntry>>(session.TranscriptJson ?? "[]")
                ?? [];

            var spokenWords = words.Where(w => wordIds.Contains(w.Id)).ToList();

            // Nothing to judge if the learner never said anything.
            if (spokenWords.Count > 0 && transcript.Any(t => !t.FromAi))
            {
                var learner = await db.Users
                    .FirstAsync(u => u.Id == session.UserId, ct);

                var evaluation = await ai.EvaluateSpeakingAsync(
                    new SpeakingEvaluationRequest(
                        learner.DisplayName,
                        session.LevelUsed,
                        spokenWords.Select(w => new AiTargetWord(
                            w.Text, w.Meaning, w.DefinitionEn, w.PartOfSpeech))
                            .ToList(),
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
            var passed = session.Skill == SkillType.Speaking
                ? SpeakingPassed(session, word, spoken)
                : session.PassedFor(word.Id);

            // The domain state machine decides what happens next: a failure
            // reschedules only this skill and never touches the ones already
            // passed (rule R5).
            var outcome = word.ApplySessionResult(session.Skill, passed, config, now);
            if (passed) passedCount++;

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
    private static bool SpeakingPassed(
        SkillSession session,
        Word word,
        IReadOnlyDictionary<Guid, SpeakingWordObservation> observed)
    {
        if (observed.TryGetValue(word.Id, out var observation))
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
