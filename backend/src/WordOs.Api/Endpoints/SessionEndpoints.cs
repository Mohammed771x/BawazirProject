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
using WordOs.Domain.Words;
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
        group.MapPost("/{id:guid}/complete", CompleteAsync);
        group.MapPost("/{id:guid}/abandon", AbandonAsync);
        group.MapGet("/{id:guid}", ResumeAsync);

        return app;
    }

    // ── Start ────────────────────────────────────────────────────────────────

    private static async Task<IResult> StartAsync(
        string skill,
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

        if (due.Count == 0)
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

        var session = SkillSession.Start(userId.Value, skillType, contentLevel, now);
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
                    content.Tokens, content.FromFallback);

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
                SessionContentBuilder.BuildSpellingItems(
                    session, due, contentLevel, level.SpellingSupportMode, random);
                break;

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
            session.LevelUsed, sentence), ct);

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
                            .ToList()),
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
        level.RecordSession(accuracy);

        // With fresh evidence in hand, ask whether the validated level moves —
        // and if it rose, whether any Active words have been outgrown.
        var decision = levels.Evaluate(level);
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
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            sessionId = session.Id,
            skill = session.Skill.ToWire(),
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
        content = session.ContentText is null ? null : new
        {
            text = session.ContentText,
            // Listening hides the transcript until the test is over.
            revealTextAfterTest = session.Skill == SkillType.Listening,
        },
        targetWords = words.Select(w => new
        {
            wordId = w.Id,
            text = w.Text,
            meaning = w.Meaning,
        }).ToList(),
        items = session.Items.Select(i => new
        {
            id = i.Id,
            type = i.Type.ToWire(),
            wordId = i.WordId,
            prompt = i.Prompt,
            // Already shuffled server-side (rule R7). The correct answer is
            // deliberately absent.
            options = JsonSerializer.Deserialize<List<string>>(i.OptionsJson),
            context = i.ContextJson is null
                ? (JsonElement?)null
                : JsonSerializer.Deserialize<JsonElement>(i.ContextJson),
            audioText = i.AudioText,
            clue = i.Clue,
            clueKind = i.ClueKind?.ToWire(),
            letters = i.LettersJson is null
                ? null
                : JsonSerializer.Deserialize<List<string>>(i.LettersJson),
            inputMode = i.InputMode?.ToWire(),
            hint = i.Hint,
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
