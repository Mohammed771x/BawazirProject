using WordOs.Domain.Common;
using WordOs.Domain.Levels;
using WordOs.Domain.Review;
using WordOs.Domain.Words;

namespace WordOs.Domain.Tests.Qa;

/// <summary>
/// QA campaign 20261111-0121-d365f09, prompt 02: the boundaries the existing
/// pipeline tests do not pin, and a seeded random walk over the aggregate.
/// </summary>
/// <remarks>
/// Expected values come from the configuration and the ADRs, written out by
/// hand here — not recomputed with the production helpers — so a wrong rule
/// cannot pass by agreeing with itself.
/// </remarks>
public class LifecycleBoundaryQaTests
{
    private static readonly WordOsConfiguration Config = new();
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid User = Guid.CreateVersion7();

    private static Word NewWord(CefrLevel level = CefrLevel.B1, DateTimeOffset? at = null) =>
        Word.Add(User, $"qa-{Guid.NewGuid():N}", "research", "بحث", "study", "n",
            level, Config, at ?? T0);

    private static readonly SkillType[] Order =
        [SkillType.Reading, SkillType.Listening, SkillType.Speaking, SkillType.Spelling, SkillType.Writing];

    // ── AvailableAt boundary (property 2) ────────────────────────────────────

    [Fact]
    public void Next_skill_opens_exactly_at_the_gap_not_one_tick_before()
    {
        var word = NewWord();
        word.ApplySessionResult(SkillType.Reading, true, Config, T0);
        var opens = T0.AddDays(2); // SkillIntervalDays = 2, written out

        Assert.False(word.IsEligibleFor(SkillType.Listening, opens.AddTicks(-1)));
        Assert.True(word.IsEligibleFor(SkillType.Listening, opens));
        Assert.True(word.IsEligibleFor(SkillType.Listening, opens.AddTicks(1)));
        // Never for a skill that is not current, whatever the clock says.
        Assert.False(word.IsEligibleFor(SkillType.Speaking, opens.AddYears(1)));
    }

    [Fact]
    public void A_failed_skill_reopens_at_the_gap_from_the_failure()
    {
        var word = NewWord();
        var failedAt = T0.AddHours(5);
        word.ApplySessionResult(SkillType.Reading, false, Config, failedAt);

        Assert.False(word.IsEligibleFor(SkillType.Reading, failedAt.AddDays(2).AddTicks(-1)));
        Assert.True(word.IsEligibleFor(SkillType.Reading, failedAt.AddDays(2)));
        Assert.Equal(SkillStatus.Failed, word.SkillState(SkillType.Reading).Status);
    }

    [Fact]
    public void A_custom_gap_is_obeyed()
    {
        var config = new WordOsConfiguration { SkillIntervalDays = 5 };
        var word = Word.Add(User, "qa-gap", "x", "س", "d", "n", CefrLevel.A1, config, T0);
        word.ApplySessionResult(SkillType.Reading, true, config, T0);

        Assert.False(word.IsEligibleFor(SkillType.Listening, T0.AddDays(5).AddTicks(-1)));
        Assert.True(word.IsEligibleFor(SkillType.Listening, T0.AddDays(5)));
    }

    // ── Seeded random walk (properties 1, 2, 4, R5) ──────────────────────────

    [Theory]
    [InlineData(20261111)]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(987654)]
    public void Random_pass_fail_sequences_keep_every_invariant(int seed)
    {
        var random = new Random(seed);
        for (var run = 0; run < 200; run++)
        {
            var word = NewWord();
            var now = T0;
            var passedSoFar = new HashSet<SkillType>();
            var steps = 0;

            while (word.State == WordState.Learning && steps++ < 60)
            {
                var skill = word.CurrentSkill!.Value;
                var opens = word.SkillState(skill).AvailableAt ?? now;
                if (opens > now) now = opens;

                // Expected current skill: the first unpassed one in order.
                Assert.Equal(Order.First(s => !passedSoFar.Contains(s)), skill);
                Assert.True(word.IsEligibleFor(skill, now));

                var pass = random.NextDouble() < 0.6;
                word.ApplySessionResult(skill, pass, Config, now);
                if (pass) passedSoFar.Add(skill);

                // R5: nothing already passed is ever un-passed.
                foreach (var s in passedSoFar)
                    Assert.Equal(SkillStatus.Passed, word.SkillState(s).Status);
                Assert.Equal(5, word.Skills.Count);
                now = now.AddHours(random.Next(1, 100));
            }

            Assert.Equal(WordState.Active, word.State);
            Assert.Null(word.CurrentSkill);
            Assert.All(word.Skills, s => Assert.Equal(SkillStatus.Passed, s.Status));
            Assert.NotNull(word.MaturedAt);
            Assert.Equal(word.MaturedAt, word.ActivatedAt);
            Assert.Single(word.Events, e => e.Type == WordEventType.BecameMature);
            Assert.Single(word.Events, e => e.Type == WordEventType.EnteredActive);
            Assert.Equal(5, word.Events.Count(e => e.Type == WordEventType.SkillPassed));
            Assert.Single(word.Events, e => e.Type == WordEventType.Added);
        }
    }

    [Fact]
    public void An_active_word_refuses_any_further_session_result()
    {
        var word = NewWord();
        var now = T0;
        foreach (var skill in Order)
        {
            word.ApplySessionResult(skill, true, Config, now);
            now = now.AddDays(2);
        }

        Assert.Throws<InvalidOperationException>(() =>
            word.ApplySessionResult(SkillType.Writing, true, Config, now));
    }

    // ── Delete and change meaning (property 6) ───────────────────────────────

    [Fact]
    public void A_deleted_word_takes_no_session_result_and_is_not_eligible()
    {
        var word = NewWord();
        word.Delete(T0.AddHours(1));

        Assert.False(word.IsEligibleFor(SkillType.Reading, T0.AddYears(1)));
        Assert.Throws<InvalidOperationException>(() =>
            word.ApplySessionResult(SkillType.Reading, true, Config, T0.AddHours(2)));
        Assert.False(WeeklyReviewPolicy.IsRipe(word, T0.AddYears(1), Config));
    }

    [Fact]
    public void Changing_the_meaning_moves_nothing_in_the_journey()
    {
        var word = NewWord();
        word.ApplySessionResult(SkillType.Reading, true, Config, T0);
        word.ApplySessionResult(SkillType.Listening, false, Config, T0.AddDays(2));
        word.RecordExposure(T0.AddDays(2));

        var before = Snapshot(word);
        word.ChangeMeaning("دراسة", MeaningSource.Learner, null, T0.AddDays(3),
            definitionEn: "new def", partOfSpeech: "n", cefrLevel: CefrLevel.B2);

        Assert.Equal(before, Snapshot(word));
        Assert.Equal("دراسة", word.Meaning);
        Assert.Equal(CefrLevel.B2, word.CefrLevel);
    }

    private static string Snapshot(Word w) =>
        $"{w.State}|{w.CurrentSkill}|{w.AddedAt:O}|{w.ExposureCount}|" +
        string.Join(";", w.Skills.OrderBy(s => s.Skill).Select(s =>
            $"{s.Skill}:{s.Status}:{s.AvailableAt:O}:{s.Attempts}:{s.PassedAt:O}:{s.FailedAt:O}"));

    // ── Level engine thresholds (property 8) ─────────────────────────────────

    private static SkillLevel LevelWith(SkillType skill, CefrLevel at, int sessions, double accuracy)
    {
        var level = SkillLevel.Create(User, skill, Config, at);
        for (var i = 0; i < sessions; i++) level.RecordSession(accuracy);
        return level;
    }

    [Fact]
    public void Thirteen_sessions_decide_nothing_and_fourteen_do()
    {
        var engine = new LevelEngine(Config);
        Assert.Null(engine.Evaluate(LevelWith(SkillType.Reading, CefrLevel.B1, 13, 1.0)));
        var decision = engine.Evaluate(LevelWith(SkillType.Reading, CefrLevel.B1, 14, 1.0));
        Assert.NotNull(decision);
        Assert.Equal(CefrLevel.B1Plus, decision!.Next);
    }

    [Theory]
    [InlineData(0.85, "promote")]
    [InlineData(0.8499, "hold")]
    [InlineData(0.70, "hold")]
    [InlineData(0.6999, "demote")]
    [InlineData(0.0, "demote")]
    public void Threshold_edges_follow_the_configuration(double accuracy, string expected)
    {
        var engine = new LevelEngine(Config);
        var decision = engine.Evaluate(LevelWith(SkillType.Listening, CefrLevel.B1, 14, accuracy))!;

        var actual = decision.Next switch
        {
            CefrLevel.B1Plus => "promote",
            CefrLevel.A2Plus => "demote",
            _ => "hold",
        };
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Spelling_never_carries_or_moves_a_level()
    {
        var engine = new LevelEngine(Config);
        var spelling = LevelWith(SkillType.Spelling, CefrLevel.C1, 30, 1.0);
        Assert.Null(spelling.SystemAssessedLevel);
        Assert.Null(engine.Evaluate(spelling));
    }

    // ── Archiving, each condition alone (property 5) ─────────────────────────

    [Theory]
    // proven B2 (rank 6). Gap 4 steps → word at A2 (rank 2) qualifies; A2+ (3) does not.
    [InlineData(CefrLevel.A2, 3, true)]
    [InlineData(CefrLevel.A2Plus, 3, false)]
    [InlineData(CefrLevel.A2, 2, false)]
    [InlineData(CefrLevel.A1, 100, true)]
    public void Archive_needs_both_the_gap_and_the_exposure(CefrLevel wordLevel, int exposure, bool expected)
    {
        var engine = new LevelEngine(Config);
        Assert.Equal(expected,
            engine.ShouldArchive(wordLevel, WordState.Active, exposure, CefrLevel.B2));
    }

    [Theory]
    [InlineData(WordState.Learning)]
    [InlineData(WordState.Archived)]
    [InlineData(WordState.Deleted)]
    public void Only_an_active_word_is_ever_archived(WordState state)
    {
        var engine = new LevelEngine(Config);
        Assert.False(engine.ShouldArchive(CefrLevel.A1, state, 100, CefrLevel.C2));
    }

    [Fact]
    public void The_weakest_validated_skill_decides_and_the_user_selected_level_does_not()
    {
        var engine = new LevelEngine(Config);
        var levels = new[]
        {
            SkillLevel.Create(User, SkillType.Reading, Config, CefrLevel.C2),
            SkillLevel.Create(User, SkillType.Listening, Config, CefrLevel.B1),
            SkillLevel.Create(User, SkillType.Speaking, Config, CefrLevel.C2),
            SkillLevel.Create(User, SkillType.Writing, Config, CefrLevel.C2),
            SkillLevel.Create(User, SkillType.Spelling, Config),
        };
        foreach (var l in levels.Where(l => l.CarriesCefrLevel))
            l.SetUserSelectedLevel(CefrLevel.C2);

        Assert.Equal(CefrLevel.B1, engine.SystemValidatedLevel(levels));
        // B1 is rank 4; an A2 word (rank 2) is only 2 steps below → kept.
        Assert.False(engine.ShouldArchive(CefrLevel.A2, WordState.Active, 10,
            engine.SystemValidatedLevel(levels)));
    }

    [Fact]
    public void Archiving_keeps_the_row_and_its_history()
    {
        var word = NewWord(CefrLevel.A1);
        var now = T0;
        foreach (var skill in Order) { word.ApplySessionResult(skill, true, Config, now); now = now.AddDays(2); }
        for (var i = 0; i < 3; i++) word.RecordExposure(now);
        var eventsBefore = word.Events.Count;

        word.Archive(now);

        Assert.Equal(WordState.Archived, word.State);
        Assert.Equal(eventsBefore + 1, word.Events.Count);
        Assert.All(word.Skills, s => Assert.Equal(SkillStatus.Passed, s.Status));
    }

    // ── Weekly review policy (property 7) ────────────────────────────────────

    [Fact]
    public void A_word_ripens_exactly_seven_days_after_it_was_added()
    {
        var word = NewWord();
        Assert.False(WeeklyReviewPolicy.IsRipe(word, T0.AddDays(7).AddTicks(-1), Config));
        Assert.True(WeeklyReviewPolicy.IsRipe(word, T0.AddDays(7), Config));
    }

    [Fact]
    public void A_missed_word_ripens_seven_days_after_the_review_that_missed_it()
    {
        var word = NewWord();
        var reviewed = T0.AddDays(9);
        word.MarkReviewed(reviewed, passed: false);
        Assert.False(WeeklyReviewPolicy.IsRipe(word, reviewed.AddDays(7).AddTicks(-1), Config));
        Assert.True(WeeklyReviewPolicy.IsRipe(word, reviewed.AddDays(7), Config));

        word.MarkReviewed(reviewed.AddDays(7), passed: true);
        word.MarkReviewed(reviewed.AddDays(14), passed: false); // a later miss does not un-pass
        Assert.Equal(reviewed.AddDays(7), word.ReviewPassedAt);
        Assert.False(WeeklyReviewPolicy.IsRipe(word, reviewed.AddYears(1), Config));
    }

    [Fact]
    public void A_sitting_is_capped_at_fifty_oldest_first_and_the_rest_stay_ripe()
    {
        var words = Enumerable.Range(0, 73)
            .Select(i => NewWord(at: T0.AddMinutes(i)))
            .Reverse() // input order must not matter
            .ToList();
        var now = T0.AddDays(30);

        var sitting = WeeklyReviewPolicy.NextSitting(words, now, Config);
        Assert.Equal(50, sitting.Count);
        Assert.Equal(T0, sitting[0].AddedAt);
        Assert.Equal(73, WeeklyReviewPolicy.Ripe(words, now, Config).Count);
    }

    [Fact]
    public void Reviewing_touches_only_the_review_fields()
    {
        var word = NewWord();
        word.ApplySessionResult(SkillType.Reading, true, Config, T0);
        var before = Snapshot(word);

        word.MarkReviewed(T0.AddDays(8), passed: true);

        Assert.Equal(before, Snapshot(word));
    }
}
