using WordOs.Domain.Analytics;
using WordOs.Domain.Common;
using WordOs.Domain.Words;

namespace WordOs.Api.Admin;

/// <summary>A value, and how many cases it was computed from.</summary>
/// <param name="Sample">The denominator — learners, sessions, answers — named by <see cref="MeasureDef.SampleUnit"/>.</param>
public sealed record MeasureValue(double? Value, int Sample);

/// <summary>
/// One metric in the dictionary (brief §15).
/// </summary>
/// <param name="Formula">How it is computed, in words the product manager can check against a row.</param>
/// <param name="Source">The tables it reads.</param>
/// <param name="Limitation">What it cannot see, or since when it is meaningful.</param>
/// <param name="Revised">When this definition last changed — the date a series may break.</param>
public sealed record MeasureDef(
    string Key, string Label, string Area, string Format, bool GoodWhenUp,
    string Definition, string Formula, string Source, string SampleUnit,
    string? Limitation, string Revised)
{
    internal Func<IntelScope, MeasureValue> Compute { get; init; } = _ => new(null, 0);

    /// <summary>
    /// A count that grows with the window — sessions, words, events — and so
    /// may be scaled to compare windows of different lengths. Distinct learners
    /// and rates are not: twice the days is not twice the learners.
    /// </summary>
    public bool Additive { get; init; }
}

/// <summary>
/// The metric dictionary, and the one way a metric is computed for any
/// population and window (ADR-132).
/// </summary>
/// <remarks>
/// <para>Decisions and changes promise to move metrics, and the promise is
/// only checkable if "abandonment" means the same thing when the decision is
/// written, when it is evaluated, on the Skills page and in the AI's
/// evidence. So every metric a decision may name is defined here once, with
/// its formula and its blind spots, and the Settings page prints this list
/// verbatim — the dictionary cannot drift from the code because it <i>is</i>
/// the code.</para>
///
/// <para>Each definition reuses the page computations where one exists
/// (<see cref="IntelMetrics.SkillSummaryFor"/>, <see cref="IntelMetrics.RetentionAt"/>)
/// rather than restating them.</para>
/// </remarks>
public static class IntelMeasures
{
    private const string Revised = "2026-11-03";

    public static readonly IReadOnlyList<MeasureDef> Catalogue = Build();

    public static MeasureDef? Find(string key) => Catalogue.FirstOrDefault(m => m.Key == key);

    /// <summary>The headline metrics an evaluation always reports, to show what else moved.</summary>
    public static readonly IReadOnlyList<string> Guardrails =
        ["learning_learners", "sessions", "learning_minutes", "words_entered_active", "retention_d7"];

    private static List<MeasureDef> Build()
    {
        var list = new List<MeasureDef>
        {
            new("active_learners", "المستخدمون النشطون", "engagement", "int", true,
                "متعلم سُجّل له أي نشاط خلال الفترة.",
                "عدد المتعلمين الذين لهم يوم نشط واحد على الأقل داخل الفترة. النشاط = تسجيل دخول، أو تمرين، أو كلمة أضيفت، أو مراجعة، أو ملاحظة، أو حدث من التطبيق.",
                "activity_events, skill_sessions, words, weekly_reviews, feedback_messages, analytics_events", "متعلم",
                "فتح التطبيق دون أي فعل لا يُرى إلا من الإصدارات التي ترسل app_opened.", Revised)
            { Compute = s => new(s.Learners.Count(u => ActiveIn(s, u.Id)), s.Learners.Count) },

            new("learning_learners", "النشطون تعليميًا", "engagement", "int", true,
                "متعلم بدأ نشاطًا تعليميًا فعليًا خلال الفترة.",
                "عدد المتعلمين الذين بدأوا تمرين مهارة (دون التدريب الحر) أو مراجعة أسبوعية داخل الفترة.",
                "skill_sessions, weekly_reviews", "متعلم", null, Revised)
            { Compute = s => new(s.Learners.Count(u => LearnedIn(s, u.Id)), s.Learners.Count) },

            new("active_days", "أيام النشاط", "engagement", "int", true,
                "مجموع الأيام التي نشط فيها كل متعلم.",
                "لكل متعلم: عدد الأيام (بتوقيت التقارير) التي فيها نشاط داخل الفترة، ثم المجموع.",
                "كما في «المستخدمون النشطون»", "متعلم", null, Revised)
            { Additive = true, Compute = s => new(s.Learners.Sum(u => s.Data.ActiveDaysOf(u.Id).Count(d => s.Period.Contains(s.Data.StartOf(d)))), s.Learners.Count) },

            new("app_opens", "مرات فتح التطبيق", "engagement", "int", true,
                "مرات انتقال التطبيق إلى الواجهة.",
                "عدد أحداث app_opened داخل الفترة.",
                "analytics_events", "حدث",
                "من الهاتف فقط، ومن الإصدارات التي تحمل التتبع — صفر قبلها لا يعني أن أحدًا لم يفتحه.", Revised)
            { Additive = true, Compute = s => Count(s, AnalyticsEventNames.AppOpened) },

            new("sessions", "Sessions", "engagement", "int", true,
                "تمارين المهارات التي بدأت.",
                "عدد skill_sessions التي بدأت داخل الفترة، دون التدريب الحر، مع التمارين التي انسحب منها المتعلم (session_abandoned) لأنها تُحذف.",
                "skill_sessions, analytics_events", "تمرين",
                "الانسحاب قبل ADR-125 حذف التمرين دون أثر، فالفترات الأقدم تنقص.", Revised)
            {
                Additive = true,
                Compute = s =>
                {
                    var n = s.SessionsIn().Count(x => !x.IsPractice)
                            + s.EventsIn(AnalyticsEventNames.SessionAbandoned).Count();
                    return new(n, n);
                },
            },

            new("learning_minutes", "وقت التعلم الفعلي (دقائق)", "time", "num", true,
                "الوقت الذي كان فيه التمرين أمام المتعلم.",
                "لكل تمرين: مجموع screen_left داخل التمرين إن أرسله الهاتف، وإلا مدة التمرين المكتمل إن كانت أقل من حد الجلسة (SessionCapMinutes). لا يُحسب الوقت نفسه مرتين لأن كل دقيقة تنتمي إلى تمرين واحد.",
                "skill_sessions, analytics_events (screen_left)", "تمرين",
                "تمرين لم يكتمل ولم يرسل الهاتف وقته يُستبعد ولا يُخمَّن.", Revised)
            {
                Additive = true,
                Compute = s =>
                {
                    var screen = IntelMetrics.ScreenTime(s.Events);
                    var times = s.SessionsIn().Select(x => IntelMetrics.LearningMs(s.Data, x, screen)).OfType<double>().ToList();
                    return new(Math.Round(times.Sum() / 60_000, 1), times.Count);
                },
            },

            new("words_added", "الكلمات المضافة", "words", "int", true,
                "كلمات أضافها المتعلمون.",
                "عدد الكلمات التي AddedAt لها داخل الفترة، بما فيها التي حُذفت لاحقًا.",
                "words", "كلمة", null, Revised)
            {
                Additive = true,
                Compute = s =>
                {
                    var n = s.Words.Count(w => s.Period.Contains(w.AddedAt));
                    return new(n, n);
                },
            },

            new("words_entered_active", "كلمات دخلت Active", "words", "int", true,
                "كلمات أتمت المهارات الخمس خلال الفترة.",
                "عدد أحداث EnteredActive داخل الفترة. Mature وActive يحدثان في اللحظة نفسها في WordOS (ADR-125).",
                "word_events", "كلمة", null, Revised)
            {
                Additive = true,
                Compute = s =>
                {
                    var n = s.Words.SelectMany(w => w.Events)
                        .Count(e => e.Type == WordEventType.EnteredActive && s.Period.Contains(e.CreatedAt));
                    return new(n, n);
                },
            },

            new("days_to_active", "الأيام حتى Active (وسيط)", "words", "num", false,
                "كم يومًا استغرقت الكلمة من إضافتها حتى دخولها Active.",
                "للكلمات التي دخلت Active داخل الفترة: وسيط الأيام بين AddedAt وحدث EnteredActive. الفجوات المضبوطة بين المهارات تجعل الحد الأدنى نحو ثمانية أيام.",
                "words, word_events", "كلمة",
                "متعلمون قدّم المالك جدولهم (skip) يقصّرون المدة — يظهرون كما هم.", Revised)
            {
                Compute = s =>
                {
                    var days = s.Words.SelectMany(w => w.Events
                            .Where(e => e.Type == WordEventType.EnteredActive && s.Period.Contains(e.CreatedAt))
                            .Select(e => (e.CreatedAt - w.AddedAt).TotalDays))
                        .ToList();
                    return new(days.Count == 0 ? null : Math.Round(IntelMetrics.Median(days)!.Value, 1), days.Count);
                },
            },

            new("review_first_pass", "نجاح التذكر في المراجعة الأسبوعية", "retention", "pct", true,
                "الكلمات التي تذكرها المتعلم من أول محاولة في المراجعة الأسبوعية.",
                "مجموع FirstPassCorrect على مجموع TotalWords للمراجعات المكتملة داخل الفترة، دون مراجعات التدريب.",
                "weekly_reviews", "كلمة",
                "المراجعة تقيس فقط ولا تغير مسار الكلمة (R9).", Revised)
            {
                Compute = s =>
                {
                    var done = s.Reviews.Where(r => r.IsComplete && !r.IsPractice && r.CompletedAt is { } c && s.Period.Contains(c)).ToList();
                    var total = done.Sum(r => r.TotalWords);
                    return new(IntelMetrics.Ratio(done.Sum(r => r.FirstPassCorrect), total), total);
                },
            },

            new("notification_opens", "فتح التذكير", "notifications", "int", true,
                "مرات فتح التطبيق من تذكير.",
                "عدد أحداث notification_opened داخل الفترة.",
                "analytics_events", "حدث",
                "التذكير يُطلق من الهاتف نفسه (ADR-076)، فالإرسال والوصول لا يُريان من الخادم.", Revised)
            { Additive = true, Compute = s => Count(s, AnalyticsEventNames.NotificationOpened) },

            new("notification_conversion", "تذكير → تمرين", "notifications", "pct", true,
                "نسبة فتح التذكير التي تلاها بدء تمرين.",
                "من أحداث notification_opened داخل الفترة: النسبة التي بدأ بعدها المتعلم نفسه تمرينًا خلال NotificationConversionMinutes. ارتباط زمني، لا دليل سببية.",
                "analytics_events, skill_sessions", "فتح تذكير", null, Revised)
            {
                Compute = s =>
                {
                    var opened = s.EventsIn(AnalyticsEventNames.NotificationOpened).ToList();
                    return new(IntelMetrics.Ratio(opened.Count(o => ConvertedAfter(s, o)), opened.Count), opened.Count);
                },
            },

            new("ai_fallback_rate", "اللجوء إلى البديل في الـAI", "ai", "pct", false,
                "تمارين لم يُكتب محتواها بالنموذج فاستُخدم البديل.",
                "نسبة التمارين التي بدأت داخل الفترة و UsedAiFallback فيها صحيح.",
                "skill_sessions", "تمرين", null, Revised)
            {
                Compute = s =>
                {
                    var all = s.SessionsIn().ToList();
                    return new(IntelMetrics.Ratio(all.Count(x => x.UsedAiFallback), all.Count), all.Count);
                },
            },

            new("api_errors", "أخطاء API", "technical", "int", false,
                "طلبات فشلت كما رآها التطبيق.",
                "عدد أحداث api_error داخل الفترة.",
                "analytics_events", "حدث", "من الهاتف فقط.", Revised)
            { Additive = true, Compute = s => Count(s, AnalyticsEventNames.ApiError) },
        };

        foreach (var (n, label) in new[] { (1, "D1"), (7, "D7"), (30, "D30") })
        {
            list.Add(new($"retention_d{n}", $"Retention {label}", "retention", "pct", true,
                $"عودة المتعلم في اليوم {n} أو بعده.",
                $"من سجلوا قبل {n} يومًا على الأقل من نهاية الفترة: نسبة من نشطوا في اليوم {n} بعد التسجيل أو بعده (ADR-125).",
                "users ومصادر النشاط", "متعلم",
                "عودة إلى التطبيق، لا تذكّر للكلمات — ذلك «نجاح التذكر».", Revised)
            {
                Compute = s => new(IntelMetrics.RetentionAt(s, n, s.Period.To),
                    s.Learners.Count(u => s.Period.To - u.CreatedAt >= TimeSpan.FromDays(n))),
            });
        }

        foreach (var skill in IntelMetrics.Skills)
        {
            var w = IntelMetrics.Wire(skill).ToLowerInvariant();
            var name = skill.ToString();
            SkillSummary S(IntelScope s) => IntelMetrics.SkillSummaryFor(s, skill);

            list.Add(new($"{w}.first_attempt_accuracy", $"{name}: دقة المحاولة الأولى", w, "pct", true,
                "أسئلة أُجيب عنها صحيحًا من أول محاولة.",
                "من عناصر التمارين التي لها نتيجة أولى معروفة: نسبة FirstAttemptCorrect.",
                "session_items", "سؤال", null, Revised)
            { Compute = s => { var x = S(s); return new(x.FirstAttemptAccuracy, x.Questions); } });

            list.Add(new($"{w}.overall_accuracy", $"{name}: الدقة الإجمالية", w, "pct", true,
                "الإجابات الصحيحة من كل الإجابات.",
                "عدد العناصر المجتازة على مجموع المحاولات (لكل عنصر مجتاز إجابة صحيحة واحدة هي الأخيرة).",
                "session_items", "محاولة", null, Revised)
            { Compute = s => { var x = S(s); return new(x.OverallAccuracy, x.Attempts); } });

            list.Add(new($"{w}.pass_rate", $"{name}: Pass Rate", w, "pct", true,
                "الكلمات التي اجتازت المهارة من كل الكلمات التي حُسم أمرها.",
                "SkillPassed / (SkillPassed + SkillFailed) داخل الفترة. نجاح سؤال ≠ اجتياز المهارة: هذا قرار المهارة للكلمة.",
                "word_events", "قرار", null, Revised)
            { Compute = s => { var x = S(s); return new(x.PassRate, x.Passed + x.Failed); } });

            list.Add(new($"{w}.abandonment_rate", $"{name}: الانسحاب", w, "pct", false,
                "تمارين بدأت ولم تكتمل.",
                "(session_abandoned + تمارين مفتوحة أقدم من AbandonAfterHours) / التمارين التي بدأت.",
                "skill_sessions, analytics_events", "تمرين",
                "قبل ADR-125 كان الانسحاب يحذف التمرين دون أثر.", Revised)
            { Compute = s => { var x = S(s); return new(x.AbandonmentRate, x.Sessions); } });

            list.Add(new($"{w}.retry_rate", $"{name}: إعادة المحاولة", w, "pct", false,
                "أسئلة احتاجت أكثر من محاولة.",
                "عناصر بمحاولات > 1 على العناصر التي أُجيب عنها.",
                "session_items", "سؤال", null, Revised)
            { Compute = s => { var x = S(s); return new(x.RetryRate, x.Questions); } });

            list.Add(new($"{w}.median_answer_ms", $"{name}: زمن الإجابة (وسيط)", w, "ms", false,
                "الوقت من ظهور السؤال حتى إرسال الإجابة.",
                "وسيط elapsedMs في answer_submitted / writing_evaluated / speaking_turn داخل الفترة.",
                "analytics_events", "إجابة",
                "يُرسل فقط من إصدارات التطبيق التي تقيس elapsedMs.", Revised)
            {
                Compute = s =>
                {
                    var n = s.Events.Count(e => e.Skill == skill && s.Period.Contains(e.OccurredAt) && e.DurationMs is not null
                        && e.Name is AnalyticsEventNames.AnswerSubmitted or AnalyticsEventNames.WritingEvaluated or AnalyticsEventNames.SpeakingTurn);
                    return new(S(s).MedianAnswerMs, n);
                },
            });
        }

        return list;
    }

    private static MeasureValue Count(IntelScope s, string name)
    {
        var n = s.EventsIn(name).Count();
        return new(n, n);
    }

    private static bool ActiveIn(IntelScope s, Guid userId) =>
        s.Data.ActiveDaysOf(userId).Any(d => s.Period.Contains(s.Data.StartOf(d)));

    private static bool LearnedIn(IntelScope s, Guid userId) =>
        s.Sessions.Any(x => x.UserId == userId && !x.IsPractice && s.Period.Contains(x.StartedAt))
        || s.Reviews.Any(r => r.UserId == userId && !r.IsPractice && s.Period.Contains(r.StartedAt));

    /// <summary>A reminder opening that the same learner followed with a lesson.</summary>
    public static bool ConvertedAfter(IntelScope s, AnalyticsEvent opened) =>
        s.Data.SessionsOf(opened.UserId).Any(x =>
            x.StartedAt >= opened.OccurredAt
            && x.StartedAt - opened.OccurredAt <= TimeSpan.FromMinutes(s.Data.Options.NotificationConversionMinutes));

    /// <summary>One metric over one population and window.</summary>
    public static MeasureValue Measure(IntelDataset data, string key, DateOnly from, DateOnly to, string? segment, string? level = null)
    {
        var def = Find(key);
        if (def is null) return new(null, 0);
        var scope = new IntelScope(data, new IntelFilter(
            From: from.ToString("yyyy-MM-dd"), To: to.ToString("yyyy-MM-dd"), Segment: segment, Level: level));
        return def.Compute(scope);
    }
}
