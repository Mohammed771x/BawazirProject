using WordOs.Domain.Common;

namespace WordOs.Domain.Reminders;

/// <summary>What a reminder is about, coarsely.</summary>
/// <remarks>
/// Three values, unchanged since ADR-076, and kept beside the finer
/// <see cref="ReminderMessage"/> on purpose: a client that has never heard of a
/// message key can still say something true from the kind alone. The server is
/// updated more often than the phones are.
/// </remarks>
public enum ReminderKind
{
    WordsDue,
    NothingDue,
    NoWords,
}

/// <summary>One slot to fill: a moment, and what will be true at it.</summary>
/// <param name="IsToday">
/// Whether this slot falls on the day the reminders were composed.
/// </param>
public sealed record ReminderSlot(
    DateTimeOffset At,
    bool Morning,
    bool IsToday,
    int WordsDue,
    int RipeForReview);

/// <summary>
/// What is true about this learner right now.
/// </summary>
/// <param name="StreakDays">
/// Consecutive days ending today or yesterday on which they finished something.
/// Zero when there is no run.
/// </param>
/// <param name="DaysSinceLastSession">
/// Null when they have never finished anything.
/// </param>
/// <param name="AlmostActive">
/// Words one skill short of finishing the pipeline.
/// </param>
public sealed record ReminderFacts(
    int OwnedWords,
    int LearningWords,
    int ActiveWords,
    int StreakDays,
    bool PractisedToday,
    bool PractisedYesterday,
    int? DaysSinceLastSession,
    bool LevelRoseRecently,
    int AlmostActive,
    int? DaysUntilNextDue,
    int ReviewMaxPerSitting);

/// <summary>The line a reminder will say, and the number in it.</summary>
public sealed record ComposedReminder(ReminderKind Kind, ReminderMessage Message, int Count);

/// <summary>
/// Chooses what each daily reminder says (ADR-090).
/// </summary>
/// <remarks>
/// <b>The constraint that shapes all of this:</b> these are *local*
/// notifications. The phone fires them with no network, usually with the app
/// closed, up to a week after they were handed over — so every word of them is
/// decided here, in advance, and nothing can be corrected once scheduled.
///
/// <para>Which means a message may only rest on a fact that will <i>still be
/// true when it fires</i>. "You practised yesterday" is knowable for this
/// evening and a guess by Thursday; a streak of four is a streak of four today
/// and unknown after that. So every message about the learner's recent past is
/// restricted to <see cref="ReminderSlot.IsToday"/>, and the days beyond it are
/// filled only from facts the schedule itself projects: what will be due, what
/// will have ripened, what they own. A phone that re-fetches — which it does on
/// every app open — keeps getting the good ones.</para>
///
/// <para>A reminder that lies is worse than a dull one. A learner who is told
/// they are on a five-day streak on the third day of not opening the app has
/// learned that the app does not know them, and there is no recovering that
/// with a better sentence next week.</para>
///
/// <para><b>Variety</b> is the second job. Candidates are ranked, the best one
/// that has not just been used wins, and ties are broken with a hash of the
/// learner and the date so two people on the same day do not read the same
/// script. Deterministic throughout: the same facts compose the same week, so a
/// refresh does not reshuffle what the phone already holds.</para>
/// </remarks>
public static class ReminderComposer
{
    /// <summary>
    /// How many of the previous choices a message may not repeat.
    /// </summary>
    /// <remarks>
    /// Four covers two full days, morning and evening. Enough that a line does
    /// not come back before the learner has forgotten it; small enough that a
    /// learner with only two eligible messages still gets both rather than
    /// falling through to the dull one.
    /// </remarks>
    private const int NoRepeatWindow = 4;

    public static IReadOnlyList<ComposedReminder> Compose(
        IReadOnlyList<ReminderSlot> slots,
        ReminderFacts facts,
        Guid userId,
        int seedDay)
    {
        var chosen = new List<ComposedReminder>(slots.Count);
        var recent = new List<ReminderMessage>();

        // The challenge is announced **twice at most** (ADR-089): once when it
        // opens, and once the next day if more than one sitting is waiting.
        //
        // Not once a day while it stands open. That was the first version, and
        // a test caught it saying "your challenge is ready" four times in a
        // week — which is how a learner learns to swipe the notification away
        // without reading it, and they do not learn that for one message only.
        var firstRipe = -1;
        for (var i = 0; i < slots.Count && firstRipe < 0; i++)
        {
            if (slots[i].RipeForReview > 0) firstRipe = i;
        }

        var followUpDay = firstRipe < 0
            ? (DateOnly?)null
            : DateOnly.FromDateTime(slots[firstRipe].At.Date).AddDays(1);

        var followedUp = false;

        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];

            var kind = facts.OwnedWords == 0
                ? ReminderKind.NoWords
                : slot.WordsDue > 0
                    ? ReminderKind.WordsDue
                    : ReminderKind.NothingDue;

            // ── Time-critical lines are never rotated away ───────────────────
            //
            // The rotation exists so a learner is not read the same script all
            // week. It must not be allowed to swallow a message whose whole
            // value is the moment it arrives: the day the challenge opens, and
            // the evening a run of days is about to end. A test caught it
            // trading "your streak ends at midnight" for "you are on a
            // five-day streak", which is the same fact with the urgency taken
            // out of it.
            ComposedReminder? pinned = null;

            if (i == firstRipe)
            {
                pinned = new ComposedReminder(kind, ReminderMessage.ReviewReady, 0);
            }
            else if (!followedUp
                     && followUpDay is not null
                     && slot.RipeForReview > facts.ReviewMaxPerSitting
                     && DateOnly.FromDateTime(slot.At.Date) == followUpDay)
            {
                pinned = new ComposedReminder(
                    kind, ReminderMessage.ReviewMoreWaiting, 0);
                followedUp = true;
            }
            else if (slot.IsToday
                     && !slot.Morning
                     && kind == ReminderKind.WordsDue
                     && facts.StreakDays >= 2
                     && !facts.PractisedToday)
            {
                pinned = new ComposedReminder(
                    kind, ReminderMessage.WordsDueStreakAtRisk, facts.StreakDays);
            }

            var pick = pinned
                       ?? Pick(
                           Candidates(slot, facts, kind).ToList(),
                           recent, userId, seedDay, slot);

            chosen.Add(pick);

            recent.Add(pick.Message);
            if (recent.Count > NoRepeatWindow) recent.RemoveAt(0);
        }

        return chosen;
    }

    /// <summary>
    /// Every line that is true at this slot, best first.
    /// </summary>
    /// <remarks>
    /// Order is priority, not preference: the first entry is what this learner
    /// most needs to hear at this moment, and the rotation only ever moves down
    /// the list to avoid repeating itself.
    /// </remarks>
    private static IEnumerable<ComposedReminder> Candidates(
        ReminderSlot slot,
        ReminderFacts facts,
        ReminderKind kind)
    {
        ComposedReminder Of(ReminderMessage m, int count = 0) =>
            new(kind, m, count);

        switch (kind)
        {
            case ReminderKind.NoWords:
                yield return Of(ReminderMessage.NoWordsFirst);
                yield return Of(ReminderMessage.NoWordsOneADay);
                break;

            case ReminderKind.WordsDue:
                // ── Today only: everything that looks backwards ──────────────
                if (slot.IsToday)
                {
                    // The at-risk variant is pinned in Compose, not offered
                    // here: it is a deadline, and a deadline that loses a coin
                    // toss is not a deadline.
                    if (facts.StreakDays >= 2)
                        yield return Of(ReminderMessage.WordsDueStreak, facts.StreakDays);

                    if (facts.DaysSinceLastSession >= 3)
                        yield return Of(ReminderMessage.WordsDueWelcomeBack);

                    if (facts.LevelRoseRecently)
                        yield return Of(ReminderMessage.WordsDueLevelRose);

                    if (facts.PractisedYesterday)
                        yield return Of(ReminderMessage.WordsDueAfterGoodDay);
                }

                if (facts.AlmostActive > 0)
                    yield return Of(ReminderMessage.WordsDueAlmostActive, facts.AlmostActive);

                if (slot.WordsDue == 1)
                    yield return Of(ReminderMessage.WordsDueOne, 1);

                yield return Of(
                    slot.Morning
                        ? ReminderMessage.WordsDueMorning
                        : ReminderMessage.WordsDueEvening);

                // Not WordsDueFiveMinutes any more (ADR-121). Its line in every
                // build already installed read, in Arabic, as a saying about
                // Friday, and the text lives in the app — so the only way to
                // stop it reaching a phone today is never to choose it. A new
                // key, so an old build falls back on the kind instead.
                yield return Of(ReminderMessage.WordsDueShortSession);
                yield return Of(ReminderMessage.WordsDueCount, slot.WordsDue);
                break;

            case ReminderKind.NothingDue:
                if (facts.DaysUntilNextDue is > 0 and <= 7)
                    yield return Of(
                        ReminderMessage.NothingDueNextOpens,
                        facts.DaysUntilNextDue.Value);

                if (facts.ActiveWords > 0)
                    yield return Of(ReminderMessage.NothingDueActiveCount, facts.ActiveWords);

                yield return Of(ReminderMessage.NothingDueAddOne);
                yield return Of(ReminderMessage.NothingDueResting, facts.LearningWords);
                break;
        }

        // Never reached with real facts; see ReminderMessage.OpenTheApp.
        yield return Of(ReminderMessage.OpenTheApp);
    }

    private static ComposedReminder Pick(
        IReadOnlyList<ComposedReminder> candidates,
        IReadOnlyList<ReminderMessage> recent,
        Guid userId,
        int seedDay,
        ReminderSlot slot)
    {
        // The placeholder is not a candidate while anything real exists, and it
        // has to be removed *before* the no-repeat filter rather than ranked
        // below it. Ranking was not enough: a learner with one word due has
        // five distinct lines, the window forbids four of them, and the only
        // thing left unsaid was the placeholder — so a perfectly ordinary
        // Saturday morning got "open the app". Caught by a test asserting that
        // every line of a words-due week is a words-due line.
        var real = candidates
            .Where(c => c.Message != ReminderMessage.OpenTheApp)
            .ToList();

        if (real.Count == 0) return candidates[^1];

        var fresh = real.Where(c => !recent.Contains(c.Message)).ToList();

        // Everything real has just been said. Repeating the best one beats
        // saying nothing useful — variety is worth less than truth, and this
        // only happens to a learner whose situation offers few distinct things
        // to say in the first place.
        if (fresh.Count == 0) return real[0];

        // The top two, rotated. Always taking the first would make the priority
        // list a script; rotating the whole list would bury the message that
        // matters under whatever happened to be next.
        if (fresh.Count == 1) return fresh[0];

        var seed = HashCode.Combine(
            userId, seedDay, slot.At.DayOfYear, slot.Morning);

        return fresh[(seed & int.MaxValue) % Math.Min(2, fresh.Count)];
    }
}
