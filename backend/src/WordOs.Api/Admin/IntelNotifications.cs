using System.Globalization;
using System.Text.Json;
using WordOs.Domain.Analytics;
using WordOs.Domain.Users;

namespace WordOs.Api.Admin;

/// <summary>
/// The reminder journey: planned → opened → lesson started → lesson finished
/// (brief page 8, ADR-132).
/// </summary>
/// <remarks>
/// <para>Reminders are alarms the phone sets for itself (ADR-076). So the
/// server can know what it <i>planned</i> (<c>reminders_planned</c>, recorded
/// when the phone asks for its list) and what was <i>opened</i>
/// (<c>notification_opened</c>, sent by the phone) — but never whether one
/// was delivered or seen and ignored. The page says so in words rather than
/// showing a delivery rate it cannot have.</para>
///
/// <para>An opening's kind is the planned slot it follows — the most recent
/// slot at or before the opening, within the gap to the next one, from the
/// learner's latest plan before it. The phone does not send the kind itself
/// yet; this is the best attribution the data allows, and it is labelled as
/// such.</para>
/// </remarks>
public static class IntelNotifications
{
    private sealed record Slot(DateTimeOffset At, string Kind);

    public static object For(IntelScope scope, Privacy privacy, object period)
    {
        var data = scope.Data;
        var p = scope.Period;
        var offset = TimeSpan.FromHours(data.Config.ReportingUtcOffsetHours);

        var plansByUser = scope.Events.Where(e => e.Name == AnalyticsEventNames.RemindersPlanned)
            .GroupBy(e => e.UserId)
            .ToDictionary(g => g.Key, g => g.OrderBy(e => e.OccurredAt).ToList());

        // The slots each learner's phone held for a day: those of the last
        // plan received before that day ended, since each plan replaces the last.
        // Each plan is read once: a long period asks the same plan for every day it covers.
        var parsed = new Dictionary<long, List<Slot>>();
        List<Slot> SlotsOf(AnalyticsEvent plan)
        {
            if (!parsed.TryGetValue(plan.Id, out var slots))
                parsed[plan.Id] = slots = Parse(plan, data.Config.MorningReminderHour, data.Config.EveningReminderHour, offset).ToList();
            return slots;
        }

        List<Slot> SlotsOn(Guid userId, DateOnly day)
        {
            if (!plansByUser.TryGetValue(userId, out var plans)) return [];
            var end = data.StartOf(day.AddDays(1));
            var plan = plans.LastOrDefault(e => e.OccurredAt < end);
            return plan is null ? [] : SlotsOf(plan)
                .Where(s => data.Day(s.At) == day && s.At > plan.OccurredAt).ToList();
        }

        string? KindOf(AnalyticsEvent opened)
        {
            var slots = SlotsOn(opened.UserId, data.Day(opened.OccurredAt))
                .Concat(SlotsOn(opened.UserId, data.Day(opened.OccurredAt).AddDays(-1)))
                .Where(s => s.At <= opened.OccurredAt && opened.OccurredAt - s.At < TimeSpan.FromHours(12))
                .OrderByDescending(s => s.At).ToList();
            return slots.FirstOrDefault()?.Kind;
        }

        (int Planned, int Users, Dictionary<string, int> Kinds) PlannedIn(bool previous)
        {
            var from = data.Day(previous ? p.PrevFrom : p.From);
            var to = data.Day((previous ? p.PrevTo : p.To).AddTicks(-1));
            var total = 0;
            var users = new HashSet<Guid>();
            var kinds = new Dictionary<string, int>();
            foreach (var u in scope.Learners.Where(u => plansByUser.ContainsKey(u.Id)))
                for (var d = from; d <= to; d = d.AddDays(1))
                    foreach (var s in SlotsOn(u.Id, d).Where(s => s.At <= data.Now))
                    {
                        total++;
                        users.Add(u.Id);
                        kinds[s.Kind] = kinds.GetValueOrDefault(s.Kind) + 1;
                    }
            return (total, users.Count, kinds);
        }

        var opened = scope.EventsIn(AnalyticsEventNames.NotificationOpened).ToList();
        var prevOpened = scope.EventsIn(AnalyticsEventNames.NotificationOpened, previous: true).ToList();

        SkillSessionLink? Follow(AnalyticsEvent o)
        {
            var window = TimeSpan.FromMinutes(data.Options.NotificationConversionMinutes);
            var s = data.SessionsOf(o.UserId)
                .Where(x => x.StartedAt >= o.OccurredAt && x.StartedAt - o.OccurredAt <= window)
                .OrderBy(x => x.StartedAt).FirstOrDefault();
            return s is null ? null : new SkillSessionLink(s.IsComplete);
        }

        var follows = opened.Select(o => (Open: o, Next: Follow(o), Kind: KindOf(o))).ToList();
        var prevFollows = prevOpened.Select(Follow).ToList();
        var converted = follows.Count(f => f.Next is not null);
        var completed = follows.Count(f => f.Next is { Completed: true });

        var planned = PlannedIn(false);
        var prevPlanned = PlannedIn(true);

        var kinds = planned.Kinds.Keys.Concat(follows.Select(f => f.Kind ?? "UNKNOWN")).Distinct()
            .Select(k => new
            {
                kind = k,
                planned = planned.Kinds.GetValueOrDefault(k),
                opened = follows.Count(f => (f.Kind ?? "UNKNOWN") == k),
                converted = follows.Count(f => (f.Kind ?? "UNKNOWN") == k && f.Next is not null),
            })
            .OrderByDescending(k => k.planned + k.opened).ToList();

        var first = data.Day(p.From);
        var last = data.Day(p.To.AddTicks(-1));
        var trend = new List<object>();
        for (var d = first; d <= last; d = d.AddDays(1))
        {
            var day = d;
            trend.Add(new
            {
                date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                opened = follows.Count(f => data.Day(f.Open.OccurredAt) == day),
                converted = follows.Count(f => data.Day(f.Open.OccurredAt) == day && f.Next is not null),
            });
        }

        var byHour = Enumerable.Range(0, 24)
            .Select(h => opened.Count(o => o.OccurredAt.ToOffset(offset).Hour == h)).ToList();

        var openNoLearning = follows.Where(f => f.Next is null).GroupBy(f => f.Open.UserId)
            .OrderByDescending(g => g.Count())
            .Select(g => data.LearnerById(g.Key) is { } u
                ? UserRows.For(data, u, privacy, $"فتح التذكير {g.Count()} مرات دون أن يبدأ تمرينًا")
                : null)
            .OfType<IntelUserRow>().Take(data.Options.MaxListedUsers).ToList();

        var notes = new List<string>
        {
            "التذكيرات يطلقها الهاتف نفسه دون اتصال (ADR-076): الخادم يعرف ما خطط له وما فُتح، ولا يعرف ما وصل أو ما شوهد وتُجوهل — لذلك لا يوجد معدل وصول.",
            $"التحويل = بدء تمرين خلال {data.Options.NotificationConversionMinutes} دقيقة من فتح التذكير. هذا ارتباط زمني، لا دليل على أن التذكير هو السبب.",
            "نوع التذكير عند الفتح مستنتج من آخر موعد مخطط قبله، لأن الهاتف لا يرسل النوع بعد.",
        };
        if (plansByUser.Count == 0)
            notes.Insert(0, "لا توجد خطط تذكير مسجلة بعد — تسجيلها بدأ مع ADR-132، والفترات الأقدم لا يمكن استعادتها.");
        if (opened.Count == 0 && !scope.Events.Any(e => e.Name == AnalyticsEventNames.NotificationOpened))
            notes.Add("لا أحداث فتح تذكير بعد — تأتي من إصدارات التطبيق التي تحمل التتبع.");

        return new
        {
            period,
            kpis = new[]
            {
                new Kpi("planned", "تذكيرات مخططة", planned.Planned, prevPlanned.Planned, "int", "مواعيد سلّمها الخادم للهاتف وحلّ وقتها — ليست «مُرسلة»"),
                new Kpi("targeted_users", "مستخدمون مستهدفون", planned.Users, prevPlanned.Users, "int", "لهم موعد تذكير واحد على الأقل في الفترة"),
                new Kpi("opened", "فتح التذكير", opened.Count, prevOpened.Count, "int", "notification_opened"),
                new Kpi("open_users", "مستخدمون فتحوا", opened.Select(o => o.UserId).Distinct().Count(), prevOpened.Select(o => o.UserId).Distinct().Count(), "int"),
                new Kpi("conversion", "تذكير → تمرين", IntelMetrics.Ratio(converted, opened.Count), IntelMetrics.Ratio(prevFollows.Count(f => f is not null), prevOpened.Count), "pct", $"{converted} من {opened.Count}"),
                new Kpi("completion", "تذكير → تمرين مكتمل", IntelMetrics.Ratio(completed, opened.Count), IntelMetrics.Ratio(prevFollows.Count(f => f is { Completed: true }), prevOpened.Count), "pct", $"{completed} من {opened.Count}"),
            },
            journey = new[]
            {
                new { key = "planned", label = "مخطط", count = planned.Planned },
                new { key = "opened", label = "فُتح", count = opened.Count },
                new { key = "started", label = "بدأ تمرينًا", count = converted },
                new { key = "completed", label = "أكمل التمرين", count = completed },
            },
            byKind = kinds,
            byHour,
            trend,
            users = openNoLearning,
            notes,
        };
    }

    private sealed record SkillSessionLink(bool Completed);

    private static IEnumerable<Slot> Parse(AnalyticsEvent plan, int morning, int evening, TimeSpan offset)
    {
        string? slots;
        try
        {
            using var doc = JsonDocument.Parse(plan.PropsJson ?? "{}");
            slots = doc.RootElement.TryGetProperty("slots", out var v) ? v.GetString() : null;
        }
        catch (JsonException)
        {
            yield break;
        }
        if (string.IsNullOrEmpty(slots)) yield break;

        foreach (var part in slots.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            // yyyy-MM-ddM:KIND
            var colon = part.IndexOf(':');
            if (colon != 11) continue;
            if (!DateOnly.TryParseExact(part[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;
            var hour = part[10] == 'M' ? morning : evening;
            yield return new Slot(new DateTimeOffset(date.Year, date.Month, date.Day, hour, 0, 0, offset), part[(colon + 1)..]);
        }
    }
}
