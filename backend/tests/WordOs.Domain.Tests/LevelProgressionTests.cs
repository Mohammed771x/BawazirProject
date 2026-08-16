using WordOs.Domain.Common;
using WordOs.Domain.Levels;
using WordOs.Domain.Words;

namespace WordOs.Domain.Tests;

/// <summary>
/// Rule R6 in practice, ported from
/// <c>mobile/test/level_progression_test.dart</c>. Sources:
/// <c>MVP Core.txt</c> §22–23, <c>Word Life Cycle.txt</c> §27–31, ADR-013.
/// </summary>
public class LevelProgressionTests
{
    private static readonly WordOsConfiguration Config = new();
    private static readonly LevelEngine Engine = new(Config);
    private static readonly DateTimeOffset T0 =
        new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid UserId = Guid.CreateVersion7();

    private static SkillLevel LevelWith(
        SkillType skill = SkillType.Reading,
        CefrLevel system = CefrLevel.B1,
        int sessions = 0,
        double accuracy = 0)
    {
        var level = SkillLevel.Create(UserId, skill, Config, system);
        for (var i = 0; i < sessions; i++) level.RecordSession(accuracy);
        return level;
    }

    // ── When the level may move at all ────────────────────────────────────────

    [Fact]
    public void A_single_strong_session_changes_nothing()
    {
        var level = LevelWith(sessions: 1, accuracy: 1.0);

        Assert.Null(Engine.Evaluate(level));
    }

    [Fact]
    public void Nothing_happens_until_the_evaluation_window_is_full()
    {
        for (var sessions = 0; sessions < Config.MinEvaluationSessions; sessions++)
        {
            Assert.Null(Engine.Evaluate(LevelWith(sessions: sessions, accuracy: 0.99)));
        }

        Assert.NotNull(
            Engine.Evaluate(
                LevelWith(sessions: Config.MinEvaluationSessions, accuracy: 0.99)));
    }

    // ── Promotion ─────────────────────────────────────────────────────────────

    [Fact]
    public void Sustained_accuracy_at_or_above_85_percent_promotes_one_step()
    {
        var decision = Engine.Evaluate(LevelWith(sessions: 20, accuracy: 0.88))!;

        Assert.True(decision.IsPromotion);
        Assert.Equal(CefrLevel.B1, decision.Previous);
        // One step, not a whole band (MVP Core §23).
        Assert.Equal(CefrLevel.B1Plus, decision.Next);
    }

    [Fact]
    public void Exactly_at_the_threshold_promotes()
    {
        var decision = Engine.Evaluate(
            LevelWith(sessions: 20, accuracy: Config.PromoteThreshold))!;

        Assert.True(decision.IsPromotion);
    }

    [Fact]
    public void A_learner_already_at_C2_holds_instead_of_overflowing_the_ladder()
    {
        var decision = Engine.Evaluate(
            LevelWith(sessions: 20, accuracy: 1.0, system: CefrLevel.C2))!;

        Assert.False(decision.Moved);
        Assert.Null(decision.Next);
    }

    // ── Demotion ──────────────────────────────────────────────────────────────

    [Fact]
    public void Accuracy_below_70_percent_demotes_one_step()
    {
        var decision = Engine.Evaluate(LevelWith(sessions: 20, accuracy: 0.55))!;

        Assert.Equal(LevelChangeReason.Demoted, decision.Reason);
        Assert.Equal(CefrLevel.A2Plus, decision.Next);
    }

    [Fact]
    public void A_learner_already_at_A1_holds_instead_of_underflowing()
    {
        var decision = Engine.Evaluate(
            LevelWith(sessions: 20, accuracy: 0.1, system: CefrLevel.A1))!;

        Assert.False(decision.Moved);
    }

    // ── Holding ───────────────────────────────────────────────────────────────

    [Fact]
    public void Between_the_thresholds_the_level_holds_but_the_window_resets()
    {
        var level = LevelWith(sessions: 20, accuracy: 0.78);
        var decision = Engine.Evaluate(level)!;

        Assert.False(decision.Moved);

        Engine.Apply(level, decision);

        Assert.Equal(CefrLevel.B1, level.SystemAssessedLevel);
        // Spent evidence must not be re-used.
        Assert.Equal(0, level.EvaluationSessions);
    }

    // ── Rule R6 — the two levels stay separate ────────────────────────────────

    [Fact]
    public void Applying_a_decision_never_touches_the_user_selected_level()
    {
        var level = SkillLevel.Create(UserId, SkillType.Reading, Config, CefrLevel.A2);
        level.SetUserSelectedLevel(CefrLevel.C1); // learner is ambitious
        for (var i = 0; i < 20; i++) level.RecordSession(0.9);

        Engine.Apply(level, Engine.Evaluate(level)!);

        Assert.Equal(CefrLevel.C1, level.UserSelectedLevel);
        Assert.Equal(CefrLevel.A2Plus, level.SystemAssessedLevel);
    }

    [Fact]
    public void Spelling_is_never_promoted_or_demoted()
    {
        var spelling = SkillLevel.Create(UserId, SkillType.Spelling, Config);
        for (var i = 0; i < 50; i++) spelling.RecordSession(1.0);

        Assert.Null(Engine.Evaluate(spelling));
    }

    [Fact]
    public void Setting_a_CEFR_level_on_Spelling_is_refused()
    {
        var spelling = SkillLevel.Create(UserId, SkillType.Spelling, Config);

        Assert.Throws<InvalidOperationException>(
            () => spelling.SetUserSelectedLevel(CefrLevel.B1));
    }

    [Fact]
    public void The_proven_level_is_the_weakest_skill_not_the_average()
    {
        var levels = new[]
        {
            LevelWith(SkillType.Reading, CefrLevel.C1),
            LevelWith(SkillType.Listening, CefrLevel.A2),
            LevelWith(SkillType.Speaking, CefrLevel.B2),
            SkillLevel.Create(UserId, SkillType.Spelling, Config),
        };

        Assert.Equal(CefrLevel.A2, Engine.SystemValidatedLevel(levels));
    }

    // ── Archiving ─────────────────────────────────────────────────────────────

    [Fact]
    public void An_Active_word_far_below_the_proven_level_with_exposure_archives()
    {
        Assert.True(Engine.ShouldArchive(
            CefrLevel.A1, WordState.Active, 5, CefrLevel.B1));
    }

    [Fact]
    public void A_word_still_being_learned_is_never_archived()
    {
        Assert.False(Engine.ShouldArchive(
            CefrLevel.A1, WordState.Learning, 99, CefrLevel.C2));
    }

    [Fact]
    public void A_word_close_to_the_proven_level_is_kept()
    {
        // One band up is not "outgrown".
        Assert.False(Engine.ShouldArchive(
            CefrLevel.B1, WordState.Active, 99, CefrLevel.B2));
    }

    [Fact]
    public void A_word_without_enough_exposure_is_kept()
    {
        // Retire what is established, not merely what is easy (§30).
        Assert.False(Engine.ShouldArchive(
            CefrLevel.A1, WordState.Active, 0, CefrLevel.B1));
    }

    [Fact]
    public void Archiving_sweeps_only_the_words_the_learner_has_outgrown()
    {
        var easy = ActiveWord(CefrLevel.A1, exposure: 6);
        var current = ActiveWord(CefrLevel.B1, exposure: 6);
        var unseen = ActiveWord(CefrLevel.A1, exposure: 0);

        var levels = Config.SkillsOrder
            .Select(s => SkillLevel.Create(
                UserId, s, Config, s == SkillType.Spelling ? null : CefrLevel.B1))
            .ToList();

        var archived = Engine.ArchiveOutgrown([easy, current, unseen], levels, T0);

        Assert.Single(archived);
        Assert.Equal(WordState.Archived, easy.State);
        Assert.Equal(WordState.Active, current.State);
        Assert.Equal(WordState.Active, unseen.State);
    }

    private static Word ActiveWord(CefrLevel level, int exposure)
    {
        var word = Word.Add(
            UserId, $"sense-{Guid.NewGuid()}", "book", "كتاب", "a written work",
            "noun", level, Config, T0);

        var at = T0;
        foreach (var skill in Config.SkillsOrder)
        {
            word.ApplySessionResult(skill, true, Config, at);
            at = at.AddDays(Config.SkillIntervalDays);
        }

        for (var i = 0; i < exposure; i++) word.RecordExposure(at);
        return word;
    }
}
