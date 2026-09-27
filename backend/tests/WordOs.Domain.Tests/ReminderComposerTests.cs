using WordOs.Domain.Reminders;

namespace WordOs.Domain.Tests;

/// <summary>
/// What a daily reminder says, and — more importantly — what it may not say.
/// </summary>
/// <remarks>
/// These notifications are local. The phone fires them with no network, with
/// the app closed, up to a week after the server handed them over. Nothing can
/// be corrected once scheduled, so a line that rests on a fact which will have
/// gone stale is a line that will eventually be a lie — and a learner told they
/// are on a five-day streak on their third day away has learned that the app
/// does not know them (ADR-090).
/// </remarks>
public class ReminderComposerTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly DateTimeOffset T0 =
        new(2026, 9, 17, 8, 0, 0, TimeSpan.Zero);

    private static ReminderFacts Facts(
        int owned = 5,
        int learning = 5,
        int active = 0,
        int streak = 0,
        bool practisedToday = false,
        bool practisedYesterday = false,
        int? daysSince = 0,
        bool levelRose = false,
        int almostActive = 0,
        int? daysUntilNextDue = null) =>
        new(owned, learning, active, streak, practisedToday, practisedYesterday,
            daysSince, levelRose, almostActive, daysUntilNextDue,
            ReviewMaxPerSitting: 50);

    /// <summary>A week of slots, morning and evening, all with words due.</summary>
    private static List<ReminderSlot> Week(
        int wordsDue = 3,
        int ripe = 0,
        int days = 7)
    {
        var slots = new List<ReminderSlot>();
        for (var day = 0; day < days; day++)
        {
            foreach (var morning in new[] { true, false })
            {
                slots.Add(new ReminderSlot(
                    At: T0.AddDays(day).AddHours(morning ? 0 : 12),
                    Morning: morning,
                    IsToday: day == 0,
                    WordsDue: wordsDue,
                    RipeForReview: ripe));
            }
        }

        return slots;
    }

    private static IReadOnlyList<ComposedReminder> Compose(
        List<ReminderSlot> slots, ReminderFacts facts) =>
        ReminderComposer.Compose(slots, facts, UserId, 20_000);

    // ── The honesty rule ─────────────────────────────────────────────────────

    [Fact]
    public void Nothing_that_looks_backwards_is_scheduled_beyond_today()
    {
        // The sharp one. Every message below is about the learner's recent
        // past, and the past is only knowable for the slots composed today.
        var backwardLooking = new[]
        {
            ReminderMessage.WordsDueStreak,
            ReminderMessage.WordsDueStreakAtRisk,
            ReminderMessage.WordsDueAfterGoodDay,
            ReminderMessage.WordsDueWelcomeBack,
            ReminderMessage.WordsDueLevelRose,
        };

        var slots = Week();
        var composed = Compose(slots, Facts(
            streak: 6,
            practisedYesterday: true,
            daysSince: 4,
            levelRose: true));

        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i].IsToday) continue;

            Assert.DoesNotContain(composed[i].Message, backwardLooking);
        }
    }

    [Fact]
    public void A_streak_is_only_mentioned_to_somebody_who_has_one()
    {
        var composed = Compose(Week(), Facts(streak: 0));

        Assert.DoesNotContain(composed, c =>
            c.Message is ReminderMessage.WordsDueStreak
                or ReminderMessage.WordsDueStreakAtRisk);
    }

    [Fact]
    public void A_streak_at_risk_is_an_evening_message_and_only_if_untouched()
    {
        // Morning is too early to say the run ends tonight, and somebody who
        // already practised today is not at risk of anything.
        var morningOnly = new List<ReminderSlot>
        {
            new(T0, Morning: true, IsToday: true, WordsDue: 3, RipeForReview: 0),
        };

        Assert.DoesNotContain(
            Compose(morningOnly, Facts(streak: 5)),
            c => c.Message == ReminderMessage.WordsDueStreakAtRisk);

        var evening = new List<ReminderSlot>
        {
            new(T0, Morning: false, IsToday: true, WordsDue: 3, RipeForReview: 0),
        };

        Assert.DoesNotContain(
            Compose(evening, Facts(streak: 5, practisedToday: true)),
            c => c.Message == ReminderMessage.WordsDueStreakAtRisk);

        Assert.Contains(
            Compose(evening, Facts(streak: 5)),
            c => c.Message == ReminderMessage.WordsDueStreakAtRisk);
    }

    // ── Variety ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_week_of_reminders_does_not_say_the_same_thing_twice_running()
    {
        var composed = Compose(Week(), Facts());

        for (var i = 1; i < composed.Count; i++)
        {
            Assert.NotEqual(composed[i - 1].Message, composed[i].Message);
        }
    }

    [Fact]
    public void A_week_of_reminders_is_more_than_two_different_sentences()
    {
        // The whole point. Fourteen notifications alternating between two lines
        // is what the learner stops reading — and stops reading *before* they
        // stop noticing, which is worse, because they keep getting them.
        var distinct = Compose(Week(), Facts()).Select(c => c.Message).Distinct();

        Assert.True(distinct.Count() >= 4,
            $"only {distinct.Count()} distinct lines in a week");
    }

    [Fact]
    public void The_count_is_not_stated_every_single_time()
    {
        // The product owner's instruction in one assertion: do not tell them
        // the number every time. It is still available — see the message that
        // carries it — but it is no longer what a notification *is*.
        var composed = Compose(Week(), Facts());

        Assert.Contains(composed, c => c.Message != ReminderMessage.WordsDueCount);
        Assert.True(
            composed.Count(c => c.Message == ReminderMessage.WordsDueCount)
                < composed.Count / 2,
            "the count should be the exception, not the rule");
    }

    [Fact]
    public void Two_learners_do_not_read_the_same_week()
    {
        var slots = Week();
        var facts = Facts();

        var a = ReminderComposer.Compose(slots, facts, Guid.NewGuid(), 20_000);
        var b = ReminderComposer.Compose(slots, facts, Guid.NewGuid(), 20_000);

        // Not a guarantee of difference — two learners in identical situations
        // may legitimately collide — but over fourteen slots and two accounts
        // the sequences should not be identical by construction.
        var identical = 0;
        for (var i = 0; i < 40 && identical == 0; i++)
        {
            var other = ReminderComposer.Compose(
                slots, facts, Guid.NewGuid(), 20_000);
            if (!other.SequenceEqual(a)) return;
        }

        Assert.Fail("every learner received an identical week");
    }

    [Fact]
    public void The_same_facts_compose_the_same_week()
    {
        // A refresh must not reshuffle what the phone already holds: the app
        // re-fetches on every open, and a reminder that changes its mind each
        // time cannot be reasoned about at all.
        var slots = Week();
        var facts = Facts(streak: 3);

        Assert.Equal(Compose(slots, facts), Compose(slots, facts));
    }

    // ── The placeholder ──────────────────────────────────────────────────────

    [Fact]
    public void The_placeholder_never_displaces_a_real_line()
    {
        // Found by a test, not by reading. With one word due there are five
        // distinct lines; the no-repeat window forbids four of them; and the
        // only thing left unsaid was the placeholder — so an ordinary Saturday
        // morning said "open the app". It is a last resort and must behave
        // like one.
        foreach (var due in new[] { 1, 2, 9 })
        {
            Assert.DoesNotContain(
                Compose(Week(wordsDue: due), Facts()),
                c => c.Message == ReminderMessage.OpenTheApp);
        }

        Assert.DoesNotContain(
            Compose(Week(wordsDue: 0), Facts(learning: 4)),
            c => c.Message == ReminderMessage.OpenTheApp);

        Assert.DoesNotContain(
            Compose(Week(wordsDue: 0), Facts(owned: 0, learning: 0)),
            c => c.Message == ReminderMessage.OpenTheApp);
    }

    // ── The weekly challenge (ADR-089) ───────────────────────────────────────

    [Fact]
    public void The_challenge_opening_is_announced_on_the_day_it_opens()
    {
        // Not ripe today, ripe from the third day on.
        var slots = Week(ripe: 0).Select((s, i) =>
            i >= 6 ? s with { RipeForReview = 12 } : s).ToList();

        var composed = Compose(slots, Facts());

        Assert.Equal(ReminderMessage.ReviewReady, composed[6].Message);
    }

    [Fact]
    public void A_challenge_that_has_been_open_all_week_is_not_announced_daily()
    {
        // Open from the start. Saying "your challenge is ready" every morning
        // for a week is how a learner learns to swipe the notification away
        // without reading it.
        var composed = Compose(Week(ripe: 12), Facts());
        var announcements = composed.Count(c => c.Message == ReminderMessage.ReviewReady);

        // Once when it opens — which for a learner who was already ripe when
        // this was composed is the first slot — and never again from a
        // notification. The hub card carries it after that (ADR-089).
        Assert.Equal(1, announcements);
    }

    [Fact]
    public void More_than_one_sitting_is_said_out_loud()
    {
        // Fifty is the ceiling, so sixty ripe words means a second group — and
        // the learner should hear that from a notification rather than discover
        // it after finishing what they thought was everything.
        var composed = Compose(Week(ripe: 60), Facts());

        Assert.Contains(composed, c => c.Message == ReminderMessage.ReviewMoreWaiting);
    }

    [Fact]
    public void Neither_challenge_message_carries_a_number()
    {
        // By instruction: how many words ripened this week is the product's
        // bookkeeping. A count here would make a quiet week look like a
        // failure and a busy one look like a chore.
        var composed = Compose(Week(ripe: 60), Facts());

        Assert.All(
            composed.Where(c => c.Message is ReminderMessage.ReviewReady
                or ReminderMessage.ReviewMoreWaiting),
            c => Assert.Equal(0, c.Count));
    }

    // ── The three kinds still hold ───────────────────────────────────────────

    [Fact]
    public void A_learner_with_no_words_is_never_told_they_have_some()
    {
        var composed = Compose(Week(wordsDue: 0), Facts(owned: 0, learning: 0));

        Assert.All(composed, c => Assert.Equal(ReminderKind.NoWords, c.Kind));
        Assert.All(composed, c => Assert.True(
            c.Message is ReminderMessage.NoWordsFirst
                or ReminderMessage.NoWordsOneADay,
            $"{c.Message} is not a sentence for somebody with no vocabulary"));
    }

    [Fact]
    public void An_empty_day_with_words_learning_is_not_an_empty_vocabulary()
    {
        var composed = Compose(
            Week(wordsDue: 0), Facts(owned: 6, learning: 6, active: 2));

        Assert.All(composed, c => Assert.Equal(ReminderKind.NothingDue, c.Kind));
        Assert.DoesNotContain(composed, c =>
            c.Message is ReminderMessage.NoWordsFirst
                or ReminderMessage.NoWordsOneADay);
    }
}
