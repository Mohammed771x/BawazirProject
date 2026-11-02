using WordOs.Domain.Analytics;

namespace WordOs.Api.Admin;

/// <param name="Source"><c>client</c>, <c>server</c>, or <c>table</c> for a fact a durable table already holds.</param>
public sealed record EventDef(string Name, string Source, string Where, string Meaning);

/// <param name="Status"><c>recorded</c>, <c>derived</c> (computed from other rows), <c>partial</c>, or <c>missing</c>.</param>
public sealed record BriefEvent(string Requested, string Status, string Where, string? Note = null);

/// <summary>
/// The event half of the data dictionary (brief §14–§15, ADR-132).
/// </summary>
/// <remarks>
/// <see cref="BriefCoverage"/> answers, event by event, "does WordOS record
/// what the brief asked for, and where?" — including the ones it does not.
/// A missing event is listed as missing; a figure that depends on it says so
/// on its page instead of drawing a zero.
/// </remarks>
public static class EventDictionary
{
    public static readonly IReadOnlyList<EventDef> Events =
    [
        // ── Durable tables (read in place, never copied — ADR-125 §1) ──
        new("word_events", "table", "word_events.Type", "Added، SkillStarted، SkillPassed، SkillFailed، BecameMature، EnteredActive، ExposureIncremented، Archived، Deleted، MeaningChanged — رحلة كل كلمة."),
        new("skill_sessions", "table", "skill_sessions + session_items", "كل تمرين: المهارة، مستوى المحتوى المستخدم، البداية والنهاية، المحتوى، نسخة الـprompt، ولكل سؤال المحاولات ونتيجة المحاولة الأولى."),
        new("weekly_reviews", "table", "weekly_reviews + weekly_review_items", "المراجعة الأسبوعية: الكلمات، النجاح من أول محاولة، المحاولات."),
        new("level_changes", "table", "level_changes", "كل تغيير مستوى لكل مهارة: من، إلى، يدويًا أو من النظام."),
        new("activity_events", "table", "activity_events.Type", "تسجيل الدخول والأنشطة العامة لكل مستخدم."),
        new("feedback_messages", "table", "feedback_messages + admin_notes", "ملاحظات المستخدمين وتصنيفها والملاحظات الإدارية الداخلية."),
        // ── analytics_events ──
        .. AnalyticsEventNames.ClientNames.Order().Select(n => new EventDef(n, "client", "analytics_events", ClientMeaning(n))),
        .. AnalyticsEventNames.ServerNames.Order().Select(n => new EventDef(n, "server", "analytics_events", ServerMeaning(n))),
    ];

    private static string ClientMeaning(string n) => n switch
    {
        AnalyticsEventNames.AppOpened => "التطبيق انتقل إلى الواجهة — بداية «جلسة تطبيق» (AppSessionId).",
        AnalyticsEventNames.AppBackgrounded => "التطبيق انتقل إلى الخلفية — نهاية جلسة التطبيق ومدتها.",
        AnalyticsEventNames.ScreenViewed => "شاشة ظهرت.",
        AnalyticsEventNames.ScreenLeft => "شاشة أُغلقت، مع مدة بقائها — أساس وقت التعلم الفعلي داخل التمرين.",
        AnalyticsEventNames.TranslationOpened => "فتح ترجمة كلمة في النص.",
        AnalyticsEventNames.AudioPlayed => "تشغيل مقطع صوتي.",
        AnalyticsEventNames.AudioReplayed => "إعادة تشغيل مقطع.",
        AnalyticsEventNames.AudioPaused => "إيقاف مؤقت.",
        AnalyticsEventNames.AudioCompleted => "اكتمل الاستماع إلى المقطع.",
        AnalyticsEventNames.HintUsed => "طلب تلميح (Spelling).",
        AnalyticsEventNames.FeedbackViewed => "عرض تقييم الـAI ومدة قراءته.",
        AnalyticsEventNames.ExerciseExited => "الخروج من تمرين قبل نهايته.",
        AnalyticsEventNames.NotificationOpened => "فتح التطبيق من تذكير.",
        AnalyticsEventNames.ApiError => "طلب فشل كما رآه التطبيق.",
        AnalyticsEventNames.EmptyStateShown => "شاشة فارغة عُرضت.",
        _ => n,
    };

    private static string ServerMeaning(string n) => n switch
    {
        AnalyticsEventNames.AnswerSubmitted => "كل محاولة إجابة: السؤال، رقم المحاولة، النتيجة، وزمن الإجابة من ظهور السؤال.",
        AnalyticsEventNames.WritingEvaluated => "تقييم جملة Writing وأسباب الفشل (استخدام الكلمة، المعنى، الاستعمال).",
        AnalyticsEventNames.SpeakingTurn => "رد المتعلم في المحادثة وزمنه وعدد الكلمات المستهدفة المستخدمة.",
        AnalyticsEventNames.SpeakingWordEvaluated => "الحكم على استخدام كل كلمة في Speaking.",
        AnalyticsEventNames.ReviewAnswered => "إجابة في المراجعة الأسبوعية.",
        AnalyticsEventNames.AiCall => "كل طلب إلى خدمة الـAI: المدة، النتيجة، البديل.",
        AnalyticsEventNames.SessionAbandoned => "تمرين انسحب منه المتعلم — يُكتب قبل حذف التمرين.",
        AnalyticsEventNames.RemindersPlanned => "مواعيد التذكير التي سلّمها الخادم للهاتف ونوع كل منها.",
        _ => n,
    };

    public static readonly IReadOnlyList<BriefEvent> BriefCoverage =
    [
        new("app_opened", "recorded", "analytics_events"),
        new("session_started", "derived", "app_opened / skill_sessions.StartedAt", "«جلسة التطبيق» تبدأ بـapp_opened، وجلسة التعلم هي skill_session."),
        new("session_ended", "derived", "app_backgrounded / skill_sessions.CompletedAt"),
        new("onboarding_started", "derived", "users.CreatedAt"),
        new("onboarding_completed", "derived", "placement_sessions + users", "مرحلة في Activation Funnel."),
        new("word_added", "recorded", "word_events: Added"),
        new("translation_opened", "recorded", "analytics_events"),
        new("audio_played", "recorded", "analytics_events"),
        new("audio_replayed", "recorded", "analytics_events"),
        new("level_changed", "recorded", "level_changes"),
        new("skill_started", "recorded", "word_events: SkillStarted / skill_sessions"),
        new("question_presented", "partial", "session_items + elapsedMs", "لا حدث مستقل لظهور السؤال؛ زمن الإجابة من ظهوره يُرسل مع الإجابة (elapsedMs)."),
        new("question_answered", "recorded", "analytics_events: answer_submitted"),
        new("answer_submitted", "recorded", "analytics_events"),
        new("skill_passed", "recorded", "word_events: SkillPassed"),
        new("skill_failed", "recorded", "word_events: SkillFailed"),
        new("hint_used", "recorded", "analytics_events"),
        new("retry_started", "derived", "answer_submitted.Attempt > 1"),
        new("feedback_viewed", "recorded", "analytics_events"),
        new("feedback_submitted", "recorded", "feedback_messages"),
        new("word_mastered", "recorded", "word_events: BecameMature"),
        new("word_entered_active", "recorded", "word_events: EnteredActive"),
        new("weekly_review_started", "recorded", "weekly_reviews.StartedAt"),
        new("weekly_review_completed", "recorded", "weekly_reviews.CompletedAt"),
        new("notification_sent", "partial", "analytics_events: reminders_planned", "الهاتف يطلق التذكير بنفسه (ADR-076)؛ المسجل هو ما خُطط له، لا ما أُرسل أو وصل."),
        new("notification_opened", "recorded", "analytics_events"),
        new("notification_converted", "derived", "notification_opened + skill_sessions", "تمرين خلال NotificationConversionMinutes."),
        new("user_feedback_created", "recorded", "feedback_messages"),
        new("content_version", "partial", "skill_sessions.PromptVersion + ContentText", "النص محفوظ مع تمرينه ونسخة الـprompt التي كتبته؛ لا جدول محتوى مستقل لأن كل نص يُولَّد لتمرينه."),
        new("word_used_after_active", "recorded", "word_events: ExposureIncremented"),
    ];
}
