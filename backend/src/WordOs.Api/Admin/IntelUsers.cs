using WordOs.Domain.Analytics;
using WordOs.Domain.Common;
using WordOs.Domain.Users;
using WordOs.Domain.Words;

namespace WordOs.Api.Admin;

/// <summary>One learner, as every list in the admin area shows them.</summary>
public sealed record IntelUserRow(
    Guid Id,
    string Name,
    string Email,
    string? Phone,
    DateTimeOffset JoinedAt,
    DateTimeOffset? LastActiveAt,
    int ActiveDays,
    int Sessions,
    int Words,
    int Mastered,
    string? Level,
    string Status,
    int Streak,
    IReadOnlyList<string> Flags,
    string? Evidence = null);

/// <summary>
/// Contact details, shown or masked by role (admin brief §33, ADR-125).
/// </summary>
public sealed record Privacy(bool CanSeeContact)
{
    public string Email(string email)
    {
        if (CanSeeContact) return email;
        var at = email.IndexOf('@');
        return at <= 1 ? "•••" : $"{email[0]}•••{email[at..]}";
    }

    public string? Phone(User u) =>
        CanSeeContact && !string.IsNullOrEmpty(u.PhoneNumber)
            ? $"+{u.PhoneCountryCode}{u.PhoneNumber}"
            : null;
}

public static class UserRows
{
    public static IntelUserRow For(IntelDataset data, User u, Privacy privacy, string? evidence = null)
    {
        var words = data.WordsOf(u.Id).Where(w => w.State != WordState.Deleted).ToList();
        var last = data.LastActiveAt(u.Id);

        var status = data.Now - u.CreatedAt < TimeSpan.FromDays(data.Options.InactiveDays)
            ? "new"
            : last is not null && data.Now - last.Value < TimeSpan.FromDays(data.Options.InactiveDays)
                ? "active"
                : "inactive";

        return new IntelUserRow(
            u.Id,
            u.DisplayName,
            privacy.Email(u.Email),
            privacy.Phone(u),
            u.CreatedAt,
            last,
            data.ActiveDaysOf(u.Id).Count,
            data.SessionsOf(u.Id).Count(s => !s.IsPractice),
            words.Count,
            words.Count(w => w.State is WordState.Active or WordState.Archived),
            IntelDataset.LevelOf(u)?.ToWire(),
            status,
            Streak(data, u.Id),
            Attention.Reasons(data, u).Select(r => r.Key).ToList(),
            evidence);
    }

    /// <summary>Consecutive active days ending today, or yesterday if today has not started yet.</summary>
    public static int Streak(IntelDataset data, Guid userId)
    {
        var days = data.ActiveDaysOf(userId);
        var day = data.Day(data.Now);
        if (!days.Contains(day)) day = day.AddDays(-1);

        var streak = 0;
        while (days.Contains(day))
        {
            streak++;
            day = day.AddDays(-1);
        }
        return streak;
    }
}

/// <summary>
/// The activation funnel (admin brief §7).
/// </summary>
/// <remarks>
/// In WordOS a word becomes Mature and Active in the same instant — all five
/// skills passed. Two stages that always coincide would be one stage drawn
/// twice, so "first active word" here means a word that is Active <i>and has
/// since been reused by the AI</i>: the point where the vocabulary started
/// doing what Active Vocabulary is for.
/// </remarks>
public static class Funnel
{
    public static readonly IReadOnlyList<(string Key, string Label, string Hint)> Stages =
    [
        ("signup", "التسجيل", "أنشأ حسابًا"),
        ("onboarding", "إكمال الإعداد", "أكمل الاهتمامات واختبار المستوى"),
        ("first_word", "أول كلمة", "أضاف كلمة واحدة على الأقل"),
        ("first_skill", "أول مهارة", "بدأ أول Session في أي مهارة"),
        ("first_completed", "أول مهارة مكتملة", "أنهى Session حتى النهاية"),
        ("second_session", "Session ثانية", "عاد وتعلم في يوم آخر"),
        ("first_mastered", "أول كلمة متقنة", "اجتازت كلمة المهارات الخمس"),
        ("first_active", "أول كلمة Active مستخدمة", "كلمة Active أعاد الـAI استخدامها"),
    ];

    public static string LabelOf(string key) =>
        Stages.FirstOrDefault(s => s.Key == key).Label ?? key;

    public static bool Reached(IntelDataset data, User u, string stage)
    {
        var words = data.WordsOf(u.Id);
        var sessions = data.SessionsOf(u.Id).Where(s => !s.IsPractice).ToList();
        return stage switch
        {
            "signup" => true,
            "onboarding" => u.OnboardingStage == OnboardingStage.Complete,
            "first_word" => words.Count > 0,
            "first_skill" => sessions.Count > 0
                             || data.EventsOf(u.Id).Any(e => e.Name == AnalyticsEventNames.SessionAbandoned),
            "first_completed" => sessions.Any(s => s.IsComplete),
            "second_session" => sessions.Select(s => data.Day(s.StartedAt)).Distinct().Count() >= 2,
            "first_mastered" => words.Any(w => w.MaturedAt is not null),
            "first_active" => words.Any(w => w.MaturedAt is not null
                                             && w.Events.Any(e => e.Type == WordEventType.ExposureIncremented)),
            _ => false,
        };
    }

    /// <summary>The last stage this learner reached, in order.</summary>
    public static string StageOf(IntelDataset data, User u)
    {
        var last = "signup";
        foreach (var (key, _, _) in Stages)
        {
            if (!Reached(data, u, key)) break;
            last = key;
        }
        return last;
    }
}

/// <summary>
/// "Users needing attention" (admin brief §24) — rules, not a score.
/// </summary>
/// <remarks>
/// Each reason is a plain rule an admin can read and disagree with, and each
/// comes with the evidence that triggered it. A blended risk score would be
/// one number nobody could explain to the learner it was about.
/// </remarks>
public static class Attention
{
    public static readonly IReadOnlyList<(string Key, string Label, string Description)> Catalogue =
    [
        ("recently_inactive", "توقف مؤخرًا", "كان نشطًا ثم انقطع"),
        ("high_abandonment", "انسحاب مرتفع", "يترك أكثر من نصف التمارين قبل نهايتها"),
        ("repeated_failures", "فشل متكرر", "فشل في نفس الكلمة أو المهارة مرات متتالية"),
        ("long_sessions_low_progress", "وقت طويل وتقدم قليل", "Sessions طويلة دون اجتياز مهارات"),
        ("very_short_sessions", "Sessions قصيرة جدًا", "يفتح التمرين ويغادره خلال ثوانٍ"),
        ("hoarder", "يضيف كثيرًا ويتقن قليلًا", "10 كلمات أو أكثر وأقل من 10% منها Active"),
        ("speaking_failure", "فشل متكرر في Speaking", "فشل في Speaking دون أي نجاح مؤخرًا"),
        ("notification_no_learning", "يفتح التنبيه ولا يتعلم", "فتح التذكير ولم يبدأ أي تمرين بعده"),
        ("high_overdue", "متأخرات كثيرة", "كلمات مستحقة لم تُراجع منذ أيام"),
        ("ai_content_issue", "مشكلة محتملة في الـAI أو المحتوى", "Sessions استخدمت محتوى احتياطيًا أو فشل التقييم"),
    ];

    public static string LabelOf(string key) =>
        Catalogue.FirstOrDefault(c => c.Key == key).Label ?? key;

    public sealed record Reason(string Key, string Evidence);

    public static IReadOnlyList<Reason> Reasons(IntelDataset data, User u)
    {
        var reasons = new List<Reason>();
        var now = data.Now;
        var o = data.Options;
        var sessions = data.SessionsOf(u.Id).Where(s => !s.IsPractice).ToList();
        var recent = sessions.Where(s => now - s.StartedAt <= TimeSpan.FromDays(30)).ToList();
        var events = data.EventsOf(u.Id);
        var words = data.WordsOf(u.Id);
        var days = data.ActiveDaysOf(u.Id);
        var last = data.LastActiveAt(u.Id);

        if (last is not null && days.Count >= 3)
        {
            var idle = now - last.Value;
            if (idle >= TimeSpan.FromDays(o.InactiveDays) && idle <= TimeSpan.FromDays(14))
                reasons.Add(new("recently_inactive", $"آخر نشاط قبل {(int)idle.TotalDays} أيام بعد {days.Count} أيام نشاط"));
        }

        var abandoned = events.Count(e => e.Name == AnalyticsEventNames.SessionAbandoned
                                          && now - e.OccurredAt <= TimeSpan.FromDays(30))
                        + recent.Count(data.IsStale);
        var started = recent.Count + events.Count(e => e.Name == AnalyticsEventNames.SessionAbandoned
                                                      && now - e.OccurredAt <= TimeSpan.FromDays(30));
        if (started >= 3 && abandoned * 2 >= started)
            reasons.Add(new("high_abandonment", $"ترك {abandoned} من {started} تمرينًا"));

        var failures = words.SelectMany(w => w.Events)
            .Where(e => e.Type == WordEventType.SkillFailed && now - e.CreatedAt <= TimeSpan.FromDays(14))
            .ToList();
        var sameWord = failures.GroupBy(e => e.WordId).Max(g => (int?)g.Count()) ?? 0;
        if (sameWord >= 2 || failures.Count >= 5)
            reasons.Add(new("repeated_failures", $"{failures.Count} حالات فشل خلال 14 يومًا"));

        var durations = recent.Select(data.DurationMs).OfType<double>().Order().ToList();
        var passes = words.SelectMany(w => w.Events)
            .Count(e => e.Type == WordEventType.SkillPassed && now - e.CreatedAt <= TimeSpan.FromDays(14));
        if (durations.Count >= 3)
        {
            var median = durations[durations.Count / 2];
            if (median > 10 * 60_000 && passes == 0)
                reasons.Add(new("long_sessions_low_progress", $"وسيط الجلسة {median / 60_000:0} دقيقة ولا اجتياز خلال 14 يومًا"));
            if (median < 30_000)
                reasons.Add(new("very_short_sessions", $"وسيط الجلسة {median / 1000:0} ثانية"));
        }

        var kept = words.Where(w => w.State != WordState.Deleted).ToList();
        var mastered = kept.Count(w => w.State is WordState.Active or WordState.Archived);
        if (kept.Count >= 10 && mastered <= kept.Count / 10)
            reasons.Add(new("hoarder", $"{kept.Count} كلمة، {mastered} منها Active"));

        var speaking = words.SelectMany(w => w.Events)
            .Where(e => e.Skill == SkillType.Speaking && now - e.CreatedAt <= TimeSpan.FromDays(30))
            .ToList();
        var speakFail = speaking.Count(e => e.Type == WordEventType.SkillFailed);
        if (speakFail >= 2 && speaking.All(e => e.Type != WordEventType.SkillPassed))
            reasons.Add(new("speaking_failure", $"{speakFail} حالات فشل في Speaking دون نجاح"));

        var opened = events.Where(e => e.Name == AnalyticsEventNames.NotificationOpened
                                       && now - e.OccurredAt <= TimeSpan.FromDays(30)).ToList();
        var wasted = opened.Count(n => !sessions.Any(s => s.StartedAt >= n.OccurredAt
                                                          && s.StartedAt - n.OccurredAt <= TimeSpan.FromMinutes(30)));
        if (wasted >= 2)
            reasons.Add(new("notification_no_learning", $"فتح التذكير {wasted} مرات دون أن يبدأ تمرينًا"));

        var overdue = Overdue(data, words);
        if (overdue >= 5)
            reasons.Add(new("high_overdue", $"{overdue} كلمات متأخرة أكثر من {o.OverdueDays} أيام"));

        var fallback = recent.Count(s => s.UsedAiFallback)
                       + events.Count(e => e.Name == AnalyticsEventNames.AiCall && e.Result == "error"
                                           && now - e.OccurredAt <= TimeSpan.FromDays(30));
        if (fallback >= 2)
            reasons.Add(new("ai_content_issue", $"{fallback} حالات محتوى احتياطي أو خطأ AI خلال 30 يومًا"));

        return reasons;
    }

    /// <summary>Words due at their current skill for longer than the overdue threshold.</summary>
    public static int Overdue(IntelDataset data, IEnumerable<Word> words) =>
        words.Count(w => DueSince(data, w) is { } since
                         && data.Now - since >= TimeSpan.FromDays(data.Options.OverdueDays));

    /// <summary>When this word's current skill became due, or null if it is not due.</summary>
    public static DateTimeOffset? DueSince(IntelDataset data, Word w)
    {
        if (w.State != WordState.Learning || w.CurrentSkill is null) return null;
        var state = w.Skills.FirstOrDefault(s => s.Skill == w.CurrentSkill);
        if (state is null) return null;

        var effective = state.EffectiveStatus(data.Now);
        if (effective is not (SkillStatus.Available or SkillStatus.Failed)) return null;
        if (state.AvailableAt is { } at && at > data.Now) return null;

        return state.AvailableAt ?? state.FailedAt ?? w.AddedAt;
    }
}
