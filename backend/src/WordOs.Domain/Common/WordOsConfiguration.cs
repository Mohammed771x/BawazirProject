using WordOs.Domain.Common;

namespace WordOs.Domain.Common;

/// <summary>
/// Every tunable of the WordOS algorithm, in one place.
/// </summary>
/// <remarks>
/// Rule R3: nothing tunable is hard-coded. In production these values are
/// seeded into the <c>configurations</c> table and loaded at startup; the
/// defaults here are the ones documented in <c>docs/00-PROJECT-PLAN.md</c> §5
/// and exist so the domain is testable without a database.
/// </remarks>
public sealed record WordOsConfiguration
{
    /// <summary>Days between one skill passing and the next becoming available.</summary>
    public int SkillIntervalDays { get; init; } = 2;

    public int MinDailyTarget { get; init; } = 5;

    public int MaxDailyTarget { get; init; } = 15;

    public int DefaultDailyTarget { get; init; } = 10;

    public int WeeklyReviewPeriodDays { get; init; } = 7;

    /// <summary>
    /// The pipeline order. Configurable per ADR-001; the product owner
    /// confirmed <c>Speaking → Writing</c> on 2026-08-15.
    /// </summary>
    public IReadOnlyList<SkillType> SkillsOrder { get; init; } =
    [
        SkillType.Reading,
        SkillType.Listening,
        SkillType.Speaking,
        SkillType.Writing,
        SkillType.Spelling,
    ];

    /// <summary>
    /// Hours ahead of UTC that a reporting day starts.
    /// </summary>
    /// <remarks>
    /// "Words added today" has to mean the learner's today. Measured against a
    /// real account: six words added after midnight local time were reported as
    /// zero, because the day boundary was UTC and the learner is three hours
    /// ahead of it — so every morning until 3am belonged to yesterday.
    ///
    /// One offset for the product rather than one per learner: the app's
    /// audience is Arabic-speaking and the alternative is a timezone column
    /// nobody sets. Configurable, because the right answer is a product
    /// decision and not a constant (rule R3). Default +3, Arabia Standard Time.
    /// </remarks>
    public int ReportingUtcOffsetHours { get; init; } = 3;

    /// <summary>The start of the reporting day containing <paramref name="now"/>.</summary>
    /// <remarks>
    /// Returned in UTC. The instant is the same either way, but PostgreSQL's
    /// <c>timestamptz</c> parameters accept offset zero and nothing else, so a
    /// value carrying +03:00 is refused at the driver — which is a 500 on the
    /// dashboard rather than a wrong number.
    /// </remarks>
    public DateTimeOffset StartOfDay(DateTimeOffset now)
    {
        var offset = TimeSpan.FromHours(ReportingUtcOffsetHours);
        var local = now.ToOffset(offset);

        return new DateTimeOffset(local.Date, offset).ToUniversalTime();
    }

    /// <summary>Local hour the morning reminder fires at (ADR-076).</summary>
    /// <remarks>
    /// Two a day, morning and evening, because the thing being fought is
    /// forgetting the app exists — and a single daily reminder that lands while
    /// the learner is at school is a reminder that never happened.
    ///
    /// Wall-clock hours rather than instants: the device schedules these, and
    /// what a learner means by "morning" is the time on their own phone. The
    /// *counts* in them are still computed here, against
    /// <see cref="ReportingUtcOffsetHours"/>, for the same reason that offset
    /// exists at all.
    /// </remarks>
    public int MorningReminderHour { get; init; } = 8;

    /// <summary>Local hour the evening reminder fires at (ADR-076).</summary>
    public int EveningReminderHour { get; init; } = 20;

    /// <summary>
    /// How many days of reminders are handed to the device at a time.
    /// </summary>
    /// <remarks>
    /// Each day gets its own reminder with its own count, rather than one
    /// repeating notification saying the same number for ever: a word becomes
    /// due on a known date, so "you have 4 words ready" can be true on Tuesday
    /// and true again with a different number on Thursday — and the only way a
    /// notification the device fires offline can say either is for the server
    /// to have worked out both in advance.
    ///
    /// Bounded by what a phone will hold: iOS keeps 64 pending notifications and
    /// silently drops the rest, and this is two per day. A learner who does not
    /// open the app for longer than this stops being reminded, which is the
    /// honest outcome — by then every count would be a guess.
    /// </remarks>
    public int ReminderHorizonDays { get; init; } = 7;

    /// <summary>Sessions of evidence needed before a level may move at all.</summary>
    public int MinEvaluationSessions { get; init; } = 14;

    /// <summary><c>MVP Core.txt</c> §23 — a strong indicator of mastery.</summary>
    public double PromoteThreshold { get; init; } = 0.85;

    /// <summary><c>MVP Core.txt</c> §22 — below this the content is too hard.</summary>
    public double DemoteThreshold { get; init; } = 0.70;

    /// <summary>
    /// Ladder steps a word must sit below the proven level before it is an
    /// archive candidate. Four steps is two full CEFR bands (ADR-013).
    /// </summary>
    public int ArchiveLevelGapSteps { get; init; } = 4;

    /// <summary>
    /// Exposure floor for archiving. Exposure is a priority signal, never a
    /// limit or a delete trigger (rule R8) — it appears here only so that a
    /// word nobody has actually met in content is not retired.
    /// </summary>
    public int ArchiveMinExposure { get; init; } = 3;

    /// <summary>How many times one item may be asked within a single session.</summary>
    public int MaxAttemptsPerItem { get; init; } = 3;

    /// <summary>Comprehension questions per Reading/Listening session.</summary>
    public int ComprehensionQuestionCount { get; init; } = 5;

    /// <summary>
    /// How many Active words are offered to the generator per session.
    /// </summary>
    /// <remarks>
    /// The least-exposed words go first (rule R8: exposure prioritises, it never
    /// limits). Kept small on purpose — a passage stuffed with old vocabulary
    /// stops being a passage, and the target words are what the session is
    /// actually for.
    /// </remarks>
    public int ActiveReuseWordsPerSession { get; init; } = 3;

    /// <summary>
    /// How long an unfinished practice session stays resumable.
    /// </summary>
    /// <remarks>
    /// Practice exists for the day the pipeline is empty (Part 2 §5), and it
    /// measures nothing — so an abandoned one is not work anybody needs back.
    /// Left open for ever it becomes an obstacle instead: a session is resumed
    /// rather than replaced, so yesterday's half-finished practice is what the
    /// learner receives when they come back and ask for today's real words.
    ///
    /// A day is deliberately generous. Someone who steps away mid-practice and
    /// returns after lunch gets their place back; nobody is handed a session
    /// from last week.
    ///
    /// Real sessions have no equivalent expiry, and should not: they hold
    /// answers a learner actually gave.
    /// </remarks>
    public int PracticeSessionExpiryHours { get; init; } = 24;

    /// <summary>
    /// How long a session claimed but not yet filled with content is left alone.
    /// </summary>
    /// <remarks>
    /// A start claims its row before generating (ADR-063), so for a few seconds
    /// a real, healthy session has no passage, no conversation and no items —
    /// indistinguishable, by inspection, from one whose generation died.
    ///
    /// Without this margin the cleanup for the dead case eats the live one: a
    /// second request arriving mid-generation deletes the first request's row,
    /// claims the skill for itself, and the first then fails to save content
    /// into a row that no longer exists. Found exactly that way — one start in
    /// six answering 500 under load, in the test written to prove the claim
    /// works.
    ///
    /// Comfortably longer than the AI budget
    /// (<c>AiServiceOptions.TimeoutSeconds</c>, 25s), because the generation
    /// cannot outlive that. Cleanup is a recovery path; nothing is worse for
    /// waiting a minute.
    /// </remarks>
    public int SessionBuildGraceSeconds { get; init; } = 120;

    public SkillType? NextSkillAfter(SkillType skill)
    {
        var index = SkillsOrder.ToList().IndexOf(skill);
        if (index < 0 || index >= SkillsOrder.Count - 1) return null;
        return SkillsOrder[index + 1];
    }

    public SkillType FirstSkill => SkillsOrder[0];

    public int ClampDailyTarget(int target) =>
        Math.Clamp(target, MinDailyTarget, MaxDailyTarget);
}
