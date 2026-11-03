using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Analytics;
using WordOs.Domain.Common;
using WordOs.Domain.Users;
using WordOs.Domain.Words;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Admin;

/// <summary>A table, chart or user list shown under an answer. Never sent to the model.</summary>
public sealed record AgentAttachment(
    string Id, string Kind, string Title,
    IReadOnlyList<string>? Columns = null, IReadOnlyList<IReadOnlyList<string>>? Rows = null,
    ChartSpec? Chart = null, IReadOnlyList<IntelUserRow>? Users = null);

/// <summary>Where a result came from, for the "sources of the analysis" panel.</summary>
public sealed record AgentMeta(string From, string To, int Learners, string Sample);

/// <param name="ModelJson">What the model reads — aggregates only, bounded.</param>
public sealed record AgentToolResult(string ModelJson, AgentMeta? Meta, IReadOnlyList<AgentAttachment> Attachments, string? Error = null);

/// <summary>
/// The analysis tools WordOS AI may ask for (ADR-133).
/// </summary>
/// <remarks>
/// <para>The model never writes a query. It names a tool from this closed list
/// and passes arguments; the backend validates them, runs the same
/// computations the admin pages run (<see cref="IntelMetrics"/>,
/// <see cref="IntelMeasures"/>, …) over an <see cref="IntelScope"/>, and
/// returns aggregates. Read-only by construction: no tool has a write path.</para>
///
/// <para>What reaches the model carries no learner's name, address or words —
/// a user list goes to the model as counts and anonymous evidence, and to the
/// page as an attachment, masked by the viewer's role like every other list.</para>
/// </remarks>
public static class AgentTools
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const int MaxResultChars = 11_000;

    public sealed record ToolDef(string Name, string Description, string Args);

    private const string Filters =
        "days (int, default 30) or from/to (yyyy-MM-dd); level (A1…C2, prefix: B1 covers B1 and B1+); " +
        "segment (cohort key); interest";

    public static readonly IReadOnlyList<ToolDef> Catalogue =
    [
        new("health", "Product health: total/daily/weekly/monthly active learners, active days, sessions, learning minutes, D7 retention — each with the previous period.", Filters),
        new("metrics", "Any metrics from the dictionary for one population, with the previous same-length period and sample sizes. The workhorse for 'how many / what rate'.", "keys (array of dictionary keys, ≤ 12); " + Filters),
        new("compare", "The same dictionary metrics for two groups side by side (e.g. level:B1 vs level:B2, joined:30 vs the rest, frequent vs rare).", "keys (array); a and b: objects with segment and/or level; days"),
        new("skill", "One skill in depth: sessions, completion, abandonment, first-attempt and overall accuracy, pass/fail, retries, answer time — current vs previous — plus failure reasons and a breakdown by learner level.", "skill (reading|listening|speaking|spelling|writing, required); " + Filters),
        new("time", "Time: answer time (mean, median, p90, n) by skill and by content level, session duration, learning minutes, app time, time from opening the app to learning.", "skill (optional); " + Filters),
        new("words", "Words: added, learning, Active, overdue, days to Active (median), the most-failed words (English word only) and where they fail.", "skill (optional); " + Filters),
        new("users", "The learners behind a result: how many and why, by a criterion. The names go to the admin's screen, not to you.", "criterion: a segment key, attention:<reason>, failed:<skill> (failed that skill in the period), abandoned:<skill>; limit (≤ 25); " + Filters),
        new("retention", "Retention D1/D7/D30 (return to the app) and recall (weekly-review first-pass, words remembered) — two different things.", Filters),
        new("notifications", "Reminder journey: planned → opened → lesson started within the window → completed, by kind and hour. Delivery is not knowable.", Filters),
        new("behavior", "UX friction signals: translation use, replays, hints, quick exits, idle answers, errors — each with baseline and affected learners.", Filters),
        new("content_versions", "A skill's results split by the prompt version and model that wrote its content — to compare content before and after a change.", "skill (required); " + Filters),
        new("technical", "AI call count, error rate, latency p50/p95, AI fallback rate, phone-reported API errors.", Filters),
        new("decisions", "Recorded product decisions. Without id: list (filter by area or text). With id: the decision, its targets, and its evaluation — baseline vs after, samples, cautions.", "id (optional); area (optional); query (optional)"),
    ];

    public static string CatalogueText() =>
        string.Join("\n", Catalogue.Select(t => $"- {t.Name}: {t.Description} Args: {t.Args}."));

    /// <summary>The dictionary as the model reads it: keys, meaning, limits, and the cohort vocabulary.</summary>
    public static string DictionaryText()
    {
        var lines = IntelMeasures.Catalogue.Select(m =>
            $"- {m.Key} ({m.Format}, {(m.GoodWhenUp ? "higher is better" : "lower is better")}): {m.Label} — {m.Definition}"
            + (m.Limitation is null ? "" : $" Limit: {m.Limitation}"));
        var segments = string.Join(", ", Segments.Catalogue.Where(s => s.Key != "all").Select(s => s.Key))
                       + ", level:<A1…C2>, interest:<name>, funnel:<stage>, attention:<reason>";
        var attention = string.Join(", ", Attention.Catalogue.Select(a => a.Key));
        var missing = string.Join("; ", EventDictionary.BriefCoverage
            .Where(b => b.Status is "partial" or "missing").Select(b => $"{b.Requested}: {b.Note}"));
        return string.Join("\n", lines)
               + $"\n\nCohort keys: {segments}\nAttention reasons: {attention}\nNot fully recorded: {missing}"
               + "\nPhone events (app opens, app time, translations, replays, hints) exist only from app builds that carry tracking.";
    }

    // ── Arguments ───────────────────────────────────────────────────────────

    private sealed class Args
    {
        public int? Days { get; set; }
        public string? From { get; set; }
        public string? To { get; set; }
        public string? Level { get; set; }
        public string? Segment { get; set; }
        public string? Interest { get; set; }
        public string? Skill { get; set; }
        public List<string>? Keys { get; set; }
        public Group? A { get; set; }
        public Group? B { get; set; }
        public string? Criterion { get; set; }
        public int? Limit { get; set; }
        public Guid? Id { get; set; }
        public string? Area { get; set; }
        public string? Query { get; set; }

        public IntelFilter Filter(string? segment = null, string? level = null) => new(
            From: From, To: To, Days: From is null ? Math.Clamp(Days ?? 30, 1, 3650) : null,
            Level: level ?? Level, Segment: segment ?? Segment, Interest: Interest);
    }

    private sealed record Group(string? Segment, string? Level);

    /// <summary>The key a cached result is found under: tool plus normalised arguments.</summary>
    public static string CacheKey(string tool, string argsJson)
    {
        try
        {
            var node = JsonNode.Parse(argsJson) as JsonObject ?? [];
            var sorted = new JsonObject(node
                .Select(p => KeyValuePair.Create(p.Key.ToLowerInvariant(), p.Value?.DeepClone()))
                .DistinctBy(p => p.Key)
                .OrderBy(p => p.Key, StringComparer.Ordinal));
            return $"{tool}:{sorted.ToJsonString()}";
        }
        catch (JsonException)
        {
            return $"{tool}:{argsJson}";
        }
    }

    public static bool IsKnown(string tool) => Catalogue.Any(t => t.Name == tool);

    // ── Execution ───────────────────────────────────────────────────────────

    public static async Task<AgentToolResult> RunAsync(
        string tool, string argsJson, IntelDataset data, WordOsDbContext db, Privacy privacy, string idPrefix,
        CancellationToken ct)
    {
        Args args;
        try
        {
            args = JsonSerializer.Deserialize<Args>(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson, Web) ?? new Args();
        }
        catch (JsonException)
        {
            return Error("The arguments were not a JSON object.");
        }

        try
        {
            return tool switch
            {
                "health" => Health(data, args),
                "metrics" => Metrics(data, args, idPrefix),
                "compare" => Compare(data, args, idPrefix),
                "skill" => Skill(data, args, idPrefix),
                "time" => Time(data, args),
                "words" => Words(data, args, idPrefix),
                "users" => Users(data, args, privacy, idPrefix),
                "retention" => Retention(data, args),
                "notifications" => Notifications(data, args, privacy),
                "behavior" => Behavior(data, args, idPrefix),
                "content_versions" => ContentVersions(data, args, idPrefix),
                "technical" => Technical(data, args),
                "decisions" => await DecisionsAsync(data, db, args, ct),
                _ => Error($"There is no tool '{tool}'. Use one of: {string.Join(", ", Catalogue.Select(t => t.Name))}."),
            };
        }
        catch (ArgumentException e)
        {
            return Error(e.Message);
        }
    }

    private static AgentToolResult Error(string message) =>
        new(JsonSerializer.Serialize(new { error = message }, Web), null, [], message);

    private static AgentToolResult Done(object model, IntelScope scope, string sample, params AgentAttachment[] attachments)
    {
        var json = JsonSerializer.Serialize(model, Web);
        if (json.Length > MaxResultChars) json = json[..MaxResultChars] + "…(truncated)";
        return new(json, MetaOf(scope, sample), attachments);
    }

    private static AgentMeta MetaOf(IntelScope s, string sample) => new(
        s.Data.Day(s.Period.From).ToString("yyyy-MM-dd"),
        s.Data.Day(s.Period.To.AddTicks(-1)).ToString("yyyy-MM-dd"),
        s.Learners.Count, sample);

    private static object Period(IntelScope s) => new
    {
        from = s.Data.Day(s.Period.From).ToString("yyyy-MM-dd"),
        to = s.Data.Day(s.Period.To.AddTicks(-1)).ToString("yyyy-MM-dd"),
        previous = new
        {
            from = s.Data.Day(s.Period.PrevFrom).ToString("yyyy-MM-dd"),
            to = s.Data.Day(s.Period.PrevTo.AddTicks(-1)).ToString("yyyy-MM-dd"),
        },
        learnersInScope = s.Learners.Count,
        filters = new { s.Filter.Level, s.Filter.Segment, s.Filter.Interest },
    };

    private static IntelScope Previous(IntelScope s) =>
        new(s.Data, s.Filter with
        {
            From = s.Data.Day(s.Period.PrevFrom).ToString("yyyy-MM-dd"),
            To = s.Data.Day(s.Period.PrevTo.AddTicks(-1)).ToString("yyyy-MM-dd"),
            Days = null,
        });

    private static double? R(double? v) => v is { } x ? Math.Round(x, 4) : null;

    private static SkillType ParseSkill(string? skill) =>
        Enum.TryParse<SkillType>(skill, true, out var s) && Enum.IsDefined(s)
            ? s
            : throw new ArgumentException("skill must be one of reading, listening, speaking, spelling, writing.");

    private static string Fmt(double? v, string format) => formatValue(v, format);

    private static string formatValue(double? v, string format) => v switch
    {
        null => "—",
        { } x when format == "pct" => $"{x * 100:0.#}%",
        { } x when format == "ms" => x >= 60_000 ? $"{x / 60_000:0.#} د" : $"{x / 1000:0.#} ث",
        { } x => x.ToString("0.##", CultureInfo.InvariantCulture),
    };

    // ── Tools ───────────────────────────────────────────────────────────────

    private static AgentToolResult Health(IntelDataset data, Args a)
    {
        var s = new IntelScope(data, a.Filter());
        var kpis = IntelMetrics.Health(s);
        return Done(new
        {
            period = Period(s),
            kpis = kpis.Select(k => new { k.Key, k.Label, value = R(k.Value), previous = R(k.Previous), k.Format, note = k.Hint }),
        }, s, $"{s.Learners.Count} متعلمًا");
    }

    private static AgentToolResult Metrics(IntelDataset data, Args a, string id)
    {
        var keys = (a.Keys ?? []).Distinct().Take(12).ToList();
        if (keys.Count == 0) throw new ArgumentException("keys is required: an array of dictionary metric keys.");
        var unknown = keys.Where(k => IntelMeasures.Find(k) is null).ToList();
        var s = new IntelScope(data, a.Filter());
        var prev = Previous(s);
        var rows = keys.Where(k => IntelMeasures.Find(k) is not null).Select(k =>
        {
            var def = IntelMeasures.Find(k)!;
            var now = def.Compute(s);
            var before = def.Compute(prev);
            return new { key = k, def.Label, def.Format, value = R(now.Value), sample = now.Sample, previous = R(before.Value), previousSample = before.Sample, sampleUnit = def.SampleUnit };
        }).ToList();
        var table = new AgentAttachment($"{id}t", "table", "المقاييس",
            ["المقياس", "الحالية", "السابقة", "العينة"],
            rows.Select(r => (IReadOnlyList<string>)[r.Label, Fmt(r.value, r.Format), Fmt(r.previous, r.Format), $"{r.sample} {r.sampleUnit}"]).ToList());
        return Done(new { period = Period(s), metrics = rows, unknownKeys = unknown }, s,
            $"{s.Learners.Count} متعلمًا", table);
    }

    private static AgentToolResult Compare(IntelDataset data, Args a, string id)
    {
        var keys = (a.Keys ?? []).Distinct().Where(k => IntelMeasures.Find(k) is not null).Take(10).ToList();
        if (keys.Count == 0) throw new ArgumentException("keys is required: dictionary metric keys to compare.");
        if (a.A is null || a.B is null) throw new ArgumentException("a and b are required: {segment?, level?} each.");
        var sa = new IntelScope(data, a.Filter(a.A.Segment ?? "", a.A.Level ?? ""));
        var sb = new IntelScope(data, a.Filter(a.B.Segment ?? "", a.B.Level ?? ""));
        string Label(Group g) => string.Join(" · ", new[] { g.Segment is { Length: > 0 } x ? Segments.LabelOf(x) : null, g.Level is { Length: > 0 } l ? $"المستوى {l}" : null }.OfType<string>()) is { Length: > 0 } t ? t : "كل المتعلمين";
        var rows = keys.Select(k =>
        {
            var def = IntelMeasures.Find(k)!;
            var va = def.Compute(sa);
            var vb = def.Compute(sb);
            return new { key = k, def.Label, def.Format, a = R(va.Value), aSample = va.Sample, b = R(vb.Value), bSample = vb.Sample };
        }).ToList();
        var la = Label(a.A);
        var lb = Label(a.B);
        var table = new AgentAttachment($"{id}t", "table", $"{la} مقابل {lb}",
            ["المقياس", la, lb],
            rows.Select(r => (IReadOnlyList<string>)[r.Label, $"{Fmt(r.a, r.Format)} (n={r.aSample})", $"{Fmt(r.b, r.Format)} (n={r.bSample})"]).ToList());
        var pct = rows.Where(r => r.Format == "pct").ToList();
        var chart = pct.Count == 0 ? null : new AgentAttachment($"{id}c", "chart", $"{la} مقابل {lb}", Chart: new ChartSpec(
            $"{id}c", "bar", $"{la} مقابل {lb}", "pct", pct.Select(r => r.Label).ToList(),
            [new ChartSeries(la, pct.Select(r => r.a).ToList()), new ChartSeries(lb, pct.Select(r => r.b).ToList())]));
        return Done(new
        {
            period = Period(sa),
            a = new { label = la, learners = sa.Learners.Count },
            b = new { label = lb, learners = sb.Learners.Count },
            metrics = rows,
            note = "Groups can differ in more than the dimension compared (level, tenure, activity); a gap is not a cause.",
        }, sa, $"{sa.Learners.Count} مقابل {sb.Learners.Count} متعلمًا", chart is null ? [table] : [table, chart]);
    }

    private static readonly string[] Bands = ["A1", "A2", "B1", "B2", "C1", "C2"];

    private static object Summary(SkillSummary x) => new
    {
        x.Sessions, x.Completed, x.Abandoned, x.Questions, x.Attempts, x.Passed, x.Failed,
        firstAttemptAccuracy = R(x.FirstAttemptAccuracy), overallAccuracy = R(x.OverallAccuracy),
        passRate = R(x.PassRate), retryRate = R(x.RetryRate), abandonmentRate = R(x.AbandonmentRate),
        medianSessionMs = R(x.MedianSessionMs), medianAnswerMs = R(x.MedianAnswerMs),
    };

    private static AgentToolResult Skill(IntelDataset data, Args a, string id)
    {
        var skill = ParseSkill(a.Skill);
        var s = new IntelScope(data, a.Filter());
        var now = IntelMetrics.SkillSummaryFor(s, skill);
        var prev = IntelMetrics.SkillSummaryFor(s, skill, previous: true);
        var failures = IntelMetrics.FailuresFor(s, skill);
        var byLevel = (a.Level is null ? Bands : []).Select(b =>
        {
            var ls = new IntelScope(data, a.Filter(level: b));
            var x = IntelMetrics.SkillSummaryFor(ls, skill);
            return new { level = b, learners = ls.Learners.Count, x.Sessions, abandonmentRate = R(x.AbandonmentRate), firstAttemptAccuracy = R(x.FirstAttemptAccuracy), questions = x.Questions, medianAnswerMs = R(x.MedianAnswerMs) };
        }).Where(l => l.Sessions > 0 || l.questions > 0).ToList();

        var attachments = new List<AgentAttachment>();
        if (byLevel.Count > 1)
            attachments.Add(new($"{id}c", "chart", $"{skill} حسب المستوى", Chart: new ChartSpec($"{id}c", "bar", $"{skill} حسب المستوى", "pct",
                byLevel.Select(l => l.level).ToList(),
                [new ChartSeries("دقة المحاولة الأولى", byLevel.Select(l => l.firstAttemptAccuracy).ToList()),
                 new ChartSeries("الانسحاب", byLevel.Select(l => l.abandonmentRate).ToList())])));
        if (failures.Reasons.Count > 0)
            attachments.Add(new($"{id}t", "table", $"أسباب الفشل في {skill}", ["السبب", "النوع", "الحالات"],
                failures.Reasons.Select(r => (IReadOnlyList<string>)[r.Label, IntelMetrics.CategoryLabels.GetValueOrDefault(r.Category, r.Category), r.Count.ToString()]).ToList()));

        return Done(new
        {
            period = Period(s),
            skill = skill.ToString(),
            current = Summary(now),
            previous = Summary(prev),
            failureReasons = failures.Reasons.Select(r => new { r.Label, r.Category, r.Count }),
            byLearnerLevel = byLevel,
            notes = "Abandonment counts sessions left open past the threshold plus explicit abandons; pass rate is per word decision, not per question. "
                    + "failureReasons explain why answers or words FAILED — they are not reasons for abandoning a session; nothing records why a learner left. "
                    + "To look at abandonment, compare it by level and content version, and check behavior (quick exits, idle answers).",
        }, s, $"{now.Sessions} تمرينًا · {now.Questions} سؤالًا", attachments.ToArray());
    }

    private static object Stats(IEnumerable<double> values)
    {
        var list = values.ToList();
        return new
        {
            n = list.Count,
            meanMs = R(IntelMetrics.Mean(list)),
            medianMs = R(IntelMetrics.Median(list)),
            p90Ms = R(IntelMetrics.Percentile(list, 0.9)),
        };
    }

    private static readonly string[] AnswerEvents =
        [AnalyticsEventNames.AnswerSubmitted, AnalyticsEventNames.WritingEvaluated, AnalyticsEventNames.SpeakingTurn];

    private static AgentToolResult Time(IntelDataset data, Args a)
    {
        var s = new IntelScope(data, a.Filter());
        SkillType? only = a.Skill is null ? null : ParseSkill(a.Skill);
        var answers = s.Events.Where(e => s.Period.Contains(e.OccurredAt) && e.DurationMs is not null
                                          && AnswerEvents.Contains(e.Name) && (only is null || e.Skill == only)).ToList();
        var screen = IntelMetrics.ScreenTime(s.Events);
        var sessions = s.SessionsIn().Where(x => !x.IsPractice && (only is null || x.Skill == only)).ToList();
        var appMs = s.Events.Where(e => e.Name == AnalyticsEventNames.AppBackgrounded && s.Period.Contains(e.OccurredAt))
            .Sum(e => (double)(e.DurationMs ?? 0));
        return Done(new
        {
            period = Period(s),
            answerTime = Stats(answers.Select(e => (double)e.DurationMs!.Value)),
            answerTimeBySkill = answers.GroupBy(e => e.Skill?.ToString() ?? "—").ToDictionary(g => g.Key, g => Stats(g.Select(e => (double)e.DurationMs!.Value))),
            answerTimeByContentLevel = answers.Where(e => e.ContentLevel != null).GroupBy(e => e.ContentLevel!)
                .ToDictionary(g => g.Key, g => Stats(g.Select(e => (double)e.DurationMs!.Value))),
            sessionDuration = Stats(sessions.Select(data.DurationMs).OfType<double>()),
            learningMinutes = Math.Round(sessions.Select(x => IntelMetrics.LearningMs(data, x, screen)).OfType<double>().Sum() / 60_000, 1),
            appMinutes = appMs > 0 ? Math.Round(appMs / 60_000, 1) : (double?)null,
            notes = "Answer time = from the question appearing to the answer being sent (elapsedMs), only from app builds that send it. Content level is the level of the material, which can differ from the learner's level. Sessions over the cap are left out of durations.",
        }, s, $"{answers.Count} إجابة · {sessions.Count} تمرينًا");
    }

    private static AgentToolResult Words(IntelDataset data, Args a, string id)
    {
        var s = new IntelScope(data, a.Filter());
        SkillType? only = a.Skill is null ? null : ParseSkill(a.Skill);
        var life = IntelMetrics.LifecycleFor(s);
        var failed = AdminIntelEndpoints.MostFailedWords(s, only, 12);
        var toActive = IntelMeasures.Find("days_to_active")!.Compute(s);
        var table = new AgentAttachment($"{id}t", "table", "أكثر الكلمات فشلًا", ["الكلمة", "مرات الفشل", "مستخدمون", "أين"],
            failed.Select(w => (IReadOnlyList<string>)[w.Text, w.Failures.ToString(), w.Users.ToString(),
                string.Join("، ", w.Skills.Select(k => $"{k.Skill} {k.Count}"))]).ToList());
        return Done(new
        {
            period = Period(s),
            lifecycle = new { life.Added, life.Studied, life.Learning, life.Mature, life.Active, life.Archived, life.Deleted, life.Due, life.Overdue },
            waitingBySkill = life.ByStage,
            medianDaysToActive = toActive.Value, wordsReachedActive = toActive.Sample,
            mostFailed = failed.Select(w => new { word = w.Text, w.Failures, w.Users, bySkill = w.Skills }),
            notes = "Learning/Active/Due/Overdue are the state now; added and failures are within the period.",
        }, s, $"{life.Added} كلمة أُضيفت", table);
    }

    private static AgentToolResult Users(IntelDataset data, Args a, Privacy privacy, string id)
    {
        var s = new IntelScope(data, a.Filter());
        var criterion = (a.Criterion ?? "").Trim();
        if (criterion.Length == 0) throw new ArgumentException("criterion is required.");
        var limit = Math.Clamp(a.Limit ?? 15, 1, 25);
        var segments = new Segments(data);

        IEnumerable<(User U, string Why)> Matches()
        {
            if (criterion.StartsWith("attention:", StringComparison.Ordinal))
            {
                var key = criterion["attention:".Length..];
                foreach (var u in s.Learners)
                    if (Attention.Reasons(data, u).FirstOrDefault(r => r.Key == key) is { } r) yield return (u, r.Evidence);
                yield break;
            }
            if (criterion.StartsWith("failed:", StringComparison.Ordinal) || criterion.StartsWith("abandoned:", StringComparison.Ordinal))
            {
                var abandoned = criterion.StartsWith("abandoned:", StringComparison.Ordinal);
                var skill = ParseSkill(criterion[(criterion.IndexOf(':') + 1)..]);
                foreach (var u in s.Learners)
                {
                    var n = abandoned
                        ? s.Events.Count(e => e.UserId == u.Id && e.Name == AnalyticsEventNames.SessionAbandoned && e.Skill == skill && s.Period.Contains(e.OccurredAt))
                          + s.Sessions.Count(x => x.UserId == u.Id && x.Skill == skill && s.Period.Contains(x.StartedAt) && data.IsStale(x))
                        : s.Words.Where(w => w.UserId == u.Id).SelectMany(w => w.Events)
                            .Count(e => e.Type == WordEventType.SkillFailed && e.Skill == skill && s.Period.Contains(e.CreatedAt));
                    if (n > 0) yield return (u, abandoned ? $"انسحب من {skill} {n} مرات" : $"فشل في {skill} {n} مرات");
                }
                yield break;
            }
            foreach (var u in s.Learners.Where(u => segments.Matches(u, criterion)))
                yield return (u, Segments.LabelOf(criterion));
        }

        var all = Matches().ToList();
        var shown = all.OrderByDescending(m => m.Why.Length).Take(limit).ToList();
        var rows = shown.Select(m => UserRows.For(data, m.U, privacy, m.Why)).ToList();
        var attachment = new AgentAttachment($"{id}u", "users", $"المستخدمون — {criterion}", Users: rows);
        return Done(new
        {
            period = Period(s),
            criterion,
            total = all.Count,
            listedOnAdminScreen = rows.Count,
            // Anonymous: the model reasons about the pattern, the admin sees the people.
            learners = shown.Select((m, i) => new
            {
                @ref = $"U{i + 1}",
                level = IntelDataset.LevelOf(m.U)?.ToWire(),
                joinedDaysAgo = (int)(data.Now - m.U.CreatedAt).TotalDays,
                evidence = m.Why,
            }),
            note = "Names and contact details are shown to the admin under your answer, never to you. Refer to them as the list below.",
        }, s, $"{all.Count} متعلمًا", attachment);
    }

    private static AgentToolResult Retention(IntelDataset data, Args a)
    {
        var s = new IntelScope(data, a.Filter());
        double? M(string key) => IntelMeasures.Find(key)!.Compute(s).Value;
        var recall = IntelMeasures.Find("review_first_pass")!.Compute(s);
        return Done(new
        {
            period = Period(s),
            returnToApp = new { d1 = R(M("retention_d1")), d7 = R(M("retention_d7")), d30 = R(M("retention_d30")) },
            previousD7 = R(IntelMetrics.RetentionAt(s, 7, s.Period.PrevTo)),
            recall = new { weeklyReviewFirstPass = R(recall.Value), words = recall.Sample },
            cohorts = IntelMetrics.Cohorts(s, 6),
            notes = "Retention D-n = of learners who joined at least n days before the period's end, the share active on day n or later. Coming back to the app is not remembering words — recall is the review's first-pass rate.",
        }, s, $"{s.Learners.Count} متعلمًا · {recall.Sample} كلمة في المراجعات");
    }

    private static AgentToolResult Notifications(IntelDataset data, Args a, Privacy privacy)
    {
        var s = new IntelScope(data, a.Filter());
        var node = JsonSerializer.SerializeToNode(IntelNotifications.For(s, privacy, Period(s)), Web)!.AsObject();
        var users = node["users"]?.AsArray().Count ?? 0;
        node.Remove("users");
        node["learnersWhoOpenAndDoNotLearn"] = users;
        node.Remove("trend");
        return Done(node, s, $"{s.Learners.Count} متعلمًا");
    }

    private static AgentToolResult Behavior(IntelDataset data, Args a, string id)
    {
        var s = new IntelScope(data, a.Filter());
        var items = IntelMetrics.Friction(s);
        var table = new AgentAttachment($"{id}t", "table", "نقاط الاحتكاك", ["الإشارة", "القيمة", "خط الأساس", "متأثرون"],
            items.Select(f => (IReadOnlyList<string>)[f.Title, Fmt(f.Value, f.Format), Fmt(f.Baseline, f.Format), f.AffectedUsers.ToString()]).ToList());
        return Done(new
        {
            period = Period(s),
            phoneEventsPresent = s.Events.Any(e => e.Source == AnalyticsSource.Client),
            friction = items.Select(f => new { f.Key, f.Title, f.Description, f.Skill, value = R(f.Value), f.Format, baseline = R(f.Baseline), f.AffectedUsers, f.Severity, f.Category }),
        }, s, $"{s.Learners.Count} متعلمًا", table);
    }

    private static AgentToolResult ContentVersions(IntelDataset data, Args a, string id)
    {
        var skill = ParseSkill(a.Skill);
        var s = new IntelScope(data, a.Filter());
        var rows = IntelMetrics.ByContentVersion(s, skill);
        var json = JsonSerializer.SerializeToNode(rows, Web)!.AsArray();
        var table = new AgentAttachment($"{id}t", "table", $"{skill} حسب نسخة المحتوى", ["النسخة", "تمارين", "إكمال", "دقة أول محاولة"],
            json.Select(r => (IReadOnlyList<string>)[
                r!["version"]!.GetValue<string>(), r["sessions"]!.ToString(),
                Fmt(r["completion"]?.GetValue<double?>(), "pct"), Fmt(r["firstAttemptAccuracy"]?.GetValue<double?>(), "pct")]).ToList());
        return Done(new { period = Period(s), skill = skill.ToString(), versions = json }, s, $"{json.Count} نسخة", table);
    }

    private static AgentToolResult Technical(IntelDataset data, Args a)
    {
        var s = new IntelScope(data, a.Filter());
        var calls = s.Events.Where(e => e.Name == AnalyticsEventNames.AiCall && s.Period.Contains(e.OccurredAt)).ToList();
        var durations = calls.Where(c => c.DurationMs != null).Select(c => (double)c.DurationMs!.Value).ToList();
        var sessions = s.SessionsIn().ToList();
        return Done(new
        {
            period = Period(s),
            aiCalls = calls.Count,
            aiErrorRate = R(IntelMetrics.Ratio(calls.Count(c => c.Result == "error"), calls.Count)),
            aiLatencyP50Ms = R(IntelMetrics.Median(durations)),
            aiLatencyP95Ms = R(IntelMetrics.Percentile(durations, 0.95)),
            aiFallbackRate = R(IntelMetrics.Ratio(sessions.Count(x => x.UsedAiFallback), sessions.Count)),
            apiErrorsFromPhones = s.Events.Count(e => e.Name == AnalyticsEventNames.ApiError && s.Period.Contains(e.OccurredAt)),
        }, s, $"{calls.Count} طلب AI");
    }

    private static async Task<AgentToolResult> DecisionsAsync(IntelDataset data, WordOsDbContext db, Args a, CancellationToken ct)
    {
        var scope = new IntelScope(data, new IntelFilter(Days: 30));
        if (a.Id is { } id)
        {
            var d = await db.ProductDecisions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Error("No decision with that id.");
            object? evaluation = null;
            string evaluationKind;
            if (d.EvaluationJson is not null) { evaluation = JsonNode.Parse(d.EvaluationJson); evaluationKind = "frozen when closed"; }
            else if (IntelEvaluation.WindowsOf(d, data.Options.EvaluationWindowDays) is { } w)
            {
                var overlaps = await db.ProductChanges.AsNoTracking()
                    .Where(c => c.ShippedOn >= w.After.From && c.ShippedOn <= w.After.To && c.DecisionId != d.Id)
                    .Select(c => new Overlap(c.Id, c.Title, c.Kind, c.ShippedOn)).ToListAsync(ct);
                evaluation = IntelEvaluation.Evaluate(data, d.Metrics, d.Segment, null, w.Before, w.After, overlaps);
                evaluationKind = "live";
            }
            else evaluationKind = "not implemented yet — nothing to measure";
            return Done(new
            {
                decision = new
                {
                    d.Id, d.Title, d.Area, d.Problem, d.Hypothesis, d.Action, d.ExpectedOutcome, status = d.Status.ToString(),
                    verdict = d.Verdict?.ToString(), d.ImplementedOn, d.ReviewOn, d.BaselineFrom, d.BaselineTo,
                    targets = d.Metrics, d.ActualOutcome,
                },
                evaluationKind,
                evaluation,
            }, scope, "قرار واحد");
        }

        var q = db.ProductDecisions.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(a.Area)) q = q.Where(d => d.Area == a.Area);
        if (!string.IsNullOrWhiteSpace(a.Query))
        {
            var term = $"%{a.Query.Trim()}%";
            q = q.Where(d => EF.Functions.ILike(d.Title, term) || EF.Functions.ILike(d.Problem, term) || EF.Functions.ILike(d.Action, term));
        }
        var list = await q.OrderByDescending(d => d.UpdatedAt).Take(15)
            .Select(d => new { d.Id, d.Title, d.Area, status = d.Status.ToString(), verdict = d.Verdict.ToString(), d.ImplementedOn, d.ReviewOn })
            .ToListAsync(ct);
        return Done(new { decisions = list, note = "Call decisions again with an id to get one decision's evaluation." }, scope, $"{list.Count} قرارًا");
    }
}
