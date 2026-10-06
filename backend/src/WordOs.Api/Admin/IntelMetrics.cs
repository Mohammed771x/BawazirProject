using System.Text.Json;
using WordOs.Domain.Analytics;
using WordOs.Domain.Common;
using WordOs.Domain.Levels;
using WordOs.Domain.Review;
using WordOs.Domain.Sessions;
using WordOs.Domain.Users;
using WordOs.Domain.Words;

namespace WordOs.Api.Admin;

// ── Shapes ───────────────────────────────────────────────────────────────────
//
// Every figure that can be undefined is nullable: "no data" and "zero" are
// different answers, and a chart that draws a missing value as 0% is lying.

public sealed record Kpi(string Key, string Label, double? Value, double? Previous, string Format, string? Hint = null);

public sealed record DayPoint(string Date, int ActiveUsers, int Sessions, double LearningMinutes, int NewUsers);

public sealed record FunnelStage(string Key, string Label, string Hint, int Count, double? Share, double? DropOff);

public sealed record SkillSummary(
    string Skill,
    int Sessions,
    int Completed,
    int Abandoned,
    int Practice,
    int Words,
    int Questions,
    int Attempts,
    int Passed,
    int Failed,
    double? FirstAttemptAccuracy,
    double? OverallAccuracy,
    double? PassRate,
    double? FailRate,
    double? RetryRate,
    double? AbandonmentRate,
    double? AvgSessionMs,
    double? MedianSessionMs,
    double? MedianAnswerMs);

public sealed record StageCount(string Skill, int Waiting, int Due, int Overdue);

public sealed record LifecyclePoint(string Date, int Added, int Passed, int Activated);

public sealed record Lifecycle(
    int Added, int Studied, int Learning, int Mature, int Active, int Archived, int Deleted,
    int Due, int Overdue, IReadOnlyList<StageCount> ByStage, IReadOnlyList<LifecyclePoint> Trend);

public sealed record Signal(string Severity, string Area, string Title, string Detail, string? Link);

public sealed record Reason(string Key, string Label, string Category, int Count);

public sealed record SkillFailures(string Skill, int Total, IReadOnlyList<Reason> Reasons);

public sealed record CategoryCount(string Category, string Label, int Count);

public sealed record Bucket(string Label, int Count);

public sealed record Metric(string Key, string Label, double? Value, string Format, string? Hint = null);

public sealed record LevelFlow(string Skill, string From, string To, int Count, string Direction);

public sealed record LevelSkillSummary(string Skill, int Up, int Down, int Manual, int System);

public sealed record LevelRow(string Level, int Downgrades, int Upgrades);

public sealed record LevelDistribution(string Skill, IReadOnlyList<LevelCount> Levels);

public sealed record LevelCount(string Level, int Selected, int Assessed);

public sealed record RecallPoint(string Label, double MinHours, int Attempts, double? Success);

public sealed record CohortRow(string Cohort, int Size, IReadOnlyList<double?> Weeks);

public sealed record WordFailure(string Text, string Meaning, int Failures, int Attempts, int Users);

public sealed record WeekPoint(string Week, int Reviews, double? Accuracy, double? FirstAttempt);

public sealed record FrictionItem(
    string Key, string Title, string Description, string? Skill, double? Value, string Format,
    double? Baseline, int AffectedUsers, string Severity, string Category);

/// <summary>
/// Every definition the admin area uses, in one place (ADR-125).
/// </summary>
public static class IntelMetrics
{
    public static readonly SkillType[] Skills =
        [SkillType.Reading, SkillType.Listening, SkillType.Speaking, SkillType.Writing, SkillType.Spelling];

    // ── Small helpers ────────────────────────────────────────────────────────

    public static double? Ratio(double part, double whole) => whole > 0 ? part / whole : null;

    public static double? Median(IEnumerable<double> values)
    {
        var list = values.Order().ToList();
        if (list.Count == 0) return null;
        return list.Count % 2 == 1 ? list[list.Count / 2] : (list[list.Count / 2 - 1] + list[list.Count / 2]) / 2;
    }

    public static double? Percentile(IEnumerable<double> values, double p)
    {
        var list = values.Order().ToList();
        if (list.Count == 0) return null;
        var i = (int)Math.Ceiling(p * list.Count) - 1;
        return list[Math.Clamp(i, 0, list.Count - 1)];
    }

    public static double? Mean(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? null : list.Average();
    }

    public static T? Prop<T>(AnalyticsEvent e, string name)
    {
        if (e.PropsJson is null) return default;
        try
        {
            using var doc = JsonDocument.Parse(e.PropsJson);
            if (!doc.RootElement.TryGetProperty(name, out var v)) return default;
            return v.ValueKind == JsonValueKind.Null ? default : v.Deserialize<T>();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public static string Wire(SkillType s) => s.ToWire();

    // ── Learning time ────────────────────────────────────────────────────────

    /// <summary>
    /// How long a session was actually worked on.
    /// </summary>
    /// <remarks>
    /// The phone's screen time when it reported any — that is time with the
    /// lesson in front of the learner, and survives resuming. Otherwise the
    /// wall-clock duration of a session finished within the cap. Otherwise
    /// unknown, and left out rather than guessed.
    /// </remarks>
    public static double? LearningMs(IntelDataset data, SkillSession s, ILookup<Guid?, AnalyticsEvent> screenTime)
    {
        var screen = screenTime[s.Id].Sum(e => (double)(e.DurationMs ?? 0));
        if (screen > 0) return screen;
        return data.DurationMs(s);
    }

    public static ILookup<Guid?, AnalyticsEvent> ScreenTime(IEnumerable<AnalyticsEvent> events) =>
        events.Where(e => e.Name == AnalyticsEventNames.ScreenLeft && e.SessionId is not null)
            .ToLookup(e => e.SessionId);

    // ── Overview ─────────────────────────────────────────────────────────────

    public static IReadOnlyList<Kpi> Health(IntelScope scope)
    {
        var data = scope.Data;
        var p = scope.Period;
        var lastDay = data.Day(p.To.AddTicks(-1));

        int ActiveBetween(DateOnly fromDay, DateOnly toDay) =>
            scope.Learners.Count(u => data.ActiveDaysOf(u.Id).Any(d => d >= fromDay && d <= toDay));

        int ActiveDays(bool prev) => scope.Learners.Sum(u => data.ActiveDaysOf(u.Id).Count(d =>
        {
            var t = data.StartOf(d);
            return prev ? p.PrevContains(t) : p.Contains(t);
        }));

        var screen = ScreenTime(scope.Events);
        double Minutes(bool prev) => scope.SessionsIn(previous: prev)
            .Select(s => LearningMs(data, s, screen)).OfType<double>().Sum() / 60_000;

        var sessions = scope.SessionsIn().Count(s => !s.IsPractice);
        var prevSessions = scope.SessionsIn(previous: true).Count(s => !s.IsPractice);

        return
        [
            new("total_users", "إجمالي المستخدمين",
                scope.Learners.Count(u => u.CreatedAt < p.To),
                scope.Learners.Count(u => u.CreatedAt < p.PrevTo), "int",
                "المتعلمون المسجلون حتى نهاية الفترة"),
            new("dau", "النشطون اليوم",
                ActiveBetween(lastDay, lastDay), ActiveBetween(lastDay.AddDays(-1), lastDay.AddDays(-1)), "int",
                "آخر يوم في الفترة، مقارنة باليوم الذي قبله"),
            new("wau", "النشطون هذا الأسبوع",
                ActiveBetween(lastDay.AddDays(-6), lastDay), ActiveBetween(lastDay.AddDays(-13), lastDay.AddDays(-7)), "int",
                "آخر 7 أيام، مقارنة بالسبعة التي قبلها"),
            new("mau", "النشطون هذا الشهر",
                ActiveBetween(lastDay.AddDays(-29), lastDay), ActiveBetween(lastDay.AddDays(-59), lastDay.AddDays(-30)), "int",
                "آخر 30 يومًا، مقارنة بالثلاثين التي قبلها"),
            new("active_days", "Active Days", ActiveDays(false), ActiveDays(true), "int",
                "مجموع الأيام التي نشط فيها كل مستخدم خلال الفترة"),
            new("sessions", "Sessions", sessions, prevSessions, "int",
                "تمارين المهارات التي بدأت خلال الفترة (دون التدريب الحر)"),
            new("learning_time", "وقت التعلم", Math.Round(Minutes(false), 1), Math.Round(Minutes(true), 1), "min",
                "وقت الشاشة داخل التمارين، أو مدة التمرين المكتمل عند غيابه"),
            new("retention_d7", "Retention D7", RetentionAt(scope, 7, p.To), RetentionAt(scope, 7, p.PrevTo), "pct",
                "من سجلوا قبل 7 أيام على الأقل وعادوا في اليوم السابع أو بعده"),
        ];
    }

    public static IReadOnlyList<DayPoint> Engagement(IntelScope scope)
    {
        var data = scope.Data;
        var first = data.Day(scope.Period.From);
        var last = data.Day(scope.Period.To.AddTicks(-1));
        var screen = ScreenTime(scope.Events);

        var sessionsByDay = scope.SessionsIn().Where(s => !s.IsPractice).ToLookup(s => data.Day(s.StartedAt));
        var activeByDay = scope.Learners
            .SelectMany(u => data.ActiveDaysOf(u.Id).Select(d => (d, u.Id)))
            .ToLookup(x => x.d);
        var joinedByDay = scope.Learners.ToLookup(u => data.Day(u.CreatedAt));

        var points = new List<DayPoint>();
        for (var d = first; d <= last; d = d.AddDays(1))
        {
            points.Add(new DayPoint(
                d.ToString("yyyy-MM-dd"),
                activeByDay[d].Count(),
                sessionsByDay[d].Count(),
                Math.Round(sessionsByDay[d].Select(s => LearningMs(data, s, screen)).OfType<double>().Sum() / 60_000, 1),
                joinedByDay[d].Count()));
        }
        return points;
    }

    public static IReadOnlyList<FunnelStage> FunnelFor(IntelScope scope)
    {
        // The learners who joined in the period: a funnel over everyone ever
        // registered mixes last year's cohort with yesterday's.
        var cohort = scope.Learners.Where(u => scope.Period.Contains(u.CreatedAt)).ToList();
        var stages = new List<FunnelStage>();
        int? previous = null;

        // Sequential: a stage counts a learner only if they reached every
        // stage before it too. The stages are not nested by nature — a word
        // can be added before onboarding finishes — and a funnel whose second
        // bar is shorter than its third has no drop-off to read.
        var remaining = cohort;
        foreach (var (key, label, hint) in Funnel.Stages)
        {
            remaining = remaining.Where(u => Funnel.Reached(scope.Data, u, key)).ToList();
            var count = remaining.Count;
            stages.Add(new FunnelStage(key, label, hint, count,
                Ratio(count, cohort.Count),
                previous is > 0 ? 1 - (double)count / previous.Value : null));
            previous = count;
        }
        return stages;
    }

    public static Lifecycle LifecycleFor(IntelScope scope)
    {
        var data = scope.Data;
        var words = scope.Words;
        var kept = words.Where(w => w.State != WordState.Deleted).ToList();

        var studiedIds = scope.SessionsIn()
            .SelectMany(s => s.Items).Where(i => i.Attempts > 0 && i.WordId != null)
            .Select(i => i.WordId!.Value).ToHashSet();

        var byStage = Skills.Select(skill =>
        {
            var here = kept.Where(w => w.State == WordState.Learning && w.CurrentSkill == skill).ToList();
            var due = here.Count(w => Attention.DueSince(data, w) is not null);
            var overdue = Attention.Overdue(data, here);
            return new StageCount(Wire(skill), here.Count - due, due - overdue, overdue);
        }).ToList();

        var first = data.Day(scope.Period.From);
        var last = data.Day(scope.Period.To.AddTicks(-1));
        var events = words.SelectMany(w => w.Events).Where(e => scope.Period.Contains(e.CreatedAt)).ToList();
        var added = words.Where(w => scope.Period.Contains(w.AddedAt)).ToLookup(w => data.Day(w.AddedAt));
        var passed = events.Where(e => e.Type == WordEventType.SkillPassed).ToLookup(e => data.Day(e.CreatedAt));
        var activated = events.Where(e => e.Type == WordEventType.EnteredActive).ToLookup(e => data.Day(e.CreatedAt));

        var trend = new List<LifecyclePoint>();
        for (var d = first; d <= last; d = d.AddDays(1))
            trend.Add(new(d.ToString("yyyy-MM-dd"), added[d].Count(), passed[d].Count(), activated[d].Count()));

        var dueTotal = byStage.Sum(s => s.Due + s.Overdue);

        return new Lifecycle(
            Added: words.Count(w => scope.Period.Contains(w.AddedAt)),
            Studied: studiedIds.Count,
            Learning: kept.Count(w => w.State == WordState.Learning),
            Mature: kept.Count(w => w.MaturedAt is not null),
            Active: kept.Count(w => w.State == WordState.Active),
            Archived: kept.Count(w => w.State == WordState.Archived),
            Deleted: words.Count(w => w.State == WordState.Deleted),
            Due: dueTotal,
            Overdue: byStage.Sum(s => s.Overdue),
            ByStage: byStage,
            Trend: trend);
    }

    // ── Skills ───────────────────────────────────────────────────────────────

    public static SkillSummary SkillSummaryFor(IntelScope scope, SkillType skill, bool previous = false)
    {
        var data = scope.Data;
        var period = scope.Period;
        bool In(DateTimeOffset t) => previous ? period.PrevContains(t) : period.Contains(t);

        var sessions = scope.Sessions.Where(s => s.Skill == skill && In(s.StartedAt)).ToList();
        var real = sessions.Where(s => !s.IsPractice).ToList();
        var abandonedEvents = scope.Events.Count(e => e.Name == AnalyticsEventNames.SessionAbandoned
                                                      && e.Skill == skill && In(e.OccurredAt));
        var abandoned = abandonedEvents + real.Count(data.IsStale);
        var started = real.Count + abandonedEvents;

        var items = sessions.SelectMany(s => s.Items).ToList();
        var attempted = items.Where(i => i.Attempts > 0).ToList();
        var attempts = attempted.Sum(i => i.Attempts);
        var firstKnown = attempted.Where(i => i.FirstAttemptCorrect is not null).ToList();

        var decisions = scope.Words.SelectMany(w => w.Events)
            .Where(e => e.Skill == skill && In(e.CreatedAt)
                        && e.Type is WordEventType.SkillPassed or WordEventType.SkillFailed)
            .ToList();
        var passed = decisions.Count(e => e.Type == WordEventType.SkillPassed);
        var failed = decisions.Count - passed;

        var durations = real.Select(data.DurationMs).OfType<double>().ToList();
        var answerTimes = scope.Events
            .Where(e => e.Skill == skill && In(e.OccurredAt) && e.DurationMs is not null
                        && e.Name is AnalyticsEventNames.AnswerSubmitted
                            or AnalyticsEventNames.WritingEvaluated
                            or AnalyticsEventNames.SpeakingTurn)
            .Select(e => (double)e.DurationMs!.Value);

        return new SkillSummary(
            Wire(skill),
            Sessions: started,
            Completed: real.Count(s => s.IsComplete),
            Abandoned: abandoned,
            Practice: sessions.Count(s => s.IsPractice),
            Words: items.Where(i => i.WordId != null).Select(i => i.WordId).Distinct().Count(),
            Questions: attempted.Count,
            Attempts: attempts,
            Passed: passed,
            Failed: failed,
            FirstAttemptAccuracy: Ratio(firstKnown.Count(i => i.FirstAttemptCorrect == true), firstKnown.Count),
            // Every cleared item had exactly one right answer — its last — so
            // right answers over all answers needs no attempt log.
            OverallAccuracy: Ratio(attempted.Count(i => i.IsCleared), attempts),
            PassRate: Ratio(passed, decisions.Count),
            FailRate: Ratio(failed, decisions.Count),
            RetryRate: Ratio(attempted.Count(i => i.Attempts > 1), attempted.Count),
            AbandonmentRate: Ratio(abandoned, started),
            AvgSessionMs: Mean(durations),
            MedianSessionMs: Median(durations),
            MedianAnswerMs: Median(answerTimes));
    }

    // ── Failure analysis (admin brief §12) ───────────────────────────────────

    public static readonly IReadOnlyDictionary<string, (string Label, string Category)> ReasonCatalogue =
        new Dictionary<string, (string, string)>
        {
            ["misunderstood_context"] = ("فهم خاطئ للسياق", "learning"),
            ["vocabulary_meaning"] = ("مشكلة في معنى الكلمة", "learning"),
            ["too_difficult"] = ("المحتوى صعب — خُفّض المستوى", "content"),
            ["could_not_hear"] = ("صعوبة في السماع — إعادة تشغيل متكررة", "content"),
            ["no_answer"] = ("لم يُجب", "ux"),
            ["abandoned"] = ("انسحب قبل النهاية", "ux"),
            ["did_not_answer"] = ("لم يتكلم", "ux"),
            ["did_not_use_word"] = ("لم يستخدم الكلمة المطلوبة", "learning"),
            ["wrong_meaning"] = ("معنى خاطئ", "learning"),
            ["incorrect_usage"] = ("استخدام غير صحيح", "learning"),
            ["grammar"] = ("Grammar", "learning"),
            ["unclear"] = ("غير مفهوم / نطق", "learning"),
            ["missing_target_word"] = ("الكلمة غير موجودة في الجملة", "learning"),
            ["wrong_usage"] = ("استخدام خاطئ / Collocation", "learning"),
            ["unnatural"] = ("جملة غير طبيعية", "learning"),
            ["wrong_letters"] = ("حروف خاطئة", "learning"),
            ["failed_with_hints"] = ("فشل رغم التلميحات", "content"),
            ["ai_fallback"] = ("تقييم احتياطي — الـAI لم يجب", "ai"),
            ["unclassified"] = ("غير مصنف — قبل بدء التتبع", "unknown"),
        };

    public static readonly IReadOnlyDictionary<string, string> CategoryLabels = new Dictionary<string, string>
    {
        ["learning"] = "مشكلة تعلم",
        ["ux"] = "مشكلة UX",
        ["content"] = "مشكلة محتوى",
        ["ai"] = "مشكلة AI",
        ["unknown"] = "غير مصنف",
    };

    /// <summary>
    /// Why things failed at one skill, one reason per failure.
    /// </summary>
    /// <remarks>
    /// Each failure is filed once, under the first reason that explains it, so
    /// the bars add up to the failures and can be compared. The order of the
    /// checks below is that precedence: an AI that did not answer explains a
    /// failure before the sentence does.
    /// </remarks>
    public static SkillFailures FailuresFor(IntelScope scope, SkillType skill, IntelPeriod? periodOverride = null)
    {
        var p = periodOverride ?? scope.Period;
        var counts = new Dictionary<string, int>();
        void Count(string key, int n = 1) { if (n > 0) counts[key] = counts.GetValueOrDefault(key) + n; }

        var sessions = scope.Sessions.Where(s => s.Skill == skill && p.Contains(s.StartedAt) && !s.IsPractice).ToList();
        var events = scope.Events.Where(e => p.Contains(e.OccurredAt)).ToList();

        Count("abandoned", events.Count(e => e.Name == AnalyticsEventNames.SessionAbandoned && e.Skill == skill)
                           + sessions.Count(scope.Data.IsStale));

        var lowered = scope.LevelChanges
            .Where(l => l.Skill == skill && p.Contains(l.CreatedAt)
                        && l.ChangeType == LevelChangeType.UserManualChange
                        && l.PreviousLevel is not null && l.NewLevel is not null
                        && l.NewLevel < l.PreviousLevel)
            .ToList();

        switch (skill)
        {
            case SkillType.Reading:
            case SkillType.Listening:
            {
                var replays = events.Where(e => e.Name == AnalyticsEventNames.AudioReplayed && e.SessionId != null)
                    .GroupBy(e => e.SessionId!.Value).ToDictionary(g => g.Key, g => g.Count());

                foreach (var s in sessions)
                {
                    var wrong = s.Items.Where(i => i.FirstAttemptCorrect == false).ToList();
                    var struggled = skill == SkillType.Listening
                                    && replays.GetValueOrDefault(s.Id) >= scope.Data.Options.ReplayStruggle;
                    var loweredHere = lowered.Any(l => l.UserId == s.UserId
                                                       && l.CreatedAt >= s.StartedAt
                                                       && (s.CompletedAt is null || l.CreatedAt <= s.CompletedAt));
                    foreach (var item in wrong)
                    {
                        if (struggled) Count("could_not_hear");
                        else if (loweredHere) Count("too_difficult");
                        else if (item.Type == SessionItemType.Comprehension) Count("misunderstood_context");
                        else Count("vocabulary_meaning");
                    }

                    if (s.IsComplete) Count("no_answer", s.Items.Count(i => i.Attempts == 0));
                }
                break;
            }

            case SkillType.Speaking:
            {
                var evaluated = events.Where(e => e.Name == AnalyticsEventNames.SpeakingWordEvaluated && e.Result == "fail").ToList();
                foreach (var e in evaluated)
                {
                    if (Prop<bool?>(e, "learnerSpoke") == false) Count("did_not_answer");
                    else if (Prop<bool?>(e, "evaluated") == false) Count("did_not_use_word");
                    else if (Prop<bool?>(e, "used") == false && Prop<bool?>(e, "recordedAsUsed") != true) Count("did_not_use_word");
                    else if (Prop<bool?>(e, "meaningCorrect") == false) Count("wrong_meaning");
                    else if (Prop<bool?>(e, "majorGrammarProblem") == true) Count("grammar");
                    else if (Prop<bool?>(e, "understandable") == false) Count("unclear");
                    else Count("incorrect_usage");
                }
                Count("unclassified", Unclassified(scope, skill, p, evaluated.Count));
                break;
            }

            case SkillType.Writing:
            {
                var evaluated = events.Where(e => e.Name == AnalyticsEventNames.WritingEvaluated && e.Result == "fail").ToList();
                foreach (var e in evaluated)
                {
                    if (Prop<bool?>(e, "fromFallback") == true) Count("ai_fallback");
                    else if (Prop<bool?>(e, "usedWord") == false) Count("missing_target_word");
                    else if (Prop<bool?>(e, "meaningCorrect") == false) Count("wrong_meaning");
                    else if (Prop<bool?>(e, "usageCorrect") == false) Count("wrong_usage");
                    else if (Prop<bool?>(e, "understandable") == false) Count("unnatural");
                    else if (Prop<bool?>(e, "grammarIssue") == true) Count("grammar");
                    else Count("unnatural");
                }
                // Before attempts were logged, a wrong sentence left only the
                // item's first-attempt flag behind.
                if (evaluated.Count == 0)
                    Count("unclassified", sessions.SelectMany(s => s.Items).Count(i => i.FirstAttemptCorrect == false));
                break;
            }

            case SkillType.Spelling:
            {
                var hints = events.Where(e => e.Name == AnalyticsEventNames.HintUsed && e.SessionId != null)
                    .Select(e => (e.SessionId, e.WordId)).ToHashSet();
                var fails = events.Where(e => e.Name == AnalyticsEventNames.AnswerSubmitted
                                              && e.Skill == SkillType.Spelling && e.Result == "fail").ToList();
                if (fails.Count > 0)
                {
                    foreach (var e in fails)
                        Count(hints.Contains((e.SessionId, e.WordId)) ? "failed_with_hints" : "wrong_letters");
                }
                else
                {
                    Count("wrong_letters", sessions.SelectMany(s => s.Items).Count(i => i.FirstAttemptCorrect == false));
                }
                break;
            }
        }

        var reasons = counts
            .Select(kv => new Reason(kv.Key, ReasonCatalogue[kv.Key].Label, ReasonCatalogue[kv.Key].Category, kv.Value))
            .OrderByDescending(r => r.Count)
            .ToList();

        return new SkillFailures(Wire(skill), reasons.Sum(r => r.Count), reasons);
    }

    /// <summary>Failures the word log recorded with no evaluation event beside them.</summary>
    private static int Unclassified(IntelScope scope, SkillType skill, IntelPeriod p, int classified)
    {
        var failed = scope.Words.SelectMany(w => w.Events)
            .Count(e => e.Skill == skill && e.Type == WordEventType.SkillFailed && p.Contains(e.CreatedAt));
        return Math.Max(0, failed - classified);
    }

    public static IReadOnlyList<CategoryCount> Categories(IEnumerable<SkillFailures> failures) =>
        failures.SelectMany(f => f.Reasons)
            .GroupBy(r => r.Category)
            .Select(g => new CategoryCount(g.Key, CategoryLabels[g.Key], g.Sum(r => r.Count)))
            .OrderByDescending(c => c.Count)
            .ToList();

    // ── Content interaction (admin brief §10) ────────────────────────────────

    public static IReadOnlyList<Metric> ContentFor(IntelScope scope, SkillType skill)
    {
        var p = scope.Period;
        var sessions = scope.Sessions.Where(s => s.Skill == skill && p.Contains(s.StartedAt) && !s.IsPractice).ToList();
        var events = scope.Events.Where(e => p.Contains(e.OccurredAt) && e.Skill == skill).ToList();
        int N(string name) => events.Count(e => e.Name == name);
        var completed = sessions.Count(s => s.IsComplete);
        var levelChanges = scope.LevelChanges.Count(l => l.Skill == skill && p.Contains(l.CreatedAt)
                                                        && l.ChangeType == LevelChangeType.UserManualChange);
        var answerMs = Median(events.Where(e => e.DurationMs != null && e.Name == AnalyticsEventNames.AnswerSubmitted)
            .Select(e => (double)e.DurationMs!.Value));
        var common = new List<Metric>
        {
            new("completion", "إكمال التمرين", Ratio(completed, sessions.Count), "pct"),
            new("level_changes", "تغييرات المستوى", levelChanges, "int", "تغييرات يدوية خلال التمرين"),
        };

        switch (skill)
        {
            case SkillType.Reading:
            {
                var opens = events.Where(e => e.Name == AnalyticsEventNames.TranslationOpened).ToList();
                var withOpen = opens.Where(e => e.SessionId != null).Select(e => e.SessionId).Distinct().Count();
                var glossary = sessions.Sum(s => GlossarySize(s));
                return
                [
                    new("translation_opens", "فتح الترجمة", opens.Count, "int"),
                    new("translations_per_session", "ترجمات لكل Session", Ratio(opens.Count, sessions.Count), "num"),
                    new("words_translated", "كلمات مختلفة تُرجمت",
                        opens.Select(e => Prop<string>(e, "word")?.ToLowerInvariant()).Where(w => w != null).Distinct().Count(), "int"),
                    new("translation_ratio", "Translation ratio", Ratio(opens.Count, glossary), "pct",
                        "الترجمات المفتوحة نسبةً إلى الكلمات القابلة للترجمة في النصوص"),
                    new("sessions_with_translation", "Sessions فيها ترجمة", Ratio(withOpen, sessions.Count), "pct"),
                    .. common,
                    new("questions_answered", "أسئلة أُجيب عنها", sessions.SelectMany(s => s.Items).Count(i => i.Attempts > 0), "int"),
                    new("median_answer", "وقت الإجابة (وسيط)", answerMs, "ms"),
                ];
            }

            case SkillType.Listening:
            {
                var plays = N(AnalyticsEventNames.AudioPlayed);
                var replays = N(AnalyticsEventNames.AudioReplayed);
                return
                [
                    new("audio_plays", "تشغيل الصوت", plays, "int"),
                    new("audio_replays", "إعادة التشغيل", replays, "int"),
                    new("replays_per_session", "إعادات لكل Session", Ratio(replays, sessions.Count), "num"),
                    new("audio_completion", "اكتمال الاستماع", Ratio(N(AnalyticsEventNames.AudioCompleted), plays + replays), "pct",
                        "مرات الوصول إلى نهاية المقطع من كل تشغيل"),
                    new("pauses", "الإيقاف المؤقت", N(AnalyticsEventNames.AudioPaused), "int"),
                    .. common,
                    new("time_before_answer", "الوقت قبل الإجابة (وسيط)", answerMs, "ms"),
                ];
            }

            case SkillType.Speaking:
            {
                var turns = scope.Events.Where(e => p.Contains(e.OccurredAt) && e.Name == AnalyticsEventNames.SpeakingTurn).ToList();
                var evaluated = scope.Events.Where(e => p.Contains(e.OccurredAt) && e.Name == AnalyticsEventNames.SpeakingWordEvaluated).ToList();
                return
                [
                    new("turns", "إجابات المتعلم", turns.Count, "int"),
                    new("target_usage", "استخدام الكلمة المطلوبة", Ratio(turns.Count(t => Prop<int?>(t, "wordsUsed") > 0), turns.Count), "pct",
                        "الإجابات التي استخدمت كلمة مستهدفة واحدة على الأقل"),
                    new("successful_usage", "استخدام ناجح", Ratio(evaluated.Count(e => e.Result == "pass"), evaluated.Count), "pct"),
                    new("turns_per_session", "إجابات لكل محادثة", Ratio(turns.Count, sessions.Count), "num"),
                    new("response_time", "زمن الرد (وسيط)", Median(turns.Where(t => t.DurationMs != null).Select(t => (double)t.DurationMs!.Value)), "ms"),
                    .. common,
                ];
            }

            case SkillType.Writing:
            {
                var evaluated = scope.Events.Where(e => p.Contains(e.OccurredAt) && e.Name == AnalyticsEventNames.WritingEvaluated).ToList();
                var tasks = evaluated.Select(e => (e.SessionId, e.WordId)).Distinct().Count();
                var feedback = events.Where(e => e.Name == AnalyticsEventNames.FeedbackViewed).ToList();
                return
                [
                    new("sentences", "جمل أُرسلت", evaluated.Count, "int"),
                    new("attempts_per_task", "محاولات لكل كلمة", Ratio(evaluated.Count, tasks), "num"),
                    new("retries", "إعادة المحاولة", evaluated.Count(e => e.Attempt > 1), "int"),
                    new("feedback_viewed", "مشاهدة AI Feedback", feedback.Count, "int"),
                    new("feedback_time", "وقت قراءة الـFeedback (وسيط)", Median(feedback.Where(f => f.DurationMs != null).Select(f => (double)f.DurationMs!.Value)), "ms"),
                    new("response_time", "وقت كتابة الجملة (وسيط)", Median(evaluated.Where(e => e.DurationMs != null).Select(e => (double)e.DurationMs!.Value)), "ms"),
                    .. common,
                ];
            }

            default:
            {
                var answers = events.Where(e => e.Name == AnalyticsEventNames.AnswerSubmitted).ToList();
                var items = sessions.SelectMany(s => s.Items).Where(i => i.Attempts > 0).ToList();
                return
                [
                    new("attempts", "المحاولات", items.Sum(i => i.Attempts), "int"),
                    new("wrong_attempts", "محاولات خاطئة", items.Sum(i => i.Attempts) - items.Count(i => i.IsCleared), "int"),
                    new("hints", "التلميحات", N(AnalyticsEventNames.HintUsed), "int"),
                    new("hints_per_word", "تلميحات لكل كلمة", Ratio(N(AnalyticsEventNames.HintUsed), items.Count), "num"),
                    new("retry_rate", "Retry", Ratio(items.Count(i => i.Attempts > 1), items.Count), "pct"),
                    new("success", "نجاح من أول محاولة", Ratio(items.Count(i => i.FirstAttemptCorrect == true), items.Count), "pct"),
                    new("time", "الوقت لكل كلمة (وسيط)", Median(answers.Where(a => a.DurationMs != null).Select(a => (double)a.DurationMs!.Value)), "ms"),
                    .. common,
                ];
            }
        }
    }

    private static int GlossarySize(SkillSession s)
    {
        if (string.IsNullOrEmpty(s.GlossaryJson)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(s.GlossaryJson);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    public static IReadOnlyList<(string Word, int Count)> MostTranslated(IntelScope scope, int take = 10) =>
        scope.EventsIn(AnalyticsEventNames.TranslationOpened)
            .Select(e => Prop<string>(e, "word")?.ToLowerInvariant())
            .OfType<string>()
            .GroupBy(w => w)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(x => x.Item2)
            .Take(take)
            .ToList();

    // ── Levels (admin brief §11) ─────────────────────────────────────────────

    public static (IReadOnlyList<LevelFlow> Flows, IReadOnlyList<LevelSkillSummary> BySkill,
        IReadOnlyList<LevelRow> ByLevel, IReadOnlyList<LevelDistribution> Distribution) Levels(IntelScope scope)
    {
        var changes = scope.LevelChanges
            .Where(l => scope.Period.Contains(l.CreatedAt) && l.ChangeType != LevelChangeType.Placement
                        && (scope.Skill is null || l.Skill == scope.Skill)
                        && l.PreviousLevel is not null && l.NewLevel is not null)
            .ToList();

        var flows = changes
            .GroupBy(l => (l.Skill, l.PreviousLevel, l.NewLevel))
            .Select(g => new LevelFlow(Wire(g.Key.Skill), g.Key.PreviousLevel!.Value.ToWire(), g.Key.NewLevel!.Value.ToWire(),
                g.Count(), g.Key.NewLevel > g.Key.PreviousLevel ? "up" : g.Key.NewLevel < g.Key.PreviousLevel ? "down" : "same"))
            .OrderByDescending(f => f.Count)
            .ToList();

        var bySkill = Skills.Where(s => s != SkillType.Spelling).Select(s =>
        {
            var mine = changes.Where(l => l.Skill == s).ToList();
            return new LevelSkillSummary(Wire(s),
                mine.Count(l => l.NewLevel > l.PreviousLevel),
                mine.Count(l => l.NewLevel < l.PreviousLevel),
                mine.Count(l => l.ChangeType == LevelChangeType.UserManualChange),
                mine.Count(l => l.ChangeType == LevelChangeType.SystemValidatedChange));
        }).ToList();

        var byLevel = Enum.GetValues<CefrLevel>().Select(level => new LevelRow(level.ToWire(),
                changes.Count(l => l.PreviousLevel == level && l.NewLevel < l.PreviousLevel),
                changes.Count(l => l.PreviousLevel == level && l.NewLevel > l.PreviousLevel)))
            .Where(r => r.Downgrades + r.Upgrades > 0)
            .ToList();

        var distribution = Skills.Where(s => s != SkillType.Spelling).Select(s => new LevelDistribution(Wire(s),
            Enum.GetValues<CefrLevel>().Select(level => new LevelCount(level.ToWire(),
                    scope.Learners.Count(u => u.SkillLevels.Any(l => l.Skill == s && l.UserSelectedLevel == level)),
                    scope.Learners.Count(u => u.SkillLevels.Any(l => l.Skill == s && l.SystemAssessedLevel == level))))
                .Where(c => c.Selected + c.Assessed > 0)
                .ToList())).ToList();

        return (flows, bySkill, byLevel, distribution);
    }

    // ── Time (admin brief §13) ───────────────────────────────────────────────

    public static readonly (string Label, double MaxMs)[] SessionBuckets =
    [
        ("أقل من دقيقة", 60_000), ("1–3 دقائق", 180_000), ("3–5 دقائق", 300_000),
        ("5–10 دقائق", 600_000), ("10–20 دقيقة", 1_200_000), ("20–40 دقيقة", 2_400_000),
        ("أكثر من 40 دقيقة", double.MaxValue),
    ];

    public static IReadOnlyList<Bucket> Distribution(IEnumerable<double> values, (string Label, double MaxMs)[] buckets)
    {
        var list = values.ToList();
        var result = new List<Bucket>();
        var floor = double.MinValue;
        foreach (var (label, max) in buckets)
        {
            result.Add(new Bucket(label, list.Count(v => v > floor && v <= max)));
            floor = max;
        }
        return result;
    }

    public static object Time(IntelScope scope)
    {
        var data = scope.Data;
        var sessions = scope.SessionsIn().Where(s => !s.IsPractice).ToList();
        var durations = sessions.Select(data.DurationMs).OfType<double>().ToList();
        var screen = ScreenTime(scope.Events);
        var learningMs = sessions.Select(s => LearningMs(data, s, screen)).OfType<double>().Sum();
        var events = scope.Events.Where(e => scope.Period.Contains(e.OccurredAt)).ToList();

        var appMs = events.Where(e => e.Name == AnalyticsEventNames.AppBackgrounded)
            .Sum(e => (double)(e.DurationMs ?? 0));

        var answers = events.Where(e => e.DurationMs != null && e.Name is AnalyticsEventNames.AnswerSubmitted
            or AnalyticsEventNames.WritingEvaluated or AnalyticsEventNames.SpeakingTurn).ToList();

        var firstAnswer = sessions.Select(s =>
            {
                var first = events.Where(e => e.SessionId == s.Id && e.Name is AnalyticsEventNames.AnswerSubmitted
                        or AnalyticsEventNames.WritingEvaluated or AnalyticsEventNames.SpeakingTurn)
                    .OrderBy(e => e.OccurredAt).FirstOrDefault();
                return first is null ? (double?)null : (first.OccurredAt - s.StartedAt).TotalMilliseconds;
            })
            .OfType<double>().Where(v => v >= 0 && v < 30 * 60_000).ToList();

        var opens = events.Where(e => e.Name == AnalyticsEventNames.AppOpened).ToList();
        var toLearning = opens.Select(o =>
            {
                var next = scope.Sessions.Where(s => s.UserId == o.UserId && s.StartedAt >= o.OccurredAt
                                                     && s.StartedAt - o.OccurredAt < TimeSpan.FromMinutes(30))
                    .OrderBy(s => s.StartedAt).FirstOrDefault();
                return next is null ? (double?)null : (next.StartedAt - o.OccurredAt).TotalMilliseconds;
            })
            .OfType<double>().ToList();

        var perWord = sessions.Where(s => s.IsComplete && data.DurationMs(s) is not null)
            .Select(s => (data.DurationMs(s)!.Value, Words: s.Items.Where(i => i.WordId != null).Select(i => i.WordId).Distinct().Count()))
            .Where(x => x.Words > 0)
            .Select(x => x.Value / x.Words);

        var ai = events.Where(e => e.Name == AnalyticsEventNames.AiCall && e.DurationMs != null).ToList();
        var feedback = events.Where(e => e.Name == AnalyticsEventNames.FeedbackViewed && e.DurationMs != null).ToList();

        return new
        {
            metrics = new List<Metric>
            {
                new("avg_session", "متوسط مدة الـSession", Mean(durations), "ms", "Sessions المكتملة خلال ساعة"),
                new("median_session", "وسيط مدة الـSession", Median(durations), "ms"),
                new("learning_time", "وقت التعلم الفعلي", learningMs, "ms", "داخل التمارين فقط"),
                new("app_time", "وقت التطبيق", appMs > 0 ? appMs : null, "ms", "كل الوقت والتطبيق مفتوح — من الهاتف"),
                new("time_per_question", "الوقت لكل سؤال (وسيط)", Median(answers.Select(a => (double)a.DurationMs!.Value)), "ms"),
                new("time_per_word", "الوقت لكل كلمة (وسيط)", Median(perWord), "ms"),
                new("time_to_learning", "من فتح التطبيق إلى بدء التعلم", Median(toLearning), "ms"),
                new("time_to_first_answer", "من بدء التمرين إلى أول إجابة", Median(firstAnswer), "ms"),
                new("time_to_submit", "وقت كتابة جملة Writing", Median(answers.Where(a => a.Name == AnalyticsEventNames.WritingEvaluated).Select(a => (double)a.DurationMs!.Value)), "ms"),
                new("feedback_reading", "وقت قراءة الـFeedback", Median(feedback.Select(f => (double)f.DurationMs!.Value)), "ms"),
                new("ai_p50", "زمن استجابة الـAI (وسيط)", Median(ai.Select(a => (double)a.DurationMs!.Value)), "ms"),
                new("ai_p95", "زمن استجابة الـAI (P95)", Percentile(ai.Select(a => (double)a.DurationMs!.Value), 0.95), "ms"),
            },
            sessionDistribution = Distribution(durations, SessionBuckets),
            answerDistribution = Distribution(answers.Select(a => (double)a.DurationMs!.Value),
            [
                ("< 5 ث", 5_000), ("5–15 ث", 15_000), ("15–30 ث", 30_000), ("30–60 ث", 60_000),
                ("1–2 د", 120_000), ("> 2 د", double.MaxValue),
            ]),
            answerBySkill = Skills.Select(s => new
            {
                skill = Wire(s),
                median = Median(answers.Where(a => a.Skill == s).Select(a => (double)a.DurationMs!.Value)),
                p90 = Percentile(answers.Where(a => a.Skill == s).Select(a => (double)a.DurationMs!.Value), 0.9),
                count = answers.Count(a => a.Skill == s),
            }).ToList(),
            aiByOperation = ai.GroupBy(a => Prop<string>(a, "operation") ?? "other").Select(g => new
            {
                operation = g.Key,
                calls = g.Count(),
                errors = g.Count(a => a.Result == "error"),
                median = Median(g.Select(a => (double)a.DurationMs!.Value)),
                p95 = Percentile(g.Select(a => (double)a.DurationMs!.Value), 0.95),
            }).OrderByDescending(x => x.calls).ToList(),
        };
    }

    // ── Retention (admin brief §14) ──────────────────────────────────────────

    /// <summary>
    /// Of the learners who joined at least <paramref name="n"/> days before
    /// <paramref name="asOf"/>, the share active on day n or later.
    /// </summary>
    /// <remarks>
    /// "On day n or later" rather than "on exactly day n": with tens of learners
    /// the exact-day figure swings by a third when one person studies on a
    /// Thursday instead of a Wednesday, and the question the product is asking
    /// is whether people come back at all.
    /// </remarks>
    public static double? RetentionAt(IntelScope scope, int n, DateTimeOffset asOf)
    {
        var data = scope.Data;
        var eligible = scope.Learners.Where(u => asOf - u.CreatedAt >= TimeSpan.FromDays(n)).ToList();
        if (eligible.Count == 0) return null;

        var returned = eligible.Count(u =>
        {
            var start = data.Day(u.CreatedAt);
            var limit = data.Day(asOf);
            return data.ActiveDaysOf(u.Id).Any(d => d.DayNumber - start.DayNumber >= n && d < limit);
        });
        return (double)returned / eligible.Count;
    }

    public static IReadOnlyList<CohortRow> Cohorts(IntelScope scope, int weeks = 8)
    {
        var data = scope.Data;
        var today = data.Day(data.Now);
        var rows = new List<CohortRow>();

        // Weeks start on Saturday, the start of the working week the audience
        // keeps (ADR-052's same reasoning as the fixed offset).
        var thisWeek = today.AddDays(-(((int)today.DayOfWeek + 1) % 7));

        for (var w = weeks - 1; w >= 0; w--)
        {
            var start = thisWeek.AddDays(-7 * w);
            var cohort = scope.Learners.Where(u => data.Day(u.CreatedAt) >= start && data.Day(u.CreatedAt) < start.AddDays(7)).ToList();

            var cells = new List<double?>();
            for (var k = 1; k <= weeks; k++)
            {
                var from = start.AddDays(7 * k);
                if (from > today || cohort.Count == 0)
                {
                    cells.Add(null);
                    continue;
                }
                var active = cohort.Count(u => data.ActiveDaysOf(u.Id).Any(d => d >= from && d < from.AddDays(7)));
                cells.Add((double)active / cohort.Count);
            }

            rows.Add(new CohortRow(start.ToString("yyyy-MM-dd"), cohort.Count, cells));
        }
        return rows;
    }

    public static readonly (string Label, double MaxHours)[] RecallBuckets =
    [
        ("< يوم", 24), ("1–2 يوم", 48), ("2–3 أيام", 72), ("3–5 أيام", 120), ("5–7 أيام", 168),
        ("1–2 أسبوع", 336), ("2–4 أسابيع", 672), ("> 4 أسابيع", double.MaxValue),
    ];

    public sealed record RecallSample(Guid UserId, SkillType? Skill, double GapHours, bool Success, string Source);

    /// <summary>
    /// Every moment a learner was asked to recall a word, with how long it had been.
    /// </summary>
    /// <remarks>
    /// A skill decision is a recall: passing requires the first attempt
    /// (ADR-015), so the event says whether the word was remembered when it came
    /// back. So is a weekly review item's first attempt. The gap is measured
    /// from the word's previous meeting — its last decision, or the day it was
    /// added. Learners whose schedule the Owner brought forward are left out:
    /// their gaps are a moved clock, not forgetting.
    /// </remarks>
    public static IReadOnlyList<RecallSample> RecallSamples(IntelScope scope)
    {
        var moved = scope.Data.Activity.Where(a => a.Type == ActivityType.ScheduleAdvanced)
            .Select(a => a.UserId).ToHashSet();
        var samples = new List<RecallSample>();

        foreach (var word in scope.Words.Where(w => !moved.Contains(w.UserId)))
        {
            var anchors = word.Events
                .Where(e => e.Type is WordEventType.SkillPassed or WordEventType.SkillFailed)
                .OrderBy(e => e.CreatedAt)
                .ToList();
            var previous = word.AddedAt;

            foreach (var e in anchors)
            {
                if (scope.Period.Contains(e.CreatedAt))
                    samples.Add(new(word.UserId, e.Skill, (e.CreatedAt - previous).TotalHours,
                        e.Type == WordEventType.SkillPassed, "skill"));
                previous = e.CreatedAt;
            }
        }

        var byWord = scope.Words.ToDictionary(w => w.Id);
        foreach (var review in scope.Reviews.Where(r => !r.IsPractice && !moved.Contains(r.UserId)))
        {
            foreach (var item in review.Items.Where(i => i.FirstAttemptCorrect is not null && i.AnsweredAt is not null))
            {
                if (!scope.Period.Contains(item.AnsweredAt!.Value) || !byWord.TryGetValue(item.WordId, out var word)) continue;
                var before = word.Events.Where(e => e.CreatedAt < review.StartedAt
                                                    && e.Type is WordEventType.SkillPassed or WordEventType.SkillFailed)
                    .Select(e => e.CreatedAt).DefaultIfEmpty(word.AddedAt).Max();
                samples.Add(new(review.UserId, null, (item.AnsweredAt.Value - before).TotalHours,
                    item.FirstAttemptCorrect == true, "review"));
            }
        }

        return samples;
    }

    public static IReadOnlyList<RecallPoint> RecallCurve(IEnumerable<RecallSample> samples)
    {
        var list = samples.ToList();
        var floor = 0d;
        var points = new List<RecallPoint>();
        foreach (var (label, max) in RecallBuckets)
        {
            var mine = list.Where(s => s.GapHours >= floor && s.GapHours < max).ToList();
            points.Add(new RecallPoint(label, floor, mine.Count, Ratio(mine.Count(s => s.Success), mine.Count)));
            floor = max;
        }
        return points;
    }

    public static object Recovery(IntelScope scope)
    {
        var recovered = 0;
        var failedPairs = 0;
        var days = new List<double>();

        foreach (var word in scope.Words)
        {
            foreach (var group in word.Events.Where(e => e.Skill != null).GroupBy(e => e.Skill))
            {
                var ordered = group.OrderBy(e => e.CreatedAt).ToList();
                var firstFail = ordered.FirstOrDefault(e => e.Type == WordEventType.SkillFailed && scope.Period.Contains(e.CreatedAt));
                if (firstFail is null) continue;
                failedPairs++;
                var pass = ordered.FirstOrDefault(e => e.Type == WordEventType.SkillPassed && e.CreatedAt > firstFail.CreatedAt);
                if (pass is null) continue;
                recovered++;
                days.Add((pass.CreatedAt - firstFail.CreatedAt).TotalDays);
            }
        }

        return new
        {
            failed = failedPairs,
            recovered,
            rate = Ratio(recovered, failedPairs),
            medianDays = Median(days),
        };
    }

    // ── Weekly review (admin brief §15) ──────────────────────────────────────

    public static object WeeklyReview(IntelScope scope)
    {
        var reviews = scope.Reviews.Where(r => !r.IsPractice && scope.Period.Contains(r.StartedAt)).ToList();
        var items = reviews.SelectMany(r => r.Items).Where(i => i.Attempts > 0).ToList();
        var completed = reviews.Where(r => r.IsComplete).ToList();
        var durations = completed.Where(r => r.CompletedAt - r.StartedAt <= TimeSpan.FromMinutes(scope.Data.Options.SessionCapMinutes))
            .Select(r => (r.CompletedAt!.Value - r.StartedAt).TotalMilliseconds).ToList();
        var words = scope.Words.ToDictionary(w => w.Id);

        var mostFailed = items.Where(i => i.FirstAttemptCorrect == false && words.ContainsKey(i.WordId))
            .GroupBy(i => (words[i.WordId].Text, words[i.WordId].Meaning))
            .Select(g => new WordFailure(g.Key.Text, g.Key.Meaning, g.Count(), g.Sum(i => i.Attempts),
                g.Select(i => words[i.WordId].UserId).Distinct().Count()))
            .OrderByDescending(w => w.Failures).Take(10).ToList();

        var trend = reviews.GroupBy(r => scope.Data.Day(r.StartedAt).AddDays(-(((int)scope.Data.Day(r.StartedAt).DayOfWeek + 1) % 7)))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var its = g.SelectMany(r => r.Items).Where(i => i.Attempts > 0).ToList();
                return new WeekPoint(g.Key.ToString("yyyy-MM-dd"), g.Count(),
                    Ratio(its.Count(i => i.IsCleared), its.Sum(i => i.Attempts)),
                    Ratio(its.Count(i => i.FirstAttemptCorrect == true), its.Count(i => i.FirstAttemptCorrect != null)));
            }).ToList();

        // Does reviewing help? The same word's first-attempt result the first
        // time it was reviewed against the next time — measurement only (R9).
        var repeated = scope.Reviews.Where(r => !r.IsPractice)
            .SelectMany(r => r.Items.Where(i => i.FirstAttemptCorrect != null).Select(i => (i.WordId, r.StartedAt, ok: i.FirstAttemptCorrect == true)))
            .GroupBy(x => x.WordId).Where(g => g.Count() >= 2)
            .Select(g => g.OrderBy(x => x.StartedAt).ToList()).ToList();

        return new
        {
            started = reviews.Count,
            completed = completed.Count,
            users = reviews.Select(r => r.UserId).Distinct().Count(),
            wordsReviewed = items.Select(i => i.WordId).Distinct().Count(),
            accuracy = Ratio(items.Count(i => i.IsCleared), items.Sum(i => i.Attempts)),
            firstAttemptAccuracy = Ratio(items.Count(i => i.FirstAttemptCorrect == true), items.Count(i => i.FirstAttemptCorrect != null)),
            medianMs = Median(durations),
            dropOff = Ratio(reviews.Count - completed.Count, reviews.Count),
            mostFailed,
            trend,
            afterEffect = new
            {
                words = repeated.Count,
                firstTime = Ratio(repeated.Count(r => r[0].ok), repeated.Count),
                nextTime = Ratio(repeated.Count(r => r[1].ok), repeated.Count),
            },
        };
    }

    // ── Friction (admin brief §16) ───────────────────────────────────────────

    public static IReadOnlyList<FrictionItem> Friction(IntelScope scope)
    {
        var o = scope.Data.Options;
        var p = scope.Period;
        var events = scope.Events.Where(e => p.Contains(e.OccurredAt)).ToList();
        var prevEvents = scope.Events.Where(e => p.PrevContains(e.OccurredAt)).ToList();
        var sessions = scope.SessionsIn().Where(s => !s.IsPractice).ToList();
        var prevSessions = scope.SessionsIn(previous: true).Where(s => !s.IsPractice).ToList();
        var items = new List<FrictionItem>();

        double? PerSession(IEnumerable<AnalyticsEvent> evs, string name, IEnumerable<SkillSession> ss, SkillType skill) =>
            Ratio(evs.Count(e => e.Name == name && e.Skill == skill), ss.Count(s => s.Skill == skill));

        int UsersWhere(Func<AnalyticsEvent, bool> predicate) =>
            events.Where(predicate).Select(e => e.UserId).Distinct().Count();

        string Severity(double? value, double warn, double alert) =>
            value is null ? "none" : value >= alert ? "high" : value >= warn ? "medium" : "low";

        // Replays per listening session.
        var replay = PerSession(events, AnalyticsEventNames.AudioReplayed, sessions, SkillType.Listening);
        var struggling = events.Where(e => e.Name == AnalyticsEventNames.AudioReplayed && e.SessionId != null)
            .GroupBy(e => (e.UserId, e.SessionId)).Where(g => g.Count() >= o.ReplayStruggle)
            .Select(g => g.Key.UserId).Distinct().Count();
        items.Add(new("listening_replay", "إعادة تشغيل الصوت في Listening",
            "متوسط إعادات التشغيل لكل Session — المرتفع يعني أن الصوت سريع أو صعب",
            "LISTENING", replay, "num", PerSession(prevEvents, AnalyticsEventNames.AudioReplayed, prevSessions, SkillType.Listening),
            struggling, Severity(replay, 2, 4), "content"));

        var translations = PerSession(events, AnalyticsEventNames.TranslationOpened, sessions, SkillType.Reading);
        items.Add(new("reading_translation", "تكرار فتح الترجمة في Reading",
            "ترجمات مفتوحة لكل Session — المرتفع يعني أن مستوى النص أعلى من المتعلم",
            "READING", translations, "num",
            PerSession(prevEvents, AnalyticsEventNames.TranslationOpened, prevSessions, SkillType.Reading),
            UsersWhere(e => e.Name == AnalyticsEventNames.TranslationOpened), Severity(translations, 4, 8), "content"));

        foreach (var skill in Skills)
        {
            var summary = SkillSummaryFor(scope, skill);
            var prev = SkillSummaryFor(scope, skill, previous: true);
            var affected = events.Where(e => e.Name == AnalyticsEventNames.SessionAbandoned && e.Skill == skill)
                .Select(e => e.UserId)
                .Concat(sessions.Where(s => s.Skill == skill && scope.Data.IsStale(s)).Select(s => s.UserId))
                .Distinct().Count();
            // One abandoned session out of one is 100% and means nothing; a
            // rate is only judged once there are enough sessions behind it.
            items.Add(new($"abandon_{skill.ToWire().ToLowerInvariant()}", $"الانسحاب من {skill}",
                "تمارين بدأت ولم تكتمل", Wire(skill), summary.AbandonmentRate, "pct", prev.AbandonmentRate,
                affected, summary.Sessions >= o.MinimumSample * 2 ? Severity(summary.AbandonmentRate, 0.25, 0.45) : "low", "ux"));
        }

        var exits = events.Count(e => e.Name == AnalyticsEventNames.ExerciseExited);
        items.Add(new("exercise_exit", "الخروج من التمرين", "ضغط رجوع أو أغلق التمرين قبل نهايته",
            null, exits, "int", prevEvents.Count(e => e.Name == AnalyticsEventNames.ExerciseExited),
            UsersWhere(e => e.Name == AnalyticsEventNames.ExerciseExited), Severity(Ratio(exits, sessions.Count), 0.2, 0.4), "ux"));

        var quick = events.Where(e => e.Name == AnalyticsEventNames.ScreenLeft && e.SessionId != null
                                      && e.DurationMs < o.ImmediateBackSeconds * 1000).ToList();
        items.Add(new("immediate_back", "رجوع فوري", $"غادر شاشة التمرين خلال أقل من {o.ImmediateBackSeconds} ثوانٍ",
            null, quick.Count, "int", null, quick.Select(e => e.UserId).Distinct().Count(),
            Severity(Ratio(quick.Count, sessions.Count), 0.1, 0.25), "ux"));

        var hints = Ratio(events.Count(e => e.Name == AnalyticsEventNames.HintUsed),
            sessions.Where(s => s.Skill == SkillType.Spelling).SelectMany(s => s.Items).Count(i => i.Attempts > 0));
        items.Add(new("hint_usage", "استخدام التلميحات في Spelling", "تلميحات لكل كلمة",
            "SPELLING", hints, "num", null, UsersWhere(e => e.Name == AnalyticsEventNames.HintUsed),
            Severity(hints, 1.5, 2.5), "content"));

        var allItems = sessions.SelectMany(s => s.Items).Where(i => i.Attempts > 0).ToList();
        var retry = Ratio(allItems.Count(i => i.Attempts > 1), allItems.Count);
        items.Add(new("retry", "إعادة المحاولة", "أسئلة احتاجت أكثر من محاولة", null, retry, "pct",
            Ratio(prevSessions.SelectMany(s => s.Items).Count(i => i.Attempts > 1), prevSessions.SelectMany(s => s.Items).Count(i => i.Attempts > 0)),
            sessions.Where(s => s.Items.Any(i => i.Attempts > 1)).Select(s => s.UserId).Distinct().Count(),
            Severity(retry, 0.3, 0.5), "learning"));

        var idle = events.Where(e => e.DurationMs > o.LongIdleSeconds * 1000 && e.Name is AnalyticsEventNames.AnswerSubmitted
            or AnalyticsEventNames.WritingEvaluated or AnalyticsEventNames.SpeakingTurn).ToList();
        items.Add(new("long_idle", "توقف طويل قبل الإجابة", $"إجابات استغرقت أكثر من {o.LongIdleSeconds / 60} دقيقة",
            null, idle.Count, "int", null, idle.Select(e => e.UserId).Distinct().Count(),
            Severity(Ratio(idle.Count, Math.Max(1, allItems.Count)), 0.05, 0.15), "ux"));

        var apiErrors = events.Count(e => e.Name == AnalyticsEventNames.ApiError);
        items.Add(new("api_errors", "أخطاء الاتصال بالخادم", "طلبات فشلت كما رآها الهاتف", null, apiErrors, "int",
            prevEvents.Count(e => e.Name == AnalyticsEventNames.ApiError), UsersWhere(e => e.Name == AnalyticsEventNames.ApiError),
            Severity(Ratio(apiErrors, Math.Max(1, sessions.Count)), 0.1, 0.3), "ux"));

        var aiCalls = events.Where(e => e.Name == AnalyticsEventNames.AiCall).ToList();
        var aiRate = Ratio(aiCalls.Count(e => e.Result == "error") + sessions.Count(s => s.UsedAiFallback), Math.Max(aiCalls.Count, sessions.Count));
        items.Add(new("ai_errors", "أخطاء الـAI والمحتوى الاحتياطي", "طلبات فشلت أو Sessions استخدمت محتوى احتياطيًا",
            null, aiRate, "pct", null,
            aiCalls.Where(e => e.Result == "error").Select(e => e.UserId).Concat(sessions.Where(s => s.UsedAiFallback).Select(s => s.UserId)).Distinct().Count(),
            Severity(aiRate, 0.03, 0.1), "ai"));

        var empty = events.Count(e => e.Name == AnalyticsEventNames.EmptyStateShown);
        items.Add(new("empty_states", "شاشات فارغة", "مرات ظهور شاشة لا شيء فيها للمتعلم", null, empty, "int", null,
            UsersWhere(e => e.Name == AnalyticsEventNames.EmptyStateShown), Severity(Ratio(empty, Math.Max(1, sessions.Count)), 0.2, 0.5), "ux"));

        var rank = new Dictionary<string, int> { ["high"] = 0, ["medium"] = 1, ["low"] = 2, ["none"] = 3 };
        return items.OrderBy(i => rank[i.Severity]).ThenByDescending(i => i.AffectedUsers).ToList();
    }

    // ── Signals: what the Overview says is worth a look ──────────────────────

    public static IReadOnlyList<Signal> Signals(IntelScope scope, IReadOnlyList<SkillSummary> skills,
        IReadOnlyList<FunnelStage> funnel, IReadOnlyList<FrictionItem> friction)
    {
        var signals = new List<Signal>();
        var o = scope.Data.Options;

        foreach (var s in skills)
        {
            var skill = Enum.Parse<SkillType>(s.Skill, ignoreCase: true);
            var prev = SkillSummaryFor(scope, skill, previous: true);
            if (s.FirstAttemptAccuracy is { } now && prev.FirstAttemptAccuracy is { } before
                && s.Questions >= 10 && before - now >= 0.1)
                signals.Add(new("high", "learning", $"انخفاض دقة {skill}",
                    $"دقة المحاولة الأولى {now:P0} مقابل {before:P0} في الفترة السابقة", $"/learning/{s.Skill}"));

            if (s.AbandonmentRate is >= 0.3 && s.Sessions >= 5)
                signals.Add(new("medium", "ux", $"انسحاب مرتفع من {skill}",
                    $"{s.Abandoned} من {s.Sessions} تمرينًا لم تكتمل ({s.AbandonmentRate:P0})", $"/learning/{s.Skill}"));

            if (s.FirstAttemptAccuracy is < 0.5 && s.Questions >= 10)
                signals.Add(new("medium", "learning", $"{skill} صعبة على المتعلمين",
                    $"دقة المحاولة الأولى {s.FirstAttemptAccuracy:P0} فقط", $"/learning/{s.Skill}"));
        }

        var worst = funnel.Where(f => f.DropOff is not null && f.Key != "signup")
            .OrderByDescending(f => f.DropOff).FirstOrDefault();
        if (worst is { DropOff: >= 0.4 })
            signals.Add(new("medium", "ux", $"أكبر تسرب في Activation: {worst.Label}",
                $"{worst.DropOff:P0} من المستخدمين لم يصلوا إلى هذه المرحلة", "/"));

        foreach (var f in friction.Where(f => f.Severity == "high").Take(3))
            signals.Add(new("high", f.Category, f.Title, f.Description, $"/behavior/friction/{f.Key}"));

        var newFeedback = scope.Feedback.Count(f => f.Status == FeedbackStatus.New);
        if (newFeedback > 0)
            signals.Add(new("low", "feedback", $"{newFeedback} ملاحظات جديدة من المستخدمين", "لم تُقرأ بعد", "/feedback"));

        var attention = scope.Learners.Count(u => Attention.Reasons(scope.Data, u).Count > 0);
        if (attention > 0)
            signals.Add(new("low", "users", $"{attention} مستخدمين يحتاجون إلى انتباه", "قواعد قابلة للمراجعة — كل حالة مع دليلها", "/users?tab=attention"));

        var rank = new Dictionary<string, int> { ["high"] = 0, ["medium"] = 1, ["low"] = 2 };
        return signals.OrderBy(s => rank[s.Severity]).ToList();
    }
}
