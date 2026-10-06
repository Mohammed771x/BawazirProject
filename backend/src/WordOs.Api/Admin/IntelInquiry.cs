using System.Text.Json;
using System.Text.RegularExpressions;
using WordOs.Application.Abstractions;
using WordOs.Domain.Analytics;
using WordOs.Domain.Common;
using WordOs.Domain.Levels;
using WordOs.Domain.Users;
using WordOs.Domain.Words;

namespace WordOs.Api.Admin;

public sealed record EvidenceItem(string Label, double? Value, string Format, string? Context = null, double? Previous = null);

public sealed record ChartSeries(string Name, IReadOnlyList<double?> Values);

/// <param name="Type"><c>line</c>, <c>bar</c>, <c>funnel</c>, <c>distribution</c>, <c>donut</c>.</param>
/// <param name="Unit"><c>pct</c>, <c>int</c>, <c>num</c>, <c>ms</c>.</param>
public sealed record ChartSpec(string Id, string Type, string Title, string Unit,
    IReadOnlyList<string> Labels, IReadOnlyList<ChartSeries> Series, string? Note = null);

public sealed record WhereItem(string Dimension, string Value, string Detail);

public sealed record ComparisonRow(string Label, double? A, double? B, string Format);

public sealed record Comparison(string Title, string ALabel, string BLabel, IReadOnlyList<ComparisonRow> Rows);

/// <summary>
/// A product investigation (admin brief §26).
/// </summary>
/// <remarks>
/// Three layers, kept apart on the page as here: <see cref="Evidence"/>,
/// <see cref="Charts"/>, <see cref="Where"/> and <see cref="Comparison"/> are
/// <b>data</b>, computed by the backend; <see cref="Interpretation"/> is what
/// the data appears to say; <see cref="Hypotheses"/> and
/// <see cref="Investigate"/> are guesses worth testing. The page never lets a
/// guess be mistaken for a measurement, and nothing here is a decision.
/// </remarks>
public sealed record Investigation(
    Guid Id,
    string Section,
    string SectionLabel,
    string Question,
    DateTimeOffset CreatedAt,
    string? Author,
    string Summary,
    IReadOnlyList<EvidenceItem> Evidence,
    IReadOnlyList<ChartSpec> Charts,
    IReadOnlyList<IntelUserRow> AffectedUsers,
    string AffectedLabel,
    IReadOnlyList<WhereItem> Where,
    Comparison? Comparison,
    IReadOnlyList<string> Interpretation,
    IReadOnlyList<string> Hypotheses,
    IReadOnlyList<InsightLead> Investigate,
    string InterpretedBy,
    string DataNote,
    DateTimeOffset PeriodFrom,
    DateTimeOffset PeriodTo);

/// <summary>
/// "Ask the data" (admin brief §25) — section first, then the question.
/// </summary>
/// <remarks>
/// The backend answers the <i>data</i> half itself, by section, refined by what
/// the question mentions (a skill, a level, "new users", translations,
/// replays). The model is then given those aggregates — never a learner's
/// identity — and asked only to read them. When it cannot be reached the
/// rules below write a plainer reading, and the result says which it was.
/// </remarks>
public static partial class IntelInquiry
{
    public static readonly IReadOnlyList<(string Key, string Label)> Sections =
    [
        ("product_health", "Product Health"),
        ("users", "المستخدمون"),
        ("engagement", "Engagement"),
        ("activation", "Activation"),
        ("words", "الكلمات"),
        ("skills", "Skills"),
        ("reading", "Reading"),
        ("listening", "Listening"),
        ("speaking", "Speaking"),
        ("writing", "Writing"),
        ("spelling", "Spelling"),
        ("level", "Level"),
        ("retention", "Retention"),
        ("weekly_review", "Weekly Review"),
        ("ux", "UX"),
        ("feedback", "Feedback"),
        ("notifications", "Notifications"),
        ("ai", "AI"),
    ];

    private sealed class Builder(IntelScope scope, Privacy privacy)
    {
        public IntelScope Scope { get; } = scope;
        public Privacy Privacy { get; } = privacy;
        public List<EvidenceItem> Evidence { get; } = [];
        public List<ChartSpec> Charts { get; } = [];
        public List<IntelUserRow> Users { get; } = [];
        public string UsersLabel { get; set; } = "المستخدمون المرتبطون";
        public List<WhereItem> Where { get; } = [];
        public Comparison? Comparison { get; set; }
        public List<string> Findings { get; } = [];
        public List<string> Hypotheses { get; } = [];
        public List<InsightLead> Leads { get; } = [];

        public void AddUsers(IEnumerable<User> users, Func<User, string?>? evidence = null)
        {
            foreach (var u in users.Take(Scope.Data.Options.MaxListedUsers))
                if (Users.All(r => r.Id != u.Id))
                    Users.Add(UserRows.For(Scope.Data, u, Privacy, evidence?.Invoke(u)));
        }
    }

    public static async Task<Investigation> InvestigateAsync(
        IntelScope scope, string section, string question, Privacy privacy,
        IAdminInsightService insight, CancellationToken ct)
    {
        var b = new Builder(scope, privacy);
        var mentioned = SkillsIn(question);

        switch (section)
        {
            case "product_health": ProductHealth(b); break;
            case "users":
            case "engagement": Engagement(b); break;
            case "activation": Activation(b); break;
            case "words": Words(b); break;
            case "skills": Skills(b); break;
            case "reading": Skill(b, SkillType.Reading); break;
            case "listening": Skill(b, SkillType.Listening); break;
            case "speaking": Skill(b, SkillType.Speaking); break;
            case "writing": Skill(b, SkillType.Writing); break;
            case "spelling": Skill(b, SkillType.Spelling); break;
            case "level": Levels(b); break;
            case "retention": Retention(b); break;
            case "weekly_review": WeeklyReview(b); break;
            case "ux": Ux(b); break;
            case "feedback": Feedback(b); break;
            case "notifications": Notifications(b); break;
            case "ai": Ai(b); break;
        }

        // What the question itself points at, beyond its section.
        foreach (var skill in mentioned.Where(s => !section.Equals(s.ToString(), StringComparison.OrdinalIgnoreCase)).Take(2))
            Skill(b, skill, light: true);
        if (Mentions(question, "ترجم", "translat")) TranslationVersusAccuracy(b);
        if (Mentions(question, "إعاد", "replay", "اعاد")) ReplayVersusAccuracy(b);
        if (Mentions(question, "جدد", "جديد", "new user", "الجدد")) NewVersusEstablished(b);
        if (Mentions(question, "10 كلمات", "أكثر من", "many words", "يضيفون") && Mentions(question, "إتقان", "mastery", "mastered", "يتقن", "Mastery"))
            WordsVersusMastery(b);
        if (LevelIn(question) is { } level) LevelSlice(b, level, mentioned.Count > 0 ? mentioned[0] : null);

        var sectionLabel = Sections.First(s => s.Key == section).Label;
        var note = DataNote(scope, b);

        // ── Interpretation: the model if it answers, the rules if it does not ─
        var evidenceForModel = JsonSerializer.Serialize(new
        {
            period = new { from = scope.Period.From, to = scope.Period.To, learners = scope.Learners.Count },
            evidence = b.Evidence,
            charts = b.Charts.Select(c => new { c.Id, c.Title, c.Unit, c.Labels, c.Series }),
            where = b.Where,
            comparison = b.Comparison,
            affectedUsers = b.Users.Count,
            ruleFindings = b.Findings,
            dataNote = note,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var ai = await insight.InterpretAsync(new InsightRequest(sectionLabel, question, evidenceForModel), ct);

        var charts = b.Charts;
        if (ai is { Charts.Count: > 0 })
        {
            var chosen = ai.Charts.Select(id => b.Charts.FirstOrDefault(c => c.Id == id)).OfType<ChartSpec>().ToList();
            if (chosen.Count > 0) charts = chosen.Concat(b.Charts.Except(chosen)).ToList();
        }

        var summary = ai?.Summary ?? RuleSummary(b, sectionLabel);
        var interpretation = ai?.Interpretation is { Count: > 0 } i ? i : b.Findings;
        var hypotheses = ai?.Hypotheses is { Count: > 0 } h ? h : b.Hypotheses.Distinct().ToList();
        var leads = ai?.Investigate is { Count: > 0 } l ? l : b.Leads;

        return new Investigation(
            Guid.Empty, section, sectionLabel, question, scope.Data.Now, null,
            summary, b.Evidence, charts, b.Users, b.UsersLabel, b.Where, b.Comparison,
            interpretation, hypotheses, leads,
            ai is null ? "rules" : "ai", note, scope.Period.From, scope.Period.To);
    }

    // ── Sections ─────────────────────────────────────────────────────────────

    private static void ProductHealth(Builder b)
    {
        var health = IntelMetrics.Health(b.Scope);
        foreach (var k in health) b.Evidence.Add(new(k.Label, k.Value, k.Format, k.Hint, k.Previous));
        EngagementChart(b);
        b.Comparison = new Comparison("الفترة الحالية مقابل السابقة", "الحالية", "السابقة",
            health.Select(k => new ComparisonRow(k.Label, k.Value, k.Previous, k.Format)).ToList());

        foreach (var k in health.Where(k => k.Value is not null && k.Previous is > 0))
        {
            var change = (k.Value!.Value - k.Previous!.Value) / k.Previous.Value;
            if (Math.Abs(change) >= 0.15)
                b.Findings.Add($"{k.Label} {(change > 0 ? "ارتفع" : "انخفض")} بنسبة {Math.Abs(change):P0} عن الفترة السابقة.");
        }

        var skills = IntelMetrics.Skills.Select(s => IntelMetrics.SkillSummaryFor(b.Scope, s)).ToList();
        var funnel = IntelMetrics.FunnelFor(b.Scope);
        var friction = IntelMetrics.Friction(b.Scope);
        foreach (var s in IntelMetrics.Signals(b.Scope, skills, funnel, friction).Take(6))
            b.Where.Add(new WhereItem(AreaLabel(s.Area), s.Title, s.Detail));

        b.AddUsers(b.Scope.Learners.Where(u => Attention.Reasons(b.Scope.Data, u).Count > 0),
            u => string.Join(" · ", Attention.Reasons(b.Scope.Data, u).Select(r => Attention.LabelOf(r.Key))));
        b.UsersLabel = "المستخدمون الذين يحتاجون إلى انتباه";
        b.Leads.Add(new("ابدأ بأعلى إشارة في القائمة", "الإشارات مرتبة بالخطورة، وكل واحدة تفتح التحليل الخاص بها."));
    }

    private static void Engagement(Builder b)
    {
        var health = IntelMetrics.Health(b.Scope);
        foreach (var k in health.Where(k => k.Key is "dau" or "wau" or "mau" or "active_days" or "sessions" or "learning_time"))
            b.Evidence.Add(new(k.Label, k.Value, k.Format, k.Hint, k.Previous));
        EngagementChart(b);

        var data = b.Scope.Data;
        var buckets = new (string Label, Func<int, bool> Test)[]
        {
            ("يوم واحد", d => d == 1), ("2–3 أيام", d => d is >= 2 and <= 3), ("4–7 أيام", d => d is >= 4 and <= 7),
            ("8–14 يومًا", d => d is >= 8 and <= 14), ("أكثر من 14", d => d > 14),
        };
        var days = b.Scope.Learners.Select(u => data.ActiveDaysOf(u.Id).Count(d => b.Scope.Period.Contains(data.StartOf(d)))).ToList();
        b.Charts.Add(new("active_days_distribution", "distribution", "توزيع أيام النشاط في الفترة", "int",
            buckets.Select(x => x.Label).ToList(), [new("المستخدمون", buckets.Select(x => (double?)days.Count(x.Test)).ToList())],
            $"{days.Count(d => d == 0)} مستخدمين بلا نشاط في الفترة"));

        NewVersusEstablished(b);

        var inactive = b.Scope.Learners.Where(u => Attention.Reasons(data, u).Any(r => r.Key == "recently_inactive")).ToList();
        b.AddUsers(inactive, u => Attention.Reasons(data, u).First(r => r.Key == "recently_inactive").Evidence);
        b.UsersLabel = "توقفوا مؤخرًا";
        if (inactive.Count > 0)
        {
            b.Findings.Add($"{inactive.Count} مستخدمين كانوا نشطين ثم انقطعوا.");
            b.Leads.Add(new("افتح Timeline آخر يوم لهؤلاء", "ما فعلوه في آخر جلسة قبل الانقطاع هو أقرب دليل على السبب."));
        }
        b.Hypotheses.Add("قد تكون الفجوة بين المهارات (يومان) أطول من أن يبقى المتعلم الجديد مرتبطًا بالتطبيق.");
    }

    private static void Activation(Builder b)
    {
        var funnel = IntelMetrics.FunnelFor(b.Scope);
        b.Charts.Add(new("funnel", "funnel", "Activation Funnel — من سجلوا في الفترة", "int",
            funnel.Select(f => f.Label).ToList(), [new("المستخدمون", funnel.Select(f => (double?)f.Count).ToList())]));
        foreach (var f in funnel) b.Evidence.Add(new(f.Label, f.Count, "int", f.DropOff is { } d ? $"Drop-off {d:P0}" : null));

        var worst = funnel.Where(f => f.DropOff is not null).OrderByDescending(f => f.DropOff).FirstOrDefault();
        if (worst is not null)
        {
            var index = funnel.ToList().FindIndex(f => f.Key == worst.Key);
            var before = funnel[index - 1];
            b.Findings.Add($"أكبر تسرب بين «{before.Label}» و«{worst.Label}»: {worst.DropOff:P0} لم يكملوا.");
            b.Where.Add(new("المرحلة", worst.Label, $"{before.Count - worst.Count} مستخدمين توقفوا قبلها"));
            var stuck = b.Scope.Learners.Where(u => b.Scope.Period.Contains(u.CreatedAt) && Funnel.StageOf(b.Scope.Data, u) == before.Key).ToList();
            b.AddUsers(stuck, _ => $"توقف عند «{before.Label}»");
            b.UsersLabel = $"توقفوا عند «{before.Label}»";
            b.Hypotheses.Add(worst.Key switch
            {
                "onboarding" => "قد يكون اختبار المستوى طويلًا أو مُربكًا لمستخدم لم يرَ قيمة التطبيق بعد.",
                "first_word" => "قد لا يكون واضحًا للمستخدم أن عليه إضافة كلمة ليبدأ التعلم.",
                "first_skill" => "قد تكون الشاشة الرئيسية لا تقود بوضوح إلى أول تمرين.",
                "first_completed" => "قد يكون أول تمرين أطول أو أصعب من المتوقع.",
                "second_session" => "قد لا يوجد سبب واضح للعودة في اليوم التالي — التذكير أو الكلمات المستحقة.",
                "first_mastered" => "الوصول إلى الإتقان يحتاج خمس مهارات بفجوات زمنية؛ قد يتسرب المستخدم قبل أن يرى النتيجة.",
                _ => "قد يحتاج الانتقال إلى هذه المرحلة إلى توضيح أكبر في التجربة.",
            });
            b.Leads.Add(new($"افتح Timeline لعيّنة ممن توقفوا عند «{before.Label}»", "آخر ما فعلوه يوضح أين انقطعت الرحلة."));
        }
    }

    private static void Words(Builder b)
    {
        var life = IntelMetrics.LifecycleFor(b.Scope);
        b.Evidence.AddRange(
        [
            new("أضيفت في الفترة", life.Added, "int"), new("دُرست في الفترة", life.Studied, "int"),
            new("قيد التعلم", life.Learning, "int"), new("متقنة", life.Mature, "int"),
            new("Active", life.Active, "int"), new("مستحقة", life.Due, "int"), new("متأخرة", life.Overdue, "int"),
            new("محذوفة", life.Deleted, "int"),
        ]);
        b.Charts.Add(new("stages", "bar", "الكلمات في كل مرحلة الآن", "int",
            life.ByStage.Select(s => s.Skill).ToList(),
            [
                new("بانتظار الفجوة", life.ByStage.Select(s => (double?)s.Waiting).ToList()),
                new("مستحقة", life.ByStage.Select(s => (double?)s.Due).ToList()),
                new("متأخرة", life.ByStage.Select(s => (double?)s.Overdue).ToList()),
            ]));
        var stuck = life.ByStage.OrderByDescending(s => s.Due + s.Overdue).First();
        if (stuck.Due + stuck.Overdue > 0)
        {
            b.Where.Add(new("المهارة", stuck.Skill, $"{stuck.Due + stuck.Overdue} كلمات تنتظر المتعلم هنا"));
            b.Findings.Add($"أكثر الكلمات تراكمًا عند {stuck.Skill}: {stuck.Due} مستحقة و{stuck.Overdue} متأخرة.");
        }
        WordsVersusMastery(b);
        b.AddUsers(b.Scope.Learners.Where(u => Attention.Reasons(b.Scope.Data, u).Any(r => r.Key is "hoarder" or "high_overdue")),
            u => string.Join(" · ", Attention.Reasons(b.Scope.Data, u).Where(r => r.Key is "hoarder" or "high_overdue").Select(r => r.Evidence)));
        b.UsersLabel = "يضيفون كثيرًا أو لديهم متأخرات";
    }

    private static void Skills(Builder b)
    {
        var skills = IntelMetrics.Skills.Select(s => (cur: IntelMetrics.SkillSummaryFor(b.Scope, s), prev: IntelMetrics.SkillSummaryFor(b.Scope, s, previous: true))).ToList();
        b.Charts.Add(new("skill_accuracy", "bar", "دقة المحاولة الأولى والانسحاب لكل مهارة", "pct",
            skills.Select(s => s.cur.Skill).ToList(),
            [
                new("دقة المحاولة الأولى", skills.Select(s => s.cur.FirstAttemptAccuracy).ToList()),
                new("Pass Rate", skills.Select(s => s.cur.PassRate).ToList()),
                new("الانسحاب", skills.Select(s => s.cur.AbandonmentRate).ToList()),
            ]));
        b.Comparison = new Comparison("دقة المحاولة الأولى: الحالية مقابل السابقة", "الحالية", "السابقة",
            skills.Select(s => new ComparisonRow(s.cur.Skill, s.cur.FirstAttemptAccuracy, s.prev.FirstAttemptAccuracy, "pct")).ToList());
        foreach (var (cur, _) in skills)
            b.Evidence.Add(new($"{cur.Skill} — Sessions", cur.Sessions, "int", $"دقة {Pct(cur.FirstAttemptAccuracy)} · انسحاب {Pct(cur.AbandonmentRate)}"));

        var failures = IntelMetrics.Skills.Select(s => IntelMetrics.FailuresFor(b.Scope, s)).ToList();
        var categories = IntelMetrics.Categories(failures);
        if (categories.Count > 0)
        {
            b.Charts.Add(new("failure_categories", "donut", "طبيعة الفشل عبر المهارات", "int",
                categories.Select(c => c.Label).ToList(), [new("حالات", categories.Select(c => (double?)c.Count).ToList())]));
            b.Findings.Add($"أغلب حالات الفشل من نوع «{categories[0].Label}» ({categories[0].Count}).");
            b.Hypotheses.Add(CategoryHypothesis(categories[0].Category));
        }

        var worst = skills.Where(s => s.cur.Questions >= 5 && s.cur.FirstAttemptAccuracy is not null)
            .OrderBy(s => s.cur.FirstAttemptAccuracy).FirstOrDefault();
        if (worst.cur is not null)
        {
            b.Where.Add(new("المهارة", worst.cur.Skill, $"أدنى دقة محاولة أولى: {Pct(worst.cur.FirstAttemptAccuracy)}"));
            b.Findings.Add($"{worst.cur.Skill} هي الأصعب حاليًا: دقة المحاولة الأولى {Pct(worst.cur.FirstAttemptAccuracy)}.");
            var skill = Enum.Parse<SkillType>(worst.cur.Skill, ignoreCase: true);
            var segments = new Segments(b.Scope.Data);
            b.AddUsers(b.Scope.Learners.Where(u => segments.Matches(u, $"weak:{skill}")),
                u => { var (f, d) = segments.FirstAttempt(u.Id, skill); return $"{f} من {d} من أول محاولة"; });
            b.UsersLabel = $"ضعف في {worst.cur.Skill}";
            b.Leads.Add(new($"افتح تحليل {worst.cur.Skill}", "توزيع أسباب الفشل حسب المستوى والكلمة يوضح إن كانت المشكلة في المحتوى أم في التعلم."));
        }
    }

    private static void Skill(Builder b, SkillType skill, bool light = false)
    {
        var scope = b.Scope;
        var cur = IntelMetrics.SkillSummaryFor(scope, skill);
        var prev = IntelMetrics.SkillSummaryFor(scope, skill, previous: true);
        var name = skill.ToString();

        b.Evidence.Add(new($"{name} — دقة المحاولة الأولى", cur.FirstAttemptAccuracy, "pct", null, prev.FirstAttemptAccuracy));
        b.Evidence.Add(new($"{name} — الانسحاب", cur.AbandonmentRate, "pct", $"{cur.Abandoned} من {cur.Sessions}", prev.AbandonmentRate));
        if (!light)
        {
            b.Evidence.Add(new($"{name} — Pass Rate", cur.PassRate, "pct", $"{cur.Passed} نجاح · {cur.Failed} فشل", prev.PassRate));
            b.Evidence.Add(new($"{name} — Retry", cur.RetryRate, "pct", null, prev.RetryRate));
            b.Evidence.Add(new($"{name} — وسيط مدة الـSession", cur.MedianSessionMs, "ms", null, prev.MedianSessionMs));
            foreach (var m in IntelMetrics.ContentFor(scope, skill).Where(m => m.Value is not null).Take(6))
                b.Evidence.Add(new(m.Label, m.Value, m.Format, m.Hint));

            b.Comparison = new Comparison($"{name}: الحالية مقابل السابقة", "الحالية", "السابقة",
            [
                new("Sessions", cur.Sessions, prev.Sessions, "int"),
                new("دقة المحاولة الأولى", cur.FirstAttemptAccuracy, prev.FirstAttemptAccuracy, "pct"),
                new("Overall Accuracy", cur.OverallAccuracy, prev.OverallAccuracy, "pct"),
                new("Pass Rate", cur.PassRate, prev.PassRate, "pct"),
                new("Retry", cur.RetryRate, prev.RetryRate, "pct"),
                new("الانسحاب", cur.AbandonmentRate, prev.AbandonmentRate, "pct"),
                new("وسيط مدة الـSession", cur.MedianSessionMs, prev.MedianSessionMs, "ms"),
            ]);
        }

        var failures = IntelMetrics.FailuresFor(scope, skill);
        if (failures.Reasons.Count > 0)
        {
            b.Charts.Add(new($"failures_{name.ToLowerInvariant()}", "bar", $"أسباب الفشل في {name}", "int",
                failures.Reasons.Select(r => r.Label).ToList(), [new("حالات", failures.Reasons.Select(r => (double?)r.Count).ToList())]));
            var top = failures.Reasons[0];
            b.Findings.Add($"أكثر أسباب الفشل في {name}: «{top.Label}» ({top.Count} من {failures.Total}).");
            b.Where.Add(new("السبب", top.Label, IntelMetrics.CategoryLabels[top.Category]));
            b.Hypotheses.Add(CategoryHypothesis(top.Category));
        }

        // Where: by content level.
        var sessions = scope.SessionsIn().Where(s => s.Skill == skill && !s.IsPractice).ToList();
        if (skill != SkillType.Spelling && sessions.Count > 0)
        {
            var byLevel = sessions.GroupBy(s => s.LevelUsed).OrderBy(g => g.Key).Select(g =>
            {
                var items = g.SelectMany(s => s.Items).Where(i => i.FirstAttemptCorrect != null).ToList();
                return (level: g.Key.ToWire(), accuracy: IntelMetrics.Ratio(items.Count(i => i.FirstAttemptCorrect == true), items.Count), count: g.Count());
            }).ToList();
            b.Charts.Add(new($"level_{name.ToLowerInvariant()}", "bar", $"دقة المحاولة الأولى حسب مستوى المحتوى — {name}", "pct",
                byLevel.Select(x => x.level).ToList(), [new("الدقة", byLevel.Select(x => x.accuracy).ToList())],
                string.Join(" · ", byLevel.Select(x => $"{x.level}: {x.count} Sessions"))));
            var hardest = byLevel.Where(x => x.count >= 2 && x.accuracy is not null).OrderBy(x => x.accuracy).FirstOrDefault();
            if (hardest.level is not null && byLevel.Count > 1)
            {
                b.Where.Add(new("المستوى", hardest.level, $"أدنى دقة: {Pct(hardest.accuracy)} في {hardest.count} Sessions"));
                b.Findings.Add($"في {name}، أدنى دقة عند مستوى {hardest.level} ({Pct(hardest.accuracy)}).");
            }
        }

        if (light) return;

        // When: daily trend.
        var data = scope.Data;
        var labels = new List<string>();
        var passRate = new List<double?>();
        for (var d = data.Day(scope.Period.From); d <= data.Day(scope.Period.To.AddTicks(-1)); d = d.AddDays(1))
        {
            var day = d;
            var decisions = scope.Words.SelectMany(w => w.Events)
                .Where(e => e.Skill == skill && data.Day(e.CreatedAt) == day
                            && e.Type is WordEventType.SkillPassed or WordEventType.SkillFailed).ToList();
            labels.Add(day.ToString("MM-dd"));
            passRate.Add(IntelMetrics.Ratio(decisions.Count(e => e.Type == WordEventType.SkillPassed), decisions.Count));
        }
        b.Charts.Add(new($"trend_{name.ToLowerInvariant()}", "line", $"Pass Rate اليومي — {name}", "pct", labels, [new("Pass Rate", passRate)]));

        // Who.
        var failedBy = scope.Words.SelectMany(w => w.Events.Select(e => (w, e)))
            .Where(x => x.e.Skill == skill && x.e.Type == WordEventType.SkillFailed && scope.Period.Contains(x.e.CreatedAt))
            .GroupBy(x => x.w.UserId).OrderByDescending(g => g.Count()).ToList();
        b.AddUsers(failedBy.Select(g => data.LearnerById(g.Key)).OfType<User>(),
            u => $"{failedBy.First(g => g.Key == u.Id).Count()} حالات فشل في {name}");
        b.UsersLabel = $"فشلوا في {name}";

        var abandonedUsers = scope.Events.Where(e => e.Name == AnalyticsEventNames.SessionAbandoned && e.Skill == skill && scope.Period.Contains(e.OccurredAt))
            .Select(e => e.UserId).Distinct().Select(data.LearnerById).OfType<User>().ToList();
        b.AddUsers(abandonedUsers, _ => $"انسحب من {name}");

        if (cur.FirstAttemptAccuracy is { } a && prev.FirstAttemptAccuracy is { } p && Math.Abs(a - p) >= 0.05)
            b.Findings.Add($"دقة المحاولة الأولى في {name} {(a < p ? "انخفضت" : "ارتفعت")} من {p:P0} إلى {a:P0}.");
        if (cur.AbandonmentRate is >= 0.25)
            b.Leads.Add(new($"راجع Timeline من انسحبوا من {name}", "هل انسحبوا بعد خطأ، أم بعد تحميل طويل، أم في بداية التمرين؟"));
        b.Leads.Add(new($"قارن {name} حسب المستوى", "إذا تركّز الفشل في مستوى واحد فالمحتوى غالبًا أصعب من المستوى المعلن."));
    }

    private static void Levels(Builder b)
    {
        var (flows, bySkill, byLevel, _) = IntelMetrics.Levels(b.Scope);
        b.Charts.Add(new("level_by_skill", "bar", "رفع وخفض المستوى حسب المهارة", "int",
            bySkill.Select(s => s.Skill).ToList(),
            [new("رفع", bySkill.Select(s => (double?)s.Up).ToList()), new("خفض", bySkill.Select(s => (double?)s.Down).ToList())]));
        if (byLevel.Count > 0)
            b.Charts.Add(new("downgrades_by_level", "bar", "من أي مستوى يخفّض المتعلمون؟", "int",
                byLevel.Select(l => l.Level).ToList(),
                [new("خفض", byLevel.Select(l => (double?)l.Downgrades).ToList()), new("رفع", byLevel.Select(l => (double?)l.Upgrades).ToList())]));
        foreach (var f in flows.Take(8))
            b.Evidence.Add(new($"{f.Skill}: {f.From} → {f.To}", f.Count, "int", f.Direction == "down" ? "خفض" : f.Direction == "up" ? "رفع" : null));

        var mostDown = bySkill.OrderByDescending(s => s.Down).FirstOrDefault();
        if (mostDown is { Down: > 0 })
        {
            b.Findings.Add($"أكثر خفض للمستوى في {mostDown.Skill} ({mostDown.Down} مرة).");
            b.Where.Add(new("المهارة", mostDown.Skill, $"{mostDown.Down} خفض مقابل {mostDown.Up} رفع"));
            b.Hypotheses.Add($"قد يكون محتوى {mostDown.Skill} أصعب من المستوى الذي يختاره المتعلم.");
        }
        var hot = byLevel.OrderByDescending(l => l.Downgrades).FirstOrDefault();
        if (hot is { Downgrades: > 0 }) b.Where.Add(new("المستوى", hot.Level, $"{hot.Downgrades} خفض منه"));

        var lowered = b.Scope.LevelChanges.Where(l => b.Scope.Period.Contains(l.CreatedAt) && l.NewLevel < l.PreviousLevel
                                                      && l.ChangeType == LevelChangeType.UserManualChange)
            .GroupBy(l => l.UserId).ToList();
        b.AddUsers(lowered.Select(g => b.Scope.Data.LearnerById(g.Key)).OfType<User>(),
            u => $"خفّض المستوى {lowered.First(g => g.Key == u.Id).Count()} مرات");
        b.UsersLabel = "خفّضوا المستوى يدويًا";
    }

    private static void Retention(Builder b)
    {
        var p = b.Scope.Period;
        foreach (var n in new[] { 1, 7, 30 })
            b.Evidence.Add(new($"D{n} Retention", IntelMetrics.RetentionAt(b.Scope, n, p.To), "pct", null, IntelMetrics.RetentionAt(b.Scope, n, p.PrevTo)));
        var samples = IntelMetrics.RecallSamples(b.Scope);
        var curve = IntelMetrics.RecallCurve(samples);
        b.Charts.Add(new("recall_curve", "line", "احتمال التذكر حسب الوقت منذ آخر لقاء بالكلمة", "pct",
            curve.Select(c => c.Label).ToList(), [new("نجاح من أول محاولة", curve.Select(c => c.Success).ToList())],
            string.Join(" · ", curve.Where(c => c.Attempts > 0).Select(c => $"{c.Label}: {c.Attempts}"))));
        b.Evidence.Add(new("محاولات تذكّر مقيسة", samples.Count, "int"));
        b.Evidence.Add(new("Recall Success", IntelMetrics.Ratio(samples.Count(s => s.Success), samples.Count), "pct"));

        var gap = b.Scope.Data.Config.SkillIntervalDays * 24.0;
        var atGap = curve.FirstOrDefault(c => c.MinHours <= gap && c.Attempts > 0 && gap < (c.MinHours == 0 ? 24 : c.MinHours * 2));
        var longer = curve.Where(c => c.MinHours > gap && c.Attempts >= 3 && c.Success is not null).ToList();
        if (longer.Count > 0)
            b.Findings.Add($"بعد أكثر من {b.Scope.Data.Config.SkillIntervalDays} أيام، التذكر بين {longer.Min(c => c.Success):P0} و{longer.Max(c => c.Success):P0}.");
        b.Hypotheses.Add("الفجوة الحالية بين المهارات ثابتة لكل الكلمات؛ قد تحتاج الكلمات الأصعب فجوة أقصر والأسهل أطول.");

        var cohorts = IntelMetrics.Cohorts(b.Scope);
        b.Charts.Add(new("cohort_week1", "bar", "نسبة العودة في الأسبوع الأول لكل Cohort", "pct",
            cohorts.Select(c => c.Cohort).ToList(), [new("الأسبوع 1", cohorts.Select(c => c.Weeks.FirstOrDefault()).ToList())],
            string.Join(" · ", cohorts.Where(c => c.Size > 0).Select(c => $"{c.Cohort}: {c.Size}"))));

        b.AddUsers(b.Scope.Learners.Where(u => Attention.Reasons(b.Scope.Data, u).Any(r => r.Key == "recently_inactive")),
            u => Attention.Reasons(b.Scope.Data, u).First(r => r.Key == "recently_inactive").Evidence);
        b.UsersLabel = "توقفوا مؤخرًا";
        b.Leads.Add(new("قارن منحنى التذكر لكل مهارة", "إذا انخفض التذكر في مهارة واحدة بعد الفجوة فالمشكلة في المهارة لا في الجدولة."));
    }

    private static void WeeklyReview(Builder b)
    {
        var json = JsonSerializer.SerializeToElement(IntelMetrics.WeeklyReview(b.Scope), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        double? D(string k) => json.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        b.Evidence.AddRange(
        [
            new("بدأوا", D("started"), "int"), new("أكملوا", D("completed"), "int"),
            new("كلمات روجعت", D("wordsReviewed"), "int"), new("Accuracy", D("accuracy"), "pct"),
            new("دقة المحاولة الأولى", D("firstAttemptAccuracy"), "pct"), new("Drop-off", D("dropOff"), "pct"),
            new("الوقت (وسيط)", D("medianMs"), "ms"),
        ]);
        if (json.TryGetProperty("trend", out var trend) && trend.GetArrayLength() > 0)
        {
            var points = trend.EnumerateArray().ToList();
            b.Charts.Add(new("review_trend", "line", "دقة المراجعة الأسبوعية عبر الأسابيع", "pct",
                points.Select(p => p.GetProperty("week").GetString()!).ToList(),
                [new("دقة المحاولة الأولى", points.Select(p => p.GetProperty("firstAttempt").ValueKind == JsonValueKind.Number ? p.GetProperty("firstAttempt").GetDouble() : (double?)null).ToList())]));
        }
        if (json.TryGetProperty("mostFailed", out var failed))
            foreach (var w in failed.EnumerateArray().Take(5))
                b.Where.Add(new("كلمة", w.GetProperty("text").GetString()!, $"{w.GetProperty("failures").GetInt32()} أخطاء لدى {w.GetProperty("users").GetInt32()} مستخدمين"));

        var started = b.Scope.Reviews.Where(r => !r.IsPractice && !r.IsComplete && b.Scope.Period.Contains(r.StartedAt)).Select(r => r.UserId).Distinct();
        b.AddUsers(started.Select(b.Scope.Data.LearnerById).OfType<User>(), _ => "بدأ المراجعة ولم يكملها");
        b.UsersLabel = "بدأوا المراجعة ولم يكملوها";
        if (D("dropOff") is >= 0.3) b.Findings.Add($"{D("dropOff"):P0} ممن بدأوا المراجعة لم يكملوها.");
        b.Hypotheses.Add("قد يكون عدد كلمات المراجعة الأسبوعية كبيرًا لجلسة واحدة.");
    }

    private static void Ux(Builder b)
    {
        var friction = IntelMetrics.Friction(b.Scope).Where(f => f.Value is not null && f.Severity != "none").ToList();
        b.Charts.Add(new("friction", "bar", "نقاط الاحتكاك — المستخدمون المتأثرون", "int",
            friction.Select(f => f.Title).ToList(), [new("مستخدمون", friction.Select(f => (double?)f.AffectedUsers).ToList())]));
        foreach (var f in friction.Take(8)) b.Evidence.Add(new(f.Title, f.Value, f.Format, f.Description, f.Baseline));
        foreach (var f in friction.Where(f => f.Severity == "high").Take(3))
        {
            b.Where.Add(new(f.Skill ?? "عام", f.Title, $"{f.AffectedUsers} مستخدمين"));
            b.Findings.Add($"«{f.Title}» عند مستوى مرتفع ({FormatValue(f.Value, f.Format)}).");
            b.Hypotheses.Add(CategoryHypothesis(f.Category));
        }
        var tracked = b.Scope.Events.Any(e => e.Source == AnalyticsSource.Client);
        if (!tracked) b.Findings.Add("لا توجد أحداث من التطبيق بعد — مؤشرات الاحتكاك من الشاشة تبدأ مع الإصدار الذي يرسلها.");
        var top = friction.FirstOrDefault();
        if (top is not null) b.Leads.Add(new($"افتح «{top.Title}»", "يعرض المستخدمين المتأثرين والمستوى والمحتوى، وهل أكملوا أم انسحبوا."));
        b.AddUsers(b.Scope.Learners.Where(u => Attention.Reasons(b.Scope.Data, u).Any(r => r.Key is "high_abandonment" or "very_short_sessions")),
            u => string.Join(" · ", Attention.Reasons(b.Scope.Data, u).Where(r => r.Key is "high_abandonment" or "very_short_sessions").Select(r => r.Evidence)));
        b.UsersLabel = "انسحاب مرتفع أو Sessions قصيرة جدًا";
    }

    private static void Feedback(Builder b)
    {
        var feedback = b.Scope.Feedback;
        var byCategory = feedback.GroupBy(f => f.Category?.ToString() ?? "بدون تصنيف").OrderByDescending(g => g.Count()).ToList();
        b.Charts.Add(new("feedback_categories", "bar", "الملاحظات حسب التصنيف", "int",
            byCategory.Select(g => g.Key).ToList(), [new("ملاحظات", byCategory.Select(g => (double?)g.Count()).ToList())]));
        b.Evidence.Add(new("كل الملاحظات", feedback.Count, "int"));
        b.Evidence.Add(new("غير مقروءة", feedback.Count(f => f.Status == FeedbackStatus.New), "int"));
        b.Evidence.Add(new("في الفترة", feedback.Count(f => b.Scope.Period.Contains(f.CreatedAt)), "int"));
        foreach (var t in FeedbackThemes.Find(feedback.ToList()).Take(5))
            b.Where.Add(new("موضوع متكرر", t.Term, $"في {t.Messages} ملاحظات"));
        if (byCategory.Count > 0) b.Findings.Add($"أكثر تصنيف: {byCategory[0].Key} ({byCategory[0].Count()}).");
        b.AddUsers(feedback.Select(f => f.UserId).Distinct().Select(b.Scope.Data.LearnerById).OfType<User>(),
            u => $"{feedback.Count(f => f.UserId == u.Id)} ملاحظات");
        b.UsersLabel = "كتبوا ملاحظات";
        b.Leads.Add(new("اقرأ الملاحظات المتكررة مع Timeline كاتبها", "ما حدث قبل كتابة الملاحظة مباشرة هو السياق الذي لا يذكره النص."));
    }

    private static void Notifications(Builder b)
    {
        var opened = b.Scope.EventsIn(AnalyticsEventNames.NotificationOpened).ToList();
        var converted = opened.Count(n => b.Scope.Sessions.Any(s => s.UserId == n.UserId && s.StartedAt >= n.OccurredAt
                                                                   && s.StartedAt - n.OccurredAt <= TimeSpan.FromMinutes(30)));
        b.Evidence.Add(new("فتح التذكير", opened.Count, "int", "تُرسل محليًا من الهاتف — الإرسال نفسه لا يُرى من الخادم"));
        b.Evidence.Add(new("Notification → تعلم خلال 30 دقيقة", IntelMetrics.Ratio(converted, opened.Count), "pct", $"{converted} من {opened.Count}"));
        b.Evidence.Add(new("مستخدمون فتحوا التذكير", opened.Select(o => o.UserId).Distinct().Count(), "int"));
        var byHour = Enumerable.Range(0, 24).Select(h => (double?)opened.Count(o => o.OccurredAt.ToOffset(TimeSpan.FromHours(b.Scope.Data.Config.ReportingUtcOffsetHours)).Hour == h)).ToList();
        b.Charts.Add(new("notification_hours", "bar", "متى يفتح المستخدمون التذكير (ساعة محلية)", "int",
            Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToList(), [new("مرات الفتح", byHour)]));
        if (opened.Count == 0) b.Findings.Add("لا توجد أحداث فتح تذكير بعد — تبدأ مع الإصدار الذي يرسلها.");
        b.AddUsers(b.Scope.Learners.Where(u => Attention.Reasons(b.Scope.Data, u).Any(r => r.Key == "notification_no_learning")),
            u => Attention.Reasons(b.Scope.Data, u).First(r => r.Key == "notification_no_learning").Evidence);
        b.UsersLabel = "يفتحون التذكير ولا يتعلمون";
        b.Hypotheses.Add("قد لا يقود التذكير مباشرة إلى التمرين المستحق، فيصل المستخدم إلى الشاشة الرئيسية ويغادر.");
    }

    private static void Ai(Builder b)
    {
        var calls = b.Scope.EventsIn(AnalyticsEventNames.AiCall).ToList();
        var sessions = b.Scope.SessionsIn().ToList();
        b.Evidence.Add(new("طلبات الـAI", calls.Count, "int"));
        b.Evidence.Add(new("معدل الخطأ", IntelMetrics.Ratio(calls.Count(c => c.Result == "error"), calls.Count), "pct"));
        b.Evidence.Add(new("Sessions بمحتوى احتياطي", IntelMetrics.Ratio(sessions.Count(s => s.UsedAiFallback), sessions.Count), "pct", $"{sessions.Count(s => s.UsedAiFallback)} من {sessions.Count}"));
        b.Evidence.Add(new("زمن الاستجابة (وسيط)", IntelMetrics.Median(calls.Where(c => c.DurationMs != null).Select(c => (double)c.DurationMs!.Value)), "ms"));
        b.Evidence.Add(new("زمن الاستجابة (P95)", IntelMetrics.Percentile(calls.Where(c => c.DurationMs != null).Select(c => (double)c.DurationMs!.Value), 0.95), "ms"));

        var byOp = calls.GroupBy(c => IntelMetrics.Prop<string>(c, "operation") ?? "other").ToList();
        b.Charts.Add(new("ai_latency", "bar", "زمن استجابة الـAI حسب العملية (وسيط)", "ms",
            byOp.Select(g => g.Key).ToList(),
            [new("وسيط", byOp.Select(g => IntelMetrics.Median(g.Where(c => c.DurationMs != null).Select(c => (double)c.DurationMs!.Value))).ToList()),
             new("P95", byOp.Select(g => IntelMetrics.Percentile(g.Where(c => c.DurationMs != null).Select(c => (double)c.DurationMs!.Value), 0.95)).ToList())]));
        foreach (var g in byOp.Where(g => g.Any(c => c.Result == "error")))
            b.Where.Add(new("العملية", g.Key, $"{g.Count(c => c.Result == "error")} أخطاء من {g.Count()}"));

        var writingFallback = b.Scope.EventsIn(AnalyticsEventNames.WritingEvaluated).Count(e => IntelMetrics.Prop<bool?>(e, "fromFallback") == true);
        if (writingFallback > 0) b.Findings.Add($"{writingFallback} جمل Writing قُيّمت بالتقييم الاحتياطي لأن الـAI لم يُجب.");
        if (calls.Count == 0) b.Findings.Add("لا توجد قياسات لطلبات الـAI في هذه الفترة — تبدأ من هذا الإصدار في الخادم.");
        b.AddUsers(b.Scope.Learners.Where(u => Attention.Reasons(b.Scope.Data, u).Any(r => r.Key == "ai_content_issue")),
            u => Attention.Reasons(b.Scope.Data, u).First(r => r.Key == "ai_content_issue").Evidence);
        b.UsersLabel = "تأثروا بمشكلات الـAI";
        b.Hypotheses.Add("ارتفاع الزمن في عملية واحدة يشير غالبًا إلى طول الـprompt أو المحتوى المطلوب منها، لا إلى المزوّد كله.");
    }

    // ── Add-ons the question asks for ────────────────────────────────────────

    private static void EngagementChart(Builder b)
    {
        var series = IntelMetrics.Engagement(b.Scope);
        b.Charts.Add(new("engagement", "line", "المستخدمون النشطون والـSessions يوميًا", "int",
            series.Select(p => p.Date[5..]).ToList(),
            [new("نشطون", series.Select(p => (double?)p.ActiveUsers).ToList()), new("Sessions", series.Select(p => (double?)p.Sessions).ToList())]));
    }

    private static void NewVersusEstablished(Builder b)
    {
        if (b.Comparison is not null && b.Comparison.Title.Contains("الجدد")) return;
        var data = b.Scope.Data;
        var fresh = new IntelScope(data, b.Scope.Filter with { Segment = "joined:30" });
        var rest = b.Scope.Learners.Where(u => !fresh.Ids.Contains(u.Id)).Select(u => u.Id).ToHashSet();
        var others = new Segments(data);

        var rows = IntelMetrics.Skills.Select(s =>
        {
            var a = IntelMetrics.SkillSummaryFor(fresh, s).FirstAttemptAccuracy;
            var (first, decided) = rest.Aggregate((0, 0), (acc, id) => { var (f, d) = others.FirstAttempt(id, s); return (acc.Item1 + f, acc.Item2 + d); });
            return new ComparisonRow($"{s} — دقة المحاولة الأولى", a, IntelMetrics.Ratio(first, decided), "pct");
        }).ToList();
        rows.Insert(0, new ComparisonRow("عدد المستخدمين", fresh.Learners.Count, rest.Count, "int"));

        // The question asked about new learners, so this comparison replaces
        // the section's own: it is the one that answers it.
        b.Comparison = new Comparison("المستخدمون الجدد (30 يومًا) مقابل البقية", "الجدد", "البقية", rows);
        var worst = rows.Skip(1).Where(r => r.A is not null && r.B is not null).OrderBy(r => r.A - r.B).FirstOrDefault();
        if (worst is not null && worst.B - worst.A >= 0.1)
            b.Findings.Add($"المستخدمون الجدد أضعف في {worst.Label.Split(' ')[0]}: {Pct(worst.A)} مقابل {Pct(worst.B)}.");
    }

    private static void TranslationVersusAccuracy(Builder b)
    {
        var events = b.Scope.EventsIn(AnalyticsEventNames.TranslationOpened).Where(e => e.SessionId != null)
            .GroupBy(e => e.SessionId!.Value).ToDictionary(g => g.Key, g => g.Count());
        var sessions = b.Scope.SessionsIn().Where(s => s.Skill == SkillType.Reading && !s.IsPractice).ToList();
        var groups = new (string Label, Func<int, bool> Test)[] { ("بلا ترجمة", n => n == 0), ("1–3 ترجمات", n => n is >= 1 and <= 3), ("4 أو أكثر", n => n >= 4) };
        var acc = groups.Select(g =>
        {
            var items = sessions.Where(s => g.Test(events.GetValueOrDefault(s.Id))).SelectMany(s => s.Items).Where(i => i.FirstAttemptCorrect != null).ToList();
            return (g.Label, accuracy: IntelMetrics.Ratio(items.Count(i => i.FirstAttemptCorrect == true), items.Count),
                sessions: sessions.Count(s => g.Test(events.GetValueOrDefault(s.Id))));
        }).ToList();
        b.Charts.Add(new("translation_accuracy", "bar", "دقة Reading حسب عدد الترجمات المفتوحة في الـSession", "pct",
            acc.Select(a => a.Label).ToList(), [new("دقة المحاولة الأولى", acc.Select(a => a.accuracy).ToList())],
            string.Join(" · ", acc.Select(a => $"{a.Label}: {a.sessions} Sessions"))));
        if (acc[0].accuracy is { } none && acc[2].accuracy is { } many && acc[2].sessions >= 2)
            b.Findings.Add($"Sessions بأربع ترجمات أو أكثر دقتها {many:P0} مقابل {none:P0} بلا ترجمة — ارتباط وليس بالضرورة سببًا.");
        if (events.Count == 0) b.Findings.Add("لا توجد أحداث ترجمة بعد؛ تُسجَّل من إصدار التطبيق الذي يرسلها.");
    }

    private static void ReplayVersusAccuracy(Builder b)
    {
        var replays = b.Scope.EventsIn(AnalyticsEventNames.AudioReplayed).Where(e => e.SessionId != null)
            .GroupBy(e => e.SessionId!.Value).ToDictionary(g => g.Key, g => g.Count());
        var sessions = b.Scope.SessionsIn().Where(s => s.Skill == SkillType.Listening && !s.IsPractice).ToList();
        var groups = new (string Label, Func<int, bool> Test)[] { ("بلا إعادة", n => n == 0), ("1–2", n => n is 1 or 2), ("3 أو أكثر", n => n >= 3) };
        var acc = groups.Select(g =>
        {
            var mine = sessions.Where(s => g.Test(replays.GetValueOrDefault(s.Id))).ToList();
            var items = mine.SelectMany(s => s.Items).Where(i => i.FirstAttemptCorrect != null).ToList();
            return (g.Label, accuracy: IntelMetrics.Ratio(items.Count(i => i.FirstAttemptCorrect == true), items.Count), sessions: mine.Count,
                levels: string.Join("/", mine.Select(s => s.LevelUsed.ToWire()).Distinct().Take(3)));
        }).ToList();
        b.Charts.Add(new("replay_accuracy", "bar", "دقة Listening حسب عدد مرات إعادة التشغيل", "pct",
            acc.Select(a => a.Label).ToList(), [new("دقة المحاولة الأولى", acc.Select(a => a.accuracy).ToList())],
            string.Join(" · ", acc.Select(a => $"{a.Label}: {a.sessions} Sessions ({a.levels})"))));
    }

    private static void WordsVersusMastery(Builder b)
    {
        var data = b.Scope.Data;
        var groups = new (string Label, Func<int, bool> Test)[]
            { ("1–5 كلمات", n => n is >= 1 and <= 5), ("6–10", n => n is >= 6 and <= 10), ("11–20", n => n is >= 11 and <= 20), ("أكثر من 20", n => n > 20) };
        var rows = groups.Select(g =>
        {
            var users = b.Scope.Learners.Where(u => g.Test(data.WordsOf(u.Id).Count(w => w.State != WordState.Deleted))).ToList();
            var words = users.SelectMany(u => data.WordsOf(u.Id).Where(w => w.State != WordState.Deleted)).ToList();
            return (g.Label, users: users.Count, mastered: IntelMetrics.Ratio(words.Count(w => w.MaturedAt != null), words.Count),
                stuck: IntelMetrics.Ratio(words.Count(w => w.State == WordState.Learning && w.CurrentSkill == SkillType.Reading), words.Count));
        }).ToList();
        b.Charts.Add(new("words_vs_mastery", "bar", "نسبة الإتقان حسب عدد الكلمات المضافة", "pct",
            rows.Select(r => r.Label).ToList(),
            [new("نسبة الكلمات المتقنة", rows.Select(r => r.mastered).ToList()), new("ما زالت في Reading", rows.Select(r => r.stuck).ToList())],
            string.Join(" · ", rows.Select(r => $"{r.Label}: {r.users} مستخدمين"))));
        var heavy = rows.Where(r => r.users > 0).ToList();
        if (heavy.Count >= 2 && heavy[^1].mastered < heavy[0].mastered)
            b.Findings.Add($"من أضافوا {heavy[^1].Label} أتقنوا {Pct(heavy[^1].mastered)} من كلماتهم مقابل {Pct(heavy[0].mastered)} لمن أضافوا {heavy[0].Label}.");
        b.Hypotheses.Add("قد يتجاوز عدد الكلمات المضافة طاقة التمارين اليومية، فتتراكم في Reading ولا تتقدم.");
    }

    private static void LevelSlice(Builder b, string level, SkillType? skill)
    {
        var sessions = b.Scope.SessionsIn().Where(s => !s.IsPractice && s.LevelUsed.ToWire().StartsWith(level)
                                                       && (skill == null || s.Skill == skill)).ToList();
        var others = b.Scope.SessionsIn().Where(s => !s.IsPractice && !s.LevelUsed.ToWire().StartsWith(level)
                                                     && (skill == null || s.Skill == skill)).ToList();
        double? Acc(IEnumerable<WordOs.Domain.Sessions.SkillSession> ss)
        {
            var items = ss.SelectMany(s => s.Items).Where(i => i.FirstAttemptCorrect != null).ToList();
            return IntelMetrics.Ratio(items.Count(i => i.FirstAttemptCorrect == true), items.Count);
        }
        b.Evidence.Add(new($"دقة المحاولة الأولى عند {level}", Acc(sessions), "pct", $"{sessions.Count} Sessions"));
        b.Evidence.Add(new("دقة المحاولة الأولى في المستويات الأخرى", Acc(others), "pct", $"{others.Count} Sessions"));
        b.Evidence.Add(new($"الانسحاب عند {level}", IntelMetrics.Ratio(sessions.Count(b.Scope.Data.IsStale), sessions.Count), "pct"));
        var lowered = b.Scope.LevelChanges.Count(l => b.Scope.Period.Contains(l.CreatedAt) && l.PreviousLevel?.ToWire().StartsWith(level) == true && l.NewLevel < l.PreviousLevel);
        b.Evidence.Add(new($"مرات الخفض من {level}", lowered, "int"));
        b.Where.Add(new("المستوى", level, $"{sessions.Count} Sessions في الفترة"));
        if (Acc(sessions) is { } a && Acc(others) is { } o && o - a >= 0.1)
            b.Findings.Add($"الدقة عند {level} أقل بـ{(o - a) * 100:0} نقطة من بقية المستويات.");
    }

    // ── Rule-written prose ───────────────────────────────────────────────────

    private static string RuleSummary(Builder b, string section)
    {
        if (b.Findings.Count > 0) return string.Join(" ", b.Findings.Take(2));
        return b.Evidence.Count == 0
            ? $"لا توجد بيانات كافية في قسم {section} لهذه الفترة."
            : $"لم تظهر في قسم {section} تغيرات كبيرة في هذه الفترة؛ الأرقام أدناه هي الصورة الحالية.";
    }

    private static string DataNote(IntelScope scope, Builder b)
    {
        var notes = new List<string>
        {
            $"الفترة {scope.Data.Day(scope.Period.From):yyyy-MM-dd} — {scope.Data.Day(scope.Period.To.AddTicks(-1)):yyyy-MM-dd}، {scope.Learners.Count} متعلمين في النطاق.",
        };
        if (scope.Learners.Count < 15) notes.Add("العينة صغيرة: فرق مستخدم أو اثنين يحرّك النسب كثيرًا، فاقرأ الاتجاه لا الرقم الدقيق.");
        if (!scope.Events.Any(e => e.Source == AnalyticsSource.Client))
            notes.Add("أحداث الشاشة (ترجمة، إعادة تشغيل، تلميحات، خروج) تبدأ مع إصدار التطبيق الذي يرسلها.");
        return string.Join(" ", notes);
    }

    private static string CategoryHypothesis(string category) => category switch
    {
        "content" => "قد يكون مستوى المحتوى أو سرعة الصوت أعلى من مستوى المتعلمين في هذه النقطة.",
        "ux" => "قد تكون التجربة نفسها — طول التمرين أو وضوح الخطوة التالية — سببًا في الانسحاب أكثر من صعوبة التعلم.",
        "ai" => "قد يكون التقييم أو المحتوى المولّد هو المصدر: محتوى احتياطي أو تقييم لا يطابق ما قاله المتعلم.",
        "learning" => "قد يحتاج المتعلمون تمهيدًا أكثر للكلمة قبل هذه المهارة، أو أمثلة أوضح على الاستخدام.",
        _ => "يحتاج هذا إلى بيانات أكثر قبل تفسيره.",
    };

    private static string AreaLabel(string area) => area switch
    {
        "learning" => "التعلم", "ux" => "UX", "content" => "المحتوى", "ai" => "AI",
        "feedback" => "Feedback", "users" => "المستخدمون", _ => area,
    };

    private static string Pct(double? v) => v is null ? "—" : $"{v:P0}";

    private static string FormatValue(double? v, string format) => format switch
    {
        "pct" => Pct(v),
        "ms" => v is null ? "—" : IntelTimeline.Duration(TimeSpan.FromMilliseconds(v.Value)),
        _ => v is null ? "—" : $"{v:0.#}",
    };

    // ── Reading the question ─────────────────────────────────────────────────

    private static bool Mentions(string q, params string[] terms) =>
        terms.Any(t => q.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<SkillType> SkillsIn(string q)
    {
        var found = new List<SkillType>();
        if (Mentions(q, "reading", "قراءة", "القراءة")) found.Add(SkillType.Reading);
        if (Mentions(q, "listening", "استماع", "الاستماع", "سماع")) found.Add(SkillType.Listening);
        if (Mentions(q, "speaking", "محادثة", "المحادثة", "تحدث", "كلام")) found.Add(SkillType.Speaking);
        if (Mentions(q, "writing", "كتابة", "الكتابة")) found.Add(SkillType.Writing);
        if (Mentions(q, "spelling", "تهجئة", "التهجئة", "إملاء")) found.Add(SkillType.Spelling);
        return found;
    }

    private static string? LevelIn(string q) => Level().Match(q.ToUpperInvariant()) is { Success: true } m ? m.Value : null;

    [GeneratedRegex(@"\b(A1|A2|B1|B2|C1|C2)\b")]
    private static partial Regex Level();
}
