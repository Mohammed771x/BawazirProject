using WordOs.Domain.Common;

using WordOs.Domain.Lexicon;

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
    /// Days between adding a word and its first weekly challenge (ADR-089).
    /// </summary>
    /// <remarks>
    /// The gap is the feature. A word tested the day it was added is not being
    /// reviewed, it is being taught again — the learner still has it in mind,
    /// answers correctly, and the score says nothing about retention, which is
    /// the only thing rule R9 lets this measure.
    /// </remarks>
    public int WeeklyReviewMaturityDays { get; init; } = 7;

    /// <summary>
    /// The most words one sitting of the challenge may ask about (ADR-089).
    /// </summary>
    /// <remarks>
    /// Unreviewed words carry over instead of expiring, so a learner who missed
    /// a fortnight could otherwise be met with a hundred and thirty questions
    /// and simply close the app. The rest are not lost: they stay ripe, and the
    /// next day's reminder offers them again.
    ///
    /// Fifty is the product owner's number (2026-09-17), and it is configuration
    /// rather than a constant because the right ceiling is a judgement about
    /// people, not about software (rule R3).
    /// </remarks>
    public int WeeklyReviewMaxWords { get; init; } = 50;

    /// <summary>
    /// Which build of the dictionary the app looks words up in (ADR-096).
    /// </summary>
    /// <remarks>
    /// The first lexicon put <c>تُوُفِّيَ</c> at the top of <c>go</c> and cited
    /// every verb in the past, so <c>sell</c> read as <c>باع</c>. The rebuild
    /// is a second edition in the same table rather than a replacement, and
    /// this setting says which one is served.
    ///
    /// It is configuration because the answer to "is the new one better?" is a
    /// judgement made after learners have used it, and going back must not
    /// require a re-import or a deploy (rule R3). Both editions are present, so
    /// a word added under either still resolves.
    /// </remarks>
    /// <para>Moved to the second edition on 2026-09-17, after it was imported
    /// and measured: it wins on quality everywhere it has an entry, and — once
    /// its gaps are filled from the first edition — on coverage in every
    /// frequency band too (92.2 % of the commonest thousand words against
    /// 85.4 %). Set it back to <c>oewn-awn</c> to return to the first
    /// dictionary; both are in the table.</para>
    /// <para><b>The default is the first edition again (2026-09-28, ADR-103)</b>,
    /// and only development asks for the second, in
    /// <c>appsettings.Development.json</c>. Production has no second-edition
    /// rows until they are loaded, and search reads only the configured
    /// edition — so a default of <c>wiktionary</c> would ship this code to an
    /// empty search for every learner. The switch in production is the
    /// environment variable <c>WordOs__LexiconEdition=wiktionary</c>, set after
    /// the load (docs/09-DEPLOYMENT.md §4½); a default that is safe to deploy
    /// is better than a deploy step that must be remembered.</para>
    public string LexiconEdition { get; init; } = LexiconEditions.OewnAwn;

    /// <summary>
    /// The pipeline order. Configurable per ADR-001; the product owner
    /// confirmed <c>Speaking → Writing</c> on 2026-08-15 and moved Writing to
    /// the end, behind Spelling, on 2026-09-17 (ADR-087).
    /// </summary>
    /// <remarks>
    /// Writing is last because it is the only skill that asks the learner to
    /// <i>produce</i> the written word unaided, and a learner who cannot yet
    /// spell it is being marked on two things at once. Spelling first means
    /// that by the time a sentence is asked for, the spelling is not in
    /// question — so what Writing measures is use, which is what it is for.
    /// </remarks>
    public IReadOnlyList<SkillType> SkillsOrder { get; init; } =
    [
        SkillType.Reading,
        SkillType.Listening,
        SkillType.Speaking,
        SkillType.Spelling,
        SkillType.Writing,
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

    /// <summary>
    /// A safety stop for a Speaking conversation, in learner turns per word
    /// (ADR-113). A conversation ends when every word has been used — never on
    /// a count — so this is not a rule about learning. It bounds the model calls
    /// one conversation can spend if a learner never uses a word; at ten turns a
    /// word no genuine conversation comes near it.
    /// </summary>
    public int SpeakingMaxLearnerTurnsPerWord { get; init; } = 10;

    /// <summary>
    /// How many of the latest conversation lines the tutor is sent each turn.
    /// It reads only the last few; the rest would be tokens and, past the AI
    /// service's limit, a refused request.
    /// </summary>
    public int SpeakingTranscriptWindow { get; init; } = 20;

    /// <summary>
    /// The most conversation lines the end-of-session evaluation is sent —
    /// the latest ones, where every word's last and best attempt is.
    /// </summary>
    public int SpeakingEvaluationTranscriptMax { get; init; } = 120;

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

    /// <summary>
    /// How long a password-reset code stays usable (ADR-078).
    /// </summary>
    /// <remarks>
    /// Short enough that a code read over someone's shoulder, or left in an
    /// open mailbox, is worthless by the time it is tried. Long enough that a
    /// learner can switch to their mail app, wait for delivery, and type six
    /// digits without being punished for a slow phone.
    /// </remarks>
    public int PasswordResetCodeExpiryMinutes { get; init; } = 15;

    /// <summary>
    /// Wrong guesses allowed against one code before it is burned.
    /// </summary>
    /// <remarks>
    /// The number that makes six digits safe. Rate limiting alone does not: a
    /// permitted request budget, spent patiently, still walks a million-wide
    /// space eventually. Five tries per code — and a new code invalidating the
    /// old — caps the whole attack at five guesses per email actually
    /// delivered to the learner's own inbox.
    /// </remarks>
    public int PasswordResetMaxAttempts { get; init; } = 5;

    /// <summary>
    /// How long after a refresh the same token may be presented again without
    /// it being treated as a leak (ADR-093).
    /// </summary>
    /// <remarks>
    /// Refresh tokens rotate, and presenting a used one normally means it was
    /// stolen — so the whole family is revoked. There is exactly one honest way
    /// a learner's own app does it: the exchange succeeded on the server, the
    /// reply was lost on the way back, and the app still holds the old token.
    /// On a phone that is not rare, and the consequence is a *permanent*
    /// sign-out for somebody who did nothing wrong.
    ///
    /// <para>Inside this window, and <b>only</b> when the replacement token has
    /// never been used — which is what proves nobody received it — the exchange
    /// is honoured instead. A replay after the real client has used the
    /// replacement, or one that arrives later than this, still revokes the
    /// family.</para>
    ///
    /// <para>Sixty seconds: long enough to cover a retry over a bad connection,
    /// short enough that an exfiltrated token is almost never redeemed inside
    /// it. Configuration rather than a constant, because it is a security
    /// trade-off the product owner is entitled to tighten (rule R3).</para>
    /// </remarks>
    public int RefreshReplayGraceSeconds { get; init; } = 60;

    /// <summary>
    /// The skill that follows <paramref name="skill"/> in the pipeline.
    /// </summary>
    /// <remarks>
    /// Positional, and therefore only correct for a word whose journey matches
    /// the current order. A word already in flight when the order changes does
    /// not — see <c>Word.NextPendingSkill</c>, which is what the pipeline
    /// actually advances on (ADR-087). This remains for callers that are asking
    /// about the *order* rather than about a word.
    /// </remarks>
    public SkillType? NextSkillAfter(SkillType skill)
    {
        var index = SkillsOrder.ToList().IndexOf(skill);
        if (index < 0 || index >= SkillsOrder.Count - 1) return null;
        return SkillsOrder[index + 1];
    }

    public SkillType FirstSkill => SkillsOrder[0];

    /// <summary>
    /// Where <paramref name="skill"/> sits in the pipeline — the order every
    /// list of skills is shown in (ADR-114). A skill missing from a
    /// misconfigured order sorts last rather than throwing.
    /// </summary>
    public int PipelineRank(SkillType skill)
    {
        var index = SkillsOrder.ToList().IndexOf(skill);
        return index < 0 ? int.MaxValue : index;
    }

    /// <summary>The default pipeline, for code that holds no configuration.</summary>
    public static WordOsConfiguration Default { get; } = new();

    /// <summary>
    /// Where a skill sits in the pipeline — for sorting anything shown to a
    /// learner in pipeline order.
    /// </summary>
    /// <remarks>
    /// Use this rather than <c>OrderBy(x =&gt; x.Skill)</c>. Ordering by the enum
    /// sorts by its <i>declaration</i>, which is an arbitrary fact about a
    /// source file and was quietly wrong for every word list in the app the day
    /// the order changed (ADR-087): the pipeline ran Spelling then Writing while
    /// every word's journey was drawn Writing then Spelling.
    ///
    /// A skill that is not in the order sorts last rather than throwing — a
    /// misconfigured list should mis-sort a row, not fail a request.
    /// </remarks>
    public int PipelinePosition(SkillType skill)
    {
        var index = SkillsOrder.ToList().IndexOf(skill);
        return index < 0 ? int.MaxValue : index;
    }

    public int ClampDailyTarget(int target) =>
        Math.Clamp(target, MinDailyTarget, MaxDailyTarget);
}
