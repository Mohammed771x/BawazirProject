using WordOs.Domain.Common;
using WordOs.Domain.Words;

namespace WordOs.Domain.Tests;

/// <summary>
/// The WordOS learning rules, ported scenario-for-scenario from
/// <c>mobile/test/word_pipeline_test.dart</c>.
/// </summary>
/// <remarks>
/// These were written as a specification against the Flutter mock precisely so
/// they could become the backend's acceptance tests. If one of these fails, the
/// backend is not implementing the documented product.
/// </remarks>
public class WordPipelineTests
{
    private static readonly WordOsConfiguration Config = new();
    private static readonly DateTimeOffset T0 =
        new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid UserId = Guid.CreateVersion7();

    private static Word AddWord(DateTimeOffset? at = null) => Word.Add(
        userId: UserId,
        senseId: "oewn-06410904-n",
        text: "research",
        meaning: "بحث علمي",
        definitionEn: "careful study to discover new facts",
        partOfSpeech: "noun",
        cefrLevel: CefrLevel.B1,
        config: Config,
        now: at ?? T0);

    [Fact]
    public void A_new_word_enters_the_pipeline_at_the_first_skill_only()
    {
        var word = AddWord();

        Assert.Equal(WordState.Learning, word.State);
        Assert.Equal(SkillType.Reading, word.CurrentSkill);
        Assert.Equal(SkillStatus.Available, word.SkillState(SkillType.Reading).Status);

        foreach (var skill in new[]
                 {
                     SkillType.Listening, SkillType.Speaking,
                     SkillType.Writing, SkillType.Spelling,
                 })
        {
            Assert.Equal(SkillStatus.Pending, word.SkillState(skill).Status);
        }
    }

    [Fact]
    public void Every_word_has_exactly_five_skill_states()
    {
        Assert.Equal(5, AddWord().Skills.Count);
    }

    [Fact]
    public void Passing_a_skill_schedules_the_next_one_after_the_configured_gap()
    {
        var word = AddWord();

        var outcome = word.ApplySessionResult(SkillType.Reading, true, Config, T0);

        Assert.True(outcome.Passed);
        Assert.Equal(SkillType.Listening, outcome.NextSkill);
        Assert.Equal(SkillStatus.Passed, word.SkillState(SkillType.Reading).Status);
        Assert.Equal(SkillType.Listening, word.CurrentSkill);

        // Not eligible before the gap elapses…
        Assert.False(word.IsEligibleFor(SkillType.Listening, T0.AddDays(1)));

        // …and eligible once it does.
        var due = T0.AddDays(Config.SkillIntervalDays);
        Assert.True(word.IsEligibleFor(SkillType.Listening, due));
        Assert.Equal(
            SkillStatus.Available,
            word.SkillState(SkillType.Listening).EffectiveStatus(due));
    }

    [Fact]
    public void A_missed_day_never_loses_the_word_it_simply_stays_due()
    {
        var word = AddWord();
        word.ApplySessionResult(SkillType.Reading, true, Config, T0);

        var muchLater = T0.AddDays(9);

        Assert.True(word.IsEligibleFor(SkillType.Listening, muchLater));
        Assert.Equal(WordState.Learning, word.State);
    }

    [Fact]
    public void Failing_one_skill_keeps_the_skills_already_passed()
    {
        var word = AddWord();
        word.ApplySessionResult(SkillType.Reading, true, Config, T0);

        var at = T0.AddDays(Config.SkillIntervalDays);
        var outcome = word.ApplySessionResult(SkillType.Listening, false, Config, at);

        Assert.False(outcome.Passed);
        Assert.Equal(
            SkillStatus.Passed,
            word.SkillState(SkillType.Reading).Status);
        Assert.Equal(
            SkillStatus.Failed,
            word.SkillState(SkillType.Listening).Status);
        Assert.Equal(SkillType.Listening, word.CurrentSkill);
        Assert.Equal(
            SkillStatus.Pending,
            word.SkillState(SkillType.Speaking).Status);
    }

    [Fact]
    public void A_failed_skill_is_retried_after_the_gap_not_immediately()
    {
        var word = AddWord();
        var outcome = word.ApplySessionResult(SkillType.Reading, false, Config, T0);

        Assert.Equal(SkillType.Reading, outcome.NextSkill);
        Assert.False(word.IsEligibleFor(SkillType.Reading, T0.AddDays(1)));
        Assert.True(
            word.IsEligibleFor(SkillType.Reading, T0.AddDays(Config.SkillIntervalDays)));
    }

    [Fact]
    public void All_five_skills_passed_makes_the_word_Active()
    {
        var word = AddWord();
        var at = T0;

        foreach (var skill in Config.SkillsOrder)
        {
            var outcome = word.ApplySessionResult(skill, true, Config, at);
            at = at.AddDays(Config.SkillIntervalDays);

            if (skill == Config.SkillsOrder[^1]) Assert.True(outcome.BecameActive);
        }

        Assert.Equal(WordState.Active, word.State);
        Assert.Null(word.CurrentSkill);
        Assert.All(word.Skills, s => Assert.Equal(SkillStatus.Passed, s.Status));
        Assert.NotNull(word.MaturedAt);
        Assert.NotNull(word.ActivatedAt);
    }

    [Fact]
    public void Skill_order_follows_configuration_Spelling_then_Writing()
    {
        // ADR-001, confirmed 2026-08-15; Writing moved behind Spelling by the
        // product owner on 2026-09-17 (ADR-087). Writing is last because it is
        // the one skill that asks for the written word unaided, and marking
        // that before Spelling has been demonstrated marks two things at once.
        Assert.Equal(
            new[]
            {
                SkillType.Reading, SkillType.Listening, SkillType.Speaking,
                SkillType.Spelling, SkillType.Writing,
            },
            Config.SkillsOrder);
    }

    [Fact]
    public void A_word_in_flight_when_the_order_changes_still_owes_every_skill()
    {
        // The migration hazard, pinned. This word was added under the old order
        // and had reached Writing — the old last skill — when the order
        // changed. Advancing by position would look up "what comes after
        // Writing", find nothing, and mature it: a word that never spelled.
        var old = Config with
        {
            SkillsOrder =
            [
                SkillType.Reading, SkillType.Listening, SkillType.Speaking,
                SkillType.Writing, SkillType.Spelling,
            ],
        };

        var word = Word.Add(
            Guid.NewGuid(), "sense-1", "plough", "محراث", "a farm tool",
            "noun", CefrLevel.B1, old, T0);

        var at = T0;
        foreach (var skill in new[]
                 {
                     SkillType.Reading, SkillType.Listening, SkillType.Speaking,
                 })
        {
            word.ApplySessionResult(skill, true, old, at);
            at = at.AddDays(old.SkillIntervalDays);
        }

        Assert.Equal(SkillType.Writing, word.CurrentSkill);

        // The order changes underneath it, and it passes the skill it was
        // already standing on.
        var outcome = word.ApplySessionResult(SkillType.Writing, true, Config, at);

        Assert.False(outcome.BecameActive);
        Assert.Equal(SkillType.Spelling, outcome.NextSkill);
        Assert.Equal(WordState.Learning, word.State);
        Assert.Equal(
            SkillStatus.Pending,
            word.SkillState(SkillType.Spelling).Status);
    }

    [Fact]
    public void A_word_that_has_passed_every_skill_matures_whatever_the_order()
    {
        var word = AddWord();
        var at = T0;

        // Deliberately not in pipeline order: what matures a word is owing
        // nothing, not arriving at a particular index.
        foreach (var skill in Config.SkillsOrder)
        {
            word.ApplySessionResult(word.CurrentSkill!.Value, true, Config, at);
            at = at.AddDays(Config.SkillIntervalDays);
            _ = skill;
        }

        Assert.Equal(WordState.Active, word.State);
        Assert.All(word.Skills, s => Assert.Equal(SkillStatus.Passed, s.Status));
    }

    [Fact]
    public void An_Active_word_is_no_longer_eligible_for_any_skill()
    {
        var word = AddWord();
        var at = T0;
        foreach (var skill in Config.SkillsOrder)
        {
            word.ApplySessionResult(skill, true, Config, at);
            at = at.AddDays(Config.SkillIntervalDays);
        }

        foreach (var skill in Config.SkillsOrder)
        {
            Assert.False(word.IsEligibleFor(skill, at));
        }
    }

    [Fact]
    public void Answering_for_a_skill_that_is_not_current_is_rejected()
    {
        var word = AddWord();

        // Speaking is three steps away; a client must not be able to skip ahead.
        Assert.Throws<InvalidOperationException>(
            () => word.ApplySessionResult(SkillType.Speaking, true, Config, T0));
    }

    [Fact]
    public void A_word_is_never_deleted_archiving_is_a_state_change_only()
    {
        var word = AddWord();
        var at = T0;
        foreach (var skill in Config.SkillsOrder)
        {
            word.ApplySessionResult(skill, true, Config, at);
            at = at.AddDays(Config.SkillIntervalDays);
        }

        var eventsBefore = word.Events.Count;
        word.Archive(at);

        Assert.Equal(WordState.Archived, word.State);
        Assert.NotNull(word.ArchivedAt);
        Assert.Equal("research", word.Text);
        Assert.True(word.Events.Count > eventsBefore);
        Assert.Equal(WordEventType.Archived, word.Events[^1].Type);
    }

    [Fact]
    public void Only_an_Active_word_may_be_archived()
    {
        var word = AddWord();

        Assert.Throws<InvalidOperationException>(() => word.Archive(T0));
    }

    [Fact]
    public void Exposure_is_recorded_but_never_removes_a_word()
    {
        var word = AddWord();
        word.RecordExposure(T0);
        word.RecordExposure(T0);

        Assert.Equal(2, word.ExposureCount);
        Assert.Equal(WordState.Learning, word.State);
    }

    [Fact]
    public void The_event_history_records_the_whole_journey()
    {
        var word = AddWord();
        var at = T0;
        foreach (var skill in Config.SkillsOrder)
        {
            word.ApplySessionResult(skill, true, Config, at);
            at = at.AddDays(Config.SkillIntervalDays);
        }

        var types = word.Events.Select(e => e.Type).ToList();

        Assert.Equal(WordEventType.Added, types[0]);
        Assert.Equal(5, types.Count(t => t == WordEventType.SkillPassed));
        Assert.Contains(WordEventType.BecameMature, types);
        Assert.Contains(WordEventType.EnteredActive, types);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_word_cannot_be_added_without_a_meaning(string meaning)
    {
        Assert.ThrowsAny<ArgumentException>(() => Word.Add(
            UserId, "sense", "book", meaning, "def", "noun",
            CefrLevel.A1, Config, T0));
    }
}
