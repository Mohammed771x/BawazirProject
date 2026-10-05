namespace WordOs.Domain.Reminders;

/// <summary>
/// The exact line a daily reminder says — one of twenty (ADR-090).
/// </summary>
/// <remarks>
/// A key, never a sentence: the server does not know which language this
/// installation reads, so the client says it (ADR-035). What the server decides
/// is <b>which</b> line, because only the server knows the facts that make one
/// line true and another one a lie.
///
/// <para>Twenty rather than three because a notification is the whole decision
/// about whether the app gets opened today, and the same sentence twice a day
/// for a fortnight stops being read. It stops being read <i>before</i> it stops
/// being noticed, which is worse: the learner keeps receiving it and has
/// learned that it never says anything.</para>
///
/// <para><b>Every line is gated on a fact.</b> Nothing here is decoration. A
/// message about a streak is only ever sent to somebody who has one; a message
/// about yesterday is only ever sent where yesterday is still yesterday when it
/// fires. That second constraint is the sharp one — see
/// <see cref="ReminderComposer"/>.</para>
/// </remarks>
public enum ReminderMessage
{
    // ── Words are waiting ────────────────────────────────────────────────────

    /// <summary>The plain one, with the number. <c>Count</c> = words due.</summary>
    /// <remarks>
    /// Kept, and deliberately not the default any more. The number is useful —
    /// "you have words waiting" is a nag and "you have four" is a task with an
    /// end to it — but said every single time it becomes the only thing the
    /// notification ever is, and the learner reads the digit instead of the
    /// sentence.
    /// </remarks>
    WordsDueCount,

    /// <summary>Exactly one word. <c>Count</c> = 1.</summary>
    WordsDueOne,

    /// <summary>The size of the ask, rather than the size of the list.</summary>
    /// <remarks>
    /// Never chosen any more (ADR-121): its line in every installed build read,
    /// in Arabic, as a saying about Friday. Kept so the wire value still means
    /// what it meant; <see cref="WordsDueShortSession"/> took its place.
    /// </remarks>
    WordsDueFiveMinutes,

    /// <summary>
    /// The size of the ask, said plainly (ADR-121). A build that predates it
    /// does not know the key and falls back on the kind, which is a true line.
    /// </summary>
    WordsDueShortSession,

    /// <summary>Morning framing — before the day takes the time.</summary>
    WordsDueMorning,

    /// <summary>Evening framing — the day is nearly over.</summary>
    WordsDueEvening,

    /// <summary>A run of days. <c>Count</c> = the streak.</summary>
    WordsDueStreak,

    /// <summary>A run of days about to end tonight. <c>Count</c> = the streak.</summary>
    WordsDueStreakAtRisk,

    /// <summary>They practised yesterday and it went well.</summary>
    WordsDueAfterGoodDay,

    /// <summary>Some days away. Written to welcome, never to scold.</summary>
    WordsDueWelcomeBack,

    /// <summary>Their level went up recently.</summary>
    WordsDueLevelRose,

    /// <summary>
    /// Words one skill from finishing the pipeline. <c>Count</c> = how many.
    /// </summary>
    WordsDueAlmostActive,

    // ── Nothing due, but they are learning ───────────────────────────────────

    /// <summary>The spaced gap, said as the feature it is.</summary>
    NothingDueResting,

    /// <summary>An empty day is the day to add a word.</summary>
    NothingDueAddOne,

    /// <summary>What has made it all the way. <c>Count</c> = active words.</summary>
    NothingDueActiveCount,

    /// <summary>When the next word comes due. <c>Count</c> = days.</summary>
    NothingDueNextOpens,

    // ── Nothing at all ───────────────────────────────────────────────────────

    /// <summary>Never added a word.</summary>
    NoWordsFirst,

    /// <summary>Never added a word, said the other way.</summary>
    NoWordsOneADay,

    // ── The weekly challenge (ADR-089) ───────────────────────────────────────

    /// <summary>
    /// The challenge has opened.
    /// </summary>
    /// <remarks>
    /// Carries <b>no number</b>, by instruction: how many words ripened this
    /// week is the product's bookkeeping, not the learner's business, and a
    /// count here would make a quiet week look like a failure.
    /// </remarks>
    ReviewReady,

    /// <summary>More than one sitting's worth is ripe. Also no number.</summary>
    ReviewMoreWaiting,

    // ── Last resort ──────────────────────────────────────────────────────────

    /// <summary>
    /// Nothing else applied. Never sent in practice; present so composing a
    /// reminder is a total function and a gap in the rules shows up as a dull
    /// message rather than as an exception in a background refresh.
    /// </summary>
    OpenTheApp,
}
