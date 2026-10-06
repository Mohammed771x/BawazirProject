using System.Text.RegularExpressions;
using WordOs.Domain.Analytics;
using WordOs.Domain.Common;
using WordOs.Domain.Sessions;
using WordOs.Domain.Users;
using WordOs.Domain.Words;

namespace WordOs.Api.Admin;

/// <summary>One thing on a learner's timeline (admin brief §22).</summary>
/// <param name="Tone"><c>good</c>, <c>bad</c> or <c>neutral</c> — how to colour it, nothing more.</param>
/// <param name="Detail">Everything known about it, label → value, for the expanded row.</param>
public sealed record TimelineEvent(
    string Id,
    DateTimeOffset At,
    string Kind,
    string Title,
    string Tone,
    string? Skill,
    string? Word,
    Guid? SessionId,
    IReadOnlyDictionary<string, string> Detail);

/// <summary>
/// A learner's whole history, merged from every table that holds a piece of it.
/// </summary>
/// <remarks>
/// Nothing is stored for the timeline's sake (§32): sign-ups come from the
/// user row, word journeys from word events, lessons from sessions, attempts
/// and on-screen behaviour from the analytics log. The timeline is a view.
/// </remarks>
public static class IntelTimeline
{
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    public static IReadOnlyList<TimelineEvent> For(IntelDataset data, User user, bool verbose, int limit = 600)
    {
        var list = new List<TimelineEvent>();
        var words = data.WordsOf(user.Id);
        var byId = words.ToDictionary(w => w.Id);
        string? Text(Guid? id) => id is { } g && byId.TryGetValue(g, out var w) ? w.Text : null;

        list.Add(new("signup", user.CreatedAt, "signup", "التسجيل", "neutral", null, null, null,
            new Dictionary<string, string> { ["الاسم"] = user.DisplayName }));

        foreach (var a in data.Activity.Where(a => a.UserId == user.Id))
        {
            var (kind, title, tone) = a.Type switch
            {
                ActivityType.PlacementCompleted => ("onboarding", "أكمل اختبار المستوى", "good"),
                ActivityType.SignedIn => ("signin", "تسجيل الدخول", "neutral"),
                ActivityType.ReviewCompleted => ("review_completed", "أكمل Weekly Review", "good"),
                ActivityType.PasswordReset => ("password_reset", "غيّر كلمة المرور", "neutral"),
                ActivityType.ScheduleAdvanced => ("schedule_advanced", "قدّم المالك الجدول (اختبار)", "neutral"),
                ActivityType.FeedbackSent => ("feedback", "أرسل ملاحظة", "neutral"),
                _ => (null, null, null),
            };
            if (kind is null || (kind == "signin" && !verbose)) continue;
            list.Add(new($"a{a.Id}", a.CreatedAt, kind, title!, tone!, a.Skill?.ToWire(), null, a.EntityId, Empty));
        }

        foreach (var w in words)
        {
            foreach (var e in w.Events)
            {
                var (kind, title, tone) = e.Type switch
                {
                    WordEventType.Added => ("word_added", $"أضاف كلمة «{w.Text}»", "neutral"),
                    WordEventType.SkillPassed => ("skill_passed", $"اجتاز {e.Skill} — {w.Text}", "good"),
                    WordEventType.SkillFailed => ("skill_failed", $"فشل في {e.Skill} — {w.Text}", "bad"),
                    WordEventType.BecameMature => ("word_mastered", $"أتقن «{w.Text}»", "good"),
                    WordEventType.EnteredActive => ("word_active", $"«{w.Text}» أصبحت Active", "good"),
                    WordEventType.Archived => ("word_archived", $"أُرشفت «{w.Text}»", "neutral"),
                    WordEventType.Deleted => ("word_deleted", $"حذف «{w.Text}»", "bad"),
                    WordEventType.MeaningChanged => ("meaning_changed", $"غيّر معنى «{w.Text}»", "neutral"),
                    WordEventType.ExposureIncremented when verbose => ("exposure", $"أعاد الـAI استخدام «{w.Text}»", "neutral"),
                    _ => (null, null, null),
                };
                if (kind is null) continue;
                list.Add(new($"w{e.Id}", e.CreatedAt, kind, title!, tone!, e.Skill?.ToWire(), w.Text, null,
                    new Dictionary<string, string> { ["المعنى"] = w.Meaning, ["الحالة الآن"] = w.State.ToWire() }));
            }
        }

        foreach (var s in data.SessionsOf(user.Id))
        {
            var label = s.IsPractice ? $"تدريب {s.Skill}" : s.Skill.ToString();
            var items = s.Items.Where(i => i.Attempts > 0).ToList();
            list.Add(new($"s{s.Id}", s.StartedAt, "session_started", $"بدأ {label}", "neutral", s.Skill.ToWire(), null, s.Id,
                new Dictionary<string, string>
                {
                    ["المستوى"] = s.Skill == SkillType.Spelling ? "—" : s.LevelUsed.ToWire(),
                    ["الكلمات"] = s.WordIds.Count.ToString(),
                    ["النص"] = s.ContentTitle ?? "—",
                    ["محتوى احتياطي"] = s.UsedAiFallback ? "نعم" : "لا",
                }));

            if (s.CompletedAt is { } done)
            {
                list.Add(new($"c{s.Id}", done, "session_completed", $"أنهى {label}", "good", s.Skill.ToWire(), null, s.Id,
                    new Dictionary<string, string>
                    {
                        ["المدة"] = Duration(done - s.StartedAt),
                        ["الأسئلة"] = items.Count.ToString(),
                        ["صحيح من أول محاولة"] = items.Count(i => i.FirstAttemptCorrect == true).ToString(),
                        ["المحاولات"] = items.Sum(i => i.Attempts).ToString(),
                    }));
            }
            else if (data.IsStale(s))
            {
                list.Add(new($"x{s.Id}", s.StartedAt.AddHours(data.Options.AbandonAfterHours), "session_abandoned",
                    $"ترك {label} مفتوحًا دون إكمال", "bad", s.Skill.ToWire(), null, s.Id,
                    new Dictionary<string, string> { ["أُجيب عن"] = $"{items.Count} من {s.Items.Count}" }));
            }
        }

        foreach (var r in data.ReviewsOf(user.Id).Where(r => !r.IsPractice))
        {
            list.Add(new($"r{r.Id}", r.StartedAt, "review_started", "بدأ Weekly Review", "neutral", null, null, r.Id,
                new Dictionary<string, string> { ["الكلمات"] = r.TotalWords.ToString() }));
        }

        foreach (var e in data.EventsOf(user.Id))
        {
            var mapped = Map(e, Text(e.WordId), verbose);
            if (mapped is not null) list.Add(mapped);
        }

        foreach (var l in data.LevelChanges.Where(l => l.UserId == user.Id))
        {
            var up = l.NewLevel > l.PreviousLevel;
            list.Add(new($"l{l.Id}", l.CreatedAt, "level_changed",
                $"{l.Skill}: {Level(l.PreviousLevel)} → {Level(l.NewLevel)}",
                up ? "good" : "neutral", l.Skill.ToWire(), null, null,
                new Dictionary<string, string>
                {
                    ["النوع"] = l.ChangeType.ToWire(),
                    ["السبب"] = l.Reason,
                }));
        }

        foreach (var f in data.Feedback.Where(f => f.UserId == user.Id))
        {
            list.Add(new($"f{f.Id}", f.CreatedAt, "feedback", "كتب ملاحظة", "neutral", null, null, null,
                new Dictionary<string, string>
                {
                    ["التصنيف"] = f.Category?.ToWire() ?? "—",
                    ["النص"] = f.Body.Length > 280 ? f.Body[..280] + "…" : f.Body,
                }));
        }

        return list.OrderByDescending(e => e.At).Take(limit).ToList();
    }

    private static TimelineEvent? Map(AnalyticsEvent e, string? word, bool verbose)
    {
        var skill = e.Skill?.ToWire();
        var id = $"e{e.Id}";
        var detail = new Dictionary<string, string>();
        if (e.DurationMs is { } ms) detail["المدة"] = Duration(TimeSpan.FromMilliseconds(ms));
        if (e.Attempt is { } attempt) detail["المحاولة"] = attempt.ToString();
        if (e.ContentLevel is { } level) detail["المستوى"] = level;
        if (e.Screen is { } screen) detail["الشاشة"] = screen;
        if (e.AppVersion is { } v) detail["الإصدار"] = v;
        foreach (var (k, value) in Props(e)) detail[k] = value;

        string Pass(string ok, string no) => e.Result == "pass" ? ok : no;
        string Tone() => e.Result switch { "pass" => "good", "fail" or "error" => "bad", _ => "neutral" };

        return e.Name switch
        {
            AnalyticsEventNames.AnswerSubmitted => new(id, e.OccurredAt, "answer",
                Pass("إجابة صحيحة", "إجابة خاطئة") + (word is null ? "" : $" — {word}"), Tone(), skill, word, e.SessionId, detail),
            AnalyticsEventNames.WritingEvaluated => new(id, e.OccurredAt, "writing",
                Pass("جملة مقبولة", "جملة لم تُقبل") + (word is null ? "" : $" — {word}"), Tone(), skill, word, e.SessionId, detail),
            AnalyticsEventNames.SpeakingWordEvaluated => new(id, e.OccurredAt, "speaking_eval",
                Pass("استخدم الكلمة بنجاح", "لم ينجح في استخدام الكلمة") + (word is null ? "" : $" — {word}"), Tone(), skill, word, e.SessionId, detail),
            AnalyticsEventNames.SpeakingTurn => new(id, e.OccurredAt, "speaking_turn", "رد في المحادثة", "neutral", skill, null, e.SessionId, detail),
            AnalyticsEventNames.ReviewAnswered => new(id, e.OccurredAt, "review_answer",
                Pass("مراجعة صحيحة", "مراجعة خاطئة") + (word is null ? "" : $" — {word}"), Tone(), null, word, e.SessionId, detail),
            AnalyticsEventNames.SessionAbandoned => new(id, e.OccurredAt, "session_abandoned", $"انسحب من {e.Skill}", "bad", skill, null, e.SessionId, detail),
            AnalyticsEventNames.TranslationOpened => new(id, e.OccurredAt, "translation",
                $"فتح الترجمة{(IntelMetrics.Prop<string>(e, "word") is { } w ? $" — {w}" : "")}", "neutral", skill, null, e.SessionId, detail),
            AnalyticsEventNames.AudioPlayed => new(id, e.OccurredAt, "audio_play", "شغّل الصوت", "neutral", skill, null, e.SessionId, detail),
            AnalyticsEventNames.AudioReplayed => new(id, e.OccurredAt, "audio_replay", "أعاد تشغيل الصوت", "neutral", skill, null, e.SessionId, detail),
            AnalyticsEventNames.AudioPaused when verbose => new(id, e.OccurredAt, "audio_pause", "أوقف الصوت مؤقتًا", "neutral", skill, null, e.SessionId, detail),
            AnalyticsEventNames.AudioCompleted when verbose => new(id, e.OccurredAt, "audio_complete", "استمع حتى النهاية", "neutral", skill, null, e.SessionId, detail),
            AnalyticsEventNames.HintUsed => new(id, e.OccurredAt, "hint", "طلب تلميحًا" + (word is null ? "" : $" — {word}"), "neutral", skill, word, e.SessionId, detail),
            AnalyticsEventNames.FeedbackViewed => new(id, e.OccurredAt, "feedback_viewed", "قرأ تقييم الـAI", "neutral", skill, word, e.SessionId, detail),
            AnalyticsEventNames.ExerciseExited => new(id, e.OccurredAt, "exercise_exit", "خرج من التمرين", "bad", skill, null, e.SessionId, detail),
            AnalyticsEventNames.NotificationOpened => new(id, e.OccurredAt, "notification", "فتح التذكير", "neutral", null, null, null, detail),
            AnalyticsEventNames.AppOpened => new(id, e.OccurredAt, "app_opened", "فتح التطبيق", "neutral", null, null, null, detail),
            AnalyticsEventNames.AppBackgrounded when verbose => new(id, e.OccurredAt, "app_closed", "أغلق التطبيق", "neutral", null, null, null, detail),
            AnalyticsEventNames.ScreenViewed when verbose => new(id, e.OccurredAt, "screen", $"فتح شاشة {e.Screen}", "neutral", skill, null, e.SessionId, detail),
            AnalyticsEventNames.ApiError => new(id, e.OccurredAt, "api_error", "خطأ في الاتصال", "bad", skill, null, e.SessionId, detail),
            AnalyticsEventNames.EmptyStateShown when verbose => new(id, e.OccurredAt, "empty_state", "ظهرت له شاشة فارغة", "neutral", skill, null, null, detail),
            AnalyticsEventNames.AiCall when e.Result == "error" => new(id, e.OccurredAt, "ai_error", "فشل طلب الـAI", "bad", skill, null, null, detail),
            _ => null,
        };
    }

    private static IEnumerable<(string, string)> Props(AnalyticsEvent e)
    {
        if (e.PropsJson is null) yield break;
        System.Text.Json.JsonDocument doc;
        try { doc = System.Text.Json.JsonDocument.Parse(e.PropsJson); }
        catch (System.Text.Json.JsonException) { yield break; }
        using (doc)
        {
            foreach (var p in doc.RootElement.EnumerateObject())
                yield return (p.Name, p.Value.ToString());
        }
    }

    private static string Level(CefrLevel? level) => level?.ToWire().Replace("_PLUS", "+") ?? "—";

    public static string Duration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0} ث"
        : t.TotalMinutes < 60 ? $"{t.TotalMinutes:0.#} د"
        : $"{t.TotalHours:0.#} س";
}

/// <summary>
/// "Repeated problems" in feedback (admin brief §17), by shared words.
/// </summary>
/// <remarks>
/// Deliberately simple and explainable: a theme is a word that appears in
/// several different messages, shown with how many. No model reads the
/// learners' text — it is shown as text and counted, never interpreted
/// (ADR-053's rule for this table).
/// </remarks>
public static partial class FeedbackThemes
{
    private static readonly HashSet<string> Stop =
    [
        "في", "من", "على", "الى", "إلى", "عن", "ما", "لا", "ان", "أن", "إن", "هذا", "هذه", "مع", "او", "أو", "كل", "لم", "لما",
        "هو", "هي", "انا", "أنا", "كان", "يكون", "عند", "شي", "شيء", "بس", "جدا", "جداً", "فيه", "فيها", "اللي", "التطبيق",
        "the", "a", "an", "is", "it", "to", "and", "of", "in", "i", "my", "app", "for", "on", "this", "that", "not", "be",
    ];

    public sealed record Theme(string Term, int Messages, IReadOnlyList<Guid> Examples);

    public static IReadOnlyList<Theme> Find(IReadOnlyList<FeedbackMessage> messages)
    {
        var index = new Dictionary<string, List<Guid>>();
        foreach (var m in messages)
        {
            var terms = Words().Matches(m.Body.ToLowerInvariant())
                .Select(x => Normalize(x.Value))
                .Where(t => t.Length >= 3 && !Stop.Contains(t))
                .Distinct();
            foreach (var t in terms)
            {
                if (!index.TryGetValue(t, out var ids)) index[t] = ids = [];
                ids.Add(m.Id);
            }
        }

        return index.Where(kv => kv.Value.Count >= 2)
            .OrderByDescending(kv => kv.Value.Count)
            .Take(12)
            .Select(kv => new Theme(kv.Key, kv.Value.Count, kv.Value.Take(5).ToList()))
            .ToList();
    }

    /// <summary>Folds the Arabic letter families a learner types interchangeably.</summary>
    /// <remarks>
    /// And the article, so «الصوت» and «صوت» are one theme.
    /// </remarks>
    private static string Normalize(string word)
    {
        var folded = word.Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا')
            .Replace('ة', 'ه').Replace('ى', 'ي');
        return folded.StartsWith("ال") && folded.Length > 4 ? folded[2..] : folded;
    }

    [GeneratedRegex(@"[\p{L}]+")]
    private static partial Regex Words();
}
