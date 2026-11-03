using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WordOs.Api.Endpoints;
using WordOs.Application.Abstractions;
using WordOs.Domain.Analytics;
using WordOs.Domain.Common;
using WordOs.Domain.Users;
using WordOs.Infrastructure.Persistence;
using static WordOs.Api.Admin.AdminIntelEndpoints;

namespace WordOs.Api.Admin;

/// <summary>
/// WordOS AI — the admin area's conversation with its own data (ADR-133).
/// </summary>
/// <remarks>
/// <para>The backend runs the loop: it hands the model the conversation, the
/// tool catalogue and the metric dictionary; the model answers or names tools;
/// the backend validates and runs them (read-only, scoped, aggregated) and
/// hands the results back, at most <see cref="AdminIntelOptions.AgentMaxRounds"/>
/// times before the model must answer. Nothing is queried unless the model
/// asks, so a conversation about an idea reads no data at all.</para>
///
/// <para>Each admin sees their own conversations. A conversation is never a
/// decision: saving an answer as an investigation and proposing a decision
/// are separate, explicit acts.</para>
/// </remarks>
public static class AdminAgentEndpoints
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public sealed record SendRequest(
        [property: Required, StringLength(4000, MinimumLength = 1)] string Text,
        Guid? ConversationId = null);

    public sealed record ConversationPatch(
        [property: StringLength(120)] string? Title = null,
        bool? Archived = null);

    /// <summary>One tool run, as the "sources" panel shows it and a follow-up may reuse it.</summary>
    public sealed record Source(
        string Tool, string Args, string? Why, AgentMeta? Meta, string Result, bool Cached, string? Error, DateTimeOffset At);

    public static IEndpointRouteBuilder MapAdminAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/intel/ai")
            .WithTags("Admin Intelligence")
            .RequireAuthorization(Policies.AdminArea);

        group.MapGet("/conversations", ListAsync);
        group.MapGet("/conversations/{id:guid}", GetAsync);
        group.MapPatch("/conversations/{id:guid}", PatchAsync);
        group.MapDelete("/conversations/{id:guid}", DeleteAsync);
        group.MapPost("/messages", SendAsync).RequireRateLimiting(RateLimitPolicies.Expensive);
        group.MapPost("/conversations/{id:guid}/regenerate", RegenerateAsync).RequireRateLimiting(RateLimitPolicies.Expensive);
        group.MapPost("/messages/{id:guid}/investigation", SaveInvestigationAsync);
        return app;
    }

    // ── Conversations ───────────────────────────────────────────────────────

    private static async Task<IResult> ListAsync(
        string? q, bool? archived, ClaimsPrincipal principal, WordOsDbContext db, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var me = principal.UserId()!.Value;
        var query = db.AiConversations.AsNoTracking().Where(c => c.AuthorId == me && c.Archived == (archived ?? false));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = $"%{q.Trim()}%";
            // A title, or anything said in it.
            query = query.Where(c => EF.Functions.ILike(c.Title, term)
                                     || db.AiMessages.Any(m => m.ConversationId == c.Id && EF.Functions.ILike(m.Content, term)));
        }
        var items = await query.OrderByDescending(c => c.UpdatedAt).Take(200)
            .Select(c => new { id = c.Id, title = c.Title, archived = c.Archived, createdAt = c.CreatedAt, updatedAt = c.UpdatedAt })
            .ToListAsync(ct);
        return Results.Ok(new { items });
    }

    private static async Task<AiConversation?> OwnAsync(WordOsDbContext db, ClaimsPrincipal principal, Guid id, CancellationToken ct) =>
        await db.AiConversations.FirstOrDefaultAsync(c => c.Id == id && c.AuthorId == principal.UserId(), ct);

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal principal, WordOsDbContext db, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var c = await OwnAsync(db, principal, id, ct);
        if (c is null) return Problems.NotFound("NOT_FOUND", "No such conversation.");
        var messages = await db.AiMessages.AsNoTracking().Where(m => m.ConversationId == id)
            .OrderBy(m => m.CreatedAt).ToListAsync(ct);
        return Results.Ok(new
        {
            conversation = new { id = c.Id, title = c.Title, archived = c.Archived, createdAt = c.CreatedAt, updatedAt = c.UpdatedAt },
            messages = messages.Select(View),
        });
    }

    private static object View(AiMessage m) => new
    {
        id = m.Id,
        role = m.Role,
        content = m.Content,
        failed = m.Failed,
        createdAt = m.CreatedAt,
        inquiryId = m.InquiryId,
        // The page shows where an answer came from, not the raw aggregates.
        sources = m.SourcesJson is null ? [] : Sources(m).Select(s => new { s.Tool, s.Args, s.Why, s.Meta, s.Cached, s.Error, s.At }),
        attachments = m.AttachmentsJson is null ? (object)Array.Empty<object>() : JsonSerializer.Deserialize<JsonElement>(m.AttachmentsJson),
    };

    private static IReadOnlyList<Source> Sources(AiMessage m) =>
        m.SourcesJson is null ? [] : JsonSerializer.Deserialize<List<Source>>(m.SourcesJson, Web) ?? [];

    private static async Task<IResult> PatchAsync(
        Guid id, ConversationPatch request, ClaimsPrincipal principal, WordOsDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        if (!MiniValidator.TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        var c = await OwnAsync(db, principal, id, ct);
        if (c is null) return Problems.NotFound("NOT_FOUND", "No such conversation.");
        var now = clock.GetUtcNow();
        if (request.Title is { } t) c.Rename(t, now);
        if (request.Archived is { } a) c.Archive(a, now);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Deletes the admin's own conversation. An investigation saved from it is
    /// kept — it was saved precisely so that it would outlive the chat.
    /// </summary>
    private static async Task<IResult> DeleteAsync(
        Guid id, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db, IConfiguration configuration,
        TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var c = await OwnAsync(db, principal, id, ct);
        if (c is null) return Problems.NotFound("NOT_FOUND", "No such conversation.");
        db.AiConversations.Remove(c);
        await db.SaveChangesAsync(ct);
        await AuditAsync(db, principal, http, configuration, clock, "ai.deleted", id.ToString(), c.Title);
        return Results.NoContent();
    }

    // ── Messages ────────────────────────────────────────────────────────────

    private static async Task<IResult> SendAsync(
        SendRequest request, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db,
        WordOsConfiguration config, IOptions<AdminIntelOptions> options, IAdminAgentService agent,
        IConfiguration configuration, TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        if (!MiniValidator.TryValidate(request, out var errors)) return Results.ValidationProblem(errors);
        var now = clock.GetUtcNow();
        var me = principal.UserId()!.Value;

        AiConversation conversation;
        if (request.ConversationId is { } cid)
        {
            var found = await OwnAsync(db, principal, cid, ct);
            if (found is null) return Problems.NotFound("NOT_FOUND", "No such conversation.");
            conversation = found;
        }
        else
        {
            conversation = AiConversation.Start(Guid.CreateVersion7(now), me, Provisional(request.Text), now);
            db.AiConversations.Add(conversation);
        }

        var history = await db.AiMessages.AsNoTracking().Where(m => m.ConversationId == conversation.Id)
            .OrderByDescending(m => m.CreatedAt).Take(options.Value.AgentHistoryMessages).ToListAsync(ct);
        history.Reverse();

        var userMessage = AiMessage.FromUser(Guid.CreateVersion7(now), conversation.Id, request.Text, now);
        db.AiMessages.Add(userMessage);
        await db.SaveChangesAsync(ct);

        var reply = await AnswerAsync(db, principal, conversation, request.Text, history, config, options.Value, agent, clock, ct);
        await AuditAsync(db, principal, http, configuration, clock, "ai.message", conversation.Id.ToString(),
            ToolsDetail(reply));

        return Results.Ok(new
        {
            conversation = new { id = conversation.Id, title = conversation.Title },
            user = View(userMessage),
            reply = View(reply),
        });
    }

    /// <summary>Ask the last question again, replacing the last answer.</summary>
    private static async Task<IResult> RegenerateAsync(
        Guid id, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db, WordOsConfiguration config,
        IOptions<AdminIntelOptions> options, IAdminAgentService agent, IConfiguration configuration,
        TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var conversation = await OwnAsync(db, principal, id, ct);
        if (conversation is null) return Problems.NotFound("NOT_FOUND", "No such conversation.");

        var all = await db.AiMessages.Where(m => m.ConversationId == id).OrderBy(m => m.CreatedAt).ToListAsync(ct);
        var lastUser = all.LastOrDefault(m => m.Role == "user");
        if (lastUser is null) return Problems.BadRequest("NOTHING_TO_REGENERATE", "The conversation has no question yet.");
        var after = all.Where(m => m.CreatedAt > lastUser.CreatedAt && m.Role == "assistant").ToList();
        // An answer saved as an investigation stays; the investigation already holds it.
        db.AiMessages.RemoveRange(after.Where(m => m.InquiryId is null));
        await db.SaveChangesAsync(ct);

        var history = all.Where(m => m.CreatedAt < lastUser.CreatedAt).TakeLast(options.Value.AgentHistoryMessages).ToList();
        var reply = await AnswerAsync(db, principal, conversation, lastUser.Content, history, config, options.Value, agent, clock, ct,
            reuseCache: false);
        await AuditAsync(db, principal, http, configuration, clock, "ai.regenerated", id.ToString(), ToolsDetail(reply));
        return Results.Ok(new { reply = View(reply) });
    }

    private static string ToolsDetail(AiMessage reply)
    {
        var tools = Sources(reply).Select(s => s.Cached ? $"{s.Tool}(cached)" : s.Tool).ToList();
        return tools.Count == 0 ? "no data queried" : string.Join(",", tools);
    }

    private static string Provisional(string text)
    {
        var t = text.Trim().ReplaceLineEndings(" ");
        return t.Length <= 60 ? t : t[..60] + "…";
    }

    /// <summary>
    /// The loop: the model decides, the backend runs tools, until an answer.
    /// </summary>
    private static async Task<AiMessage> AnswerAsync(
        WordOsDbContext db, ClaimsPrincipal principal, AiConversation conversation, string text,
        IReadOnlyList<AiMessage> history, WordOsConfiguration config, AdminIntelOptions options,
        IAdminAgentService agent, TimeProvider clock, CancellationToken ct, bool reuseCache = true)
    {
        var privacy = PrivacyFor(principal);
        var started = clock.GetUtcNow();

        // Earlier answers carry the aggregates they rested on — the last two
        // only, which is what a follow-up refers to and keeps the prompt short.
        var withData = history.Where(m => m.Role == "assistant" && !m.Failed).TakeLast(2).Select(m => m.Id).ToHashSet();
        var turns = history.Select(m => new AgentTurn(m.Role, m.Content,
            withData.Contains(m.Id) ? DataOf(m) : null)).ToList();

        // Results this conversation already has, fresh enough to reuse.
        var cache = new Dictionary<string, Source>();
        if (reuseCache)
            foreach (var s in history.SelectMany(Sources).Where(s => s.Error is null
                         && started - s.At <= TimeSpan.FromMinutes(options.AgentCacheMinutes)))
                cache[AgentTools.CacheKey(s.Tool, s.Args)] = s;

        IntelDataset? data = null;
        var rounds = new List<AgentRound>();
        var sources = new List<Source>();
        var attachments = new List<AgentAttachment>();
        var tokens = 0;
        string? model = null;
        AgentStepResult? step = null;
        var today = DateOnly.FromDateTime(started.ToOffset(TimeSpan.FromHours(config.ReportingUtcOffsetHours)).DateTime).ToString("yyyy-MM-dd");

        var retried = false;
        string? fallback = null;
        for (var round = 0; round <= options.AgentMaxRounds; round++)
        {
            var mustAnswer = round == options.AgentMaxRounds || retried;
            step = await agent.StepAsync(new AgentStepRequest(
                text, turns, rounds, AgentTools.CatalogueText(), AgentTools.DictionaryText(), today,
                MustAnswer: mustAnswer), ct);
            if (step is null || step.Action == "limited") break;
            fallback ??= step.Fallback;
            tokens += step.Tokens;
            model = step.Model;
            if (step.Action != "tools" || step.Calls.Count == 0)
            {
                // An empty answer is asked for once more, told to answer now.
                if (string.IsNullOrWhiteSpace(step.Answer) && !mustAnswer)
                {
                    retried = true;
                    round--;
                    continue;
                }
                break;
            }

            var results = new List<string>();
            foreach (var (call, i) in step.Calls.Take(4).Select((c, i) => (c, i)))
            {
                var key = AgentTools.CacheKey(call.Tool, call.ArgsJson);
                if (cache.TryGetValue(key, out var hit))
                {
                    sources.Add(hit with { Why = call.Why, Cached = true });
                    results.Add(hit.Result);
                    continue;
                }
                data ??= await LoadAsync(db, config, Options.Create(options), clock, ct);
                var result = await AgentTools.RunAsync(call.Tool, call.ArgsJson, data, db, privacy, $"r{round}c{i}", ct);
                var source = new Source(call.Tool, call.ArgsJson, call.Why, result.Meta, result.ModelJson, false, result.Error, clock.GetUtcNow());
                sources.Add(source);
                if (result.Error is null) cache[key] = source;
                attachments.AddRange(result.Attachments);
                results.Add(result.ModelJson);
            }
            rounds.Add(new AgentRound(step.Calls.Take(4).ToList(), results));
        }

        var now = clock.GetUtcNow();
        AiMessage reply;
        if (step is null || string.IsNullOrWhiteSpace(step.Answer))
        {
            reply = AiMessage.FromAssistant(Guid.CreateVersion7(now), conversation.Id,
                step?.Action == "limited" ? LimitedText(step.RetryAfterSeconds)
                    : "تعذر الوصول إلى WordOS AI الآن، فلم تُكتب إجابة. لم يُعرض أي رقم. أعد المحاولة بعد قليل.",
                sources.Count == 0 ? null : JsonSerializer.Serialize(sources, Web), null, failed: true, model, tokens, now);
        }
        else
        {
            // Only what the model chose is shown; a user list it relied on is
            // always shown, because the answer refers to the people in it.
            var chosen = attachments
                .Where(a => step.Attachments.Contains(a.Id) || a.Kind == "users")
                .Where(a => a.Users is not { Count: 0 } && a.Rows is not { Count: 0 })
                .Take(4).ToList();
            reply = AiMessage.FromAssistant(Guid.CreateVersion7(now), conversation.Id, FallbackNote(fallback) + step.Answer.Trim(),
                sources.Count == 0 ? null : JsonSerializer.Serialize(sources, Web),
                chosen.Count == 0 ? null : JsonSerializer.Serialize(chosen, Web),
                failed: false, model, tokens, now);
            if (history.Count == 0 && !string.IsNullOrWhiteSpace(step.Title)) conversation.Rename(step.Title, now);
        }

        conversation.Touch(now);
        db.AiMessages.Add(reply);
        await db.SaveChangesAsync(ct);
        return reply;
    }

    /// <summary>
    /// The model runs on a free quota — per minute and per day (ADR-134).
    /// When it is used up the admin is told so, and roughly when it is back,
    /// rather than that something broke.
    /// </summary>
    /// <summary>
    /// Groq is the chat's model; when its free quota is used up or it cannot
    /// be reached, Gemini writes the answer instead, and the admin is told so
    /// at the top of it (ADR-134).
    /// </summary>
    private static string FallbackNote(string? fallback) => fallback switch
    {
        null => string.Empty,
        "quota" => "**تنبيه:** انتهت حصة Groq المجانية لهذا الوقت، فحوّلتُ هذه الإجابة إلى Gemini.\n\n",
        _ => "**تنبيه:** تعذر الوصول إلى Groq الآن، فحوّلتُ هذه الإجابة إلى Gemini.\n\n",
    };

    private static string LimitedText(int? seconds)
    {
        var when = seconds switch
        {
            null or <= 90 => "بعد دقيقة تقريبًا",
            < 3600 => $"بعد نحو {(int)Math.Ceiling(seconds.Value / 60.0)} دقيقة",
            _ => $"بعد نحو {(int)Math.Ceiling(seconds.Value / 3600.0)} ساعة",
        };
        return $"وصل WordOS AI إلى الحد المجاني المتاح له من الأسئلة الآن، فلم تُكتب إجابة ولم يُعرض أي رقم. أعد المحاولة {when}.";
    }

    private static string? DataOf(AiMessage m)
    {
        var sources = Sources(m).Where(s => s.Error is null).ToList();
        if (sources.Count == 0) return null;
        var text = string.Join("\n", sources.Select(s => $"{s.Tool}({s.Args}) at {s.At:yyyy-MM-dd HH:mm}: {s.Result}"));
        return text.Length <= 11_000 ? text : text[..11_000] + "…";
    }

    // ── Save as an investigation (brief §7) ─────────────────────────────────

    /// <summary>
    /// Turn one answer into an investigation, in the shape the investigation
    /// and decision pages already read.
    /// </summary>
    private static async Task<IResult> SaveInvestigationAsync(
        Guid id, HttpContext http, ClaimsPrincipal principal, WordOsDbContext db, IConfiguration configuration,
        TimeProvider clock, CancellationToken ct)
    {
        if (Deny(principal) is { } denied) return denied;
        var message = await db.AiMessages.FirstOrDefaultAsync(m => m.Id == id && m.Role == "assistant", ct);
        if (message is null) return Problems.NotFound("NOT_FOUND", "No such answer.");
        var conversation = await OwnAsync(db, principal, message.ConversationId, ct);
        if (conversation is null) return Problems.NotFound("NOT_FOUND", "No such answer.");
        if (message.Failed) return Problems.BadRequest("NOTHING_TO_SAVE", "That answer failed.");
        if (message.InquiryId is { } existing) return Results.Ok(new { id = existing });

        var question = await db.AiMessages.AsNoTracking()
            .Where(m => m.ConversationId == message.ConversationId && m.Role == "user" && m.CreatedAt < message.CreatedAt)
            .OrderByDescending(m => m.CreatedAt).Select(m => m.Content).FirstOrDefaultAsync(ct) ?? conversation.Title;

        var sources = Sources(message).Where(s => s.Error is null).ToList();
        var attachments = message.AttachmentsJson is null ? []
            : JsonSerializer.Deserialize<List<AgentAttachment>>(message.AttachmentsJson, Web) ?? [];
        var from = sources.Select(s => s.Meta?.From).OfType<string>().DefaultIfEmpty(null).Min();
        var to = sources.Select(s => s.Meta?.To).OfType<string>().DefaultIfEmpty(null).Max();

        var now = clock.GetUtcNow();
        var inquiryId = Guid.CreateVersion7(now);
        var me = await db.Users.AsNoTracking().FirstAsync(u => u.Id == principal.UserId(), ct);
        var investigation = new Investigation(
            inquiryId, "chat", "WordOS AI", question, now, me.DisplayName,
            message.Content,
            sources.Select(s => new EvidenceItem($"{s.Tool} — {s.Meta?.Sample}", null, "int",
                $"{s.Meta?.From} — {s.Meta?.To} · {s.Args}")).ToList(),
            attachments.Where(a => a.Chart is not null).Select(a => a.Chart!).ToList(),
            attachments.Where(a => a.Users is not null).SelectMany(a => a.Users!).DistinctBy(u => u.Id).ToList(),
            "المستخدمون المرتبطون بالإجابة",
            [], null, [], [], [],
            "ai",
            "حُفظت من محادثة WordOS AI. الأرقام في النص محسوبة من الأدوات المذكورة في الأدلة، بالفترات المذكورة معها.",
            DateTimeOffset.TryParse(from, out var f) ? f : message.CreatedAt,
            DateTimeOffset.TryParse(to, out var t) ? t.AddDays(1) : message.CreatedAt,
            Mode: sources.Count == 0 ? "discussion" : "data");
        var json = JsonSerializer.Serialize(investigation, Web);

        db.AdminInquiries.Add(AdminInquiry.Create(inquiryId, me.Id, "chat", question,
            message.Content.Length <= 2000 ? message.Content : message.Content[..2000], "ai", json, now));
        message.SavedAs(inquiryId);
        await db.SaveChangesAsync(ct);
        await AuditAsync(db, principal, http, configuration, clock, "inquiry.created", inquiryId.ToString(), "from WordOS AI");
        return Results.Ok(new { id = inquiryId });
    }
}
