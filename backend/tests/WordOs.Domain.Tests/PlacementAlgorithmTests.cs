using WordOs.Domain.Common;
using WordOs.Domain.Placement;

namespace WordOs.Domain.Tests;

/// <summary>
/// The placement algorithm, ported from
/// <c>mobile/test/placement_algorithm_test.dart</c>.
/// </summary>
/// <remarks>
/// Written against behaviour, not internals, so it survives a change of
/// estimator. `docs/06-PLACEMENT-ALGORITHM.md` §9 explains the replacement
/// path; these tests are the acceptance suite for it.
/// </remarks>
public class PlacementAlgorithmTests
{
    private static readonly PlacementEngine Engine = new();
    private static readonly PlacementConfig Config = new();

    /// <summary>Runs a whole placement, answering each item via the callback.</summary>
    private static PlacementOutcome RunPlacement(
        Func<BankItem, string> respond,
        int seed = 7)
    {
        var random = new Random(seed);
        var responses = new List<PlacementResponse>();

        var guard = 0;
        while (true)
        {
            Assert.True(++guard < 100, "the adaptive loop must always terminate");

            var item = Engine.NextItem(responses, random);
            if (item is null) break;

            responses.Add(new PlacementResponse(
                item.Id,
                item.Skill,
                Config.Scale.DifficultyOf(item.Level),
                Engine.ScoreAnswer(item, respond(item))));
        }

        return Engine.Complete(responses);
    }

    private static string CorrectFor(BankItem item) =>
        item.IsFreeText ? CompetentAnswerFor(item) : item.CorrectAnswer!;

    private static string WrongFor(BankItem item) =>
        item.IsFreeText ? "no" : item.Options.First(o => o != item.CorrectAnswer);

    private static CefrLevel? LevelOf(PlacementOutcome outcome, SkillType skill) =>
        outcome.Levels.Single(l => l.Skill == skill).Level;

    // ── The estimator ────────────────────────────────────────────────────────

    [Fact]
    public void A_correct_answer_raises_ability_and_a_wrong_one_lowers_it()
    {
        var estimator = new AbilityEstimator();
        var neutral = estimator.Estimate([]);

        var afterCorrect = estimator.Estimate([new ScoredResponse("a", 0, 1)]);
        var afterWrong = estimator.Estimate([new ScoredResponse("a", 0, 0)]);

        Assert.True(afterCorrect.Theta > neutral.Theta);
        Assert.True(afterWrong.Theta < neutral.Theta);
    }

    [Fact]
    public void More_answers_shrink_the_standard_error()
    {
        var estimator = new AbilityEstimator();
        var few = estimator.Estimate([new ScoredResponse("a", 0, 1)]);
        var many = estimator.Estimate(
        [
            new("a", 0.0, 1), new("b", 0.5, 1), new("c", 1.0, 0),
            new("d", 0.5, 1), new("e", 1.0, 0),
        ]);

        Assert.True(many.StandardError < few.StandardError);
    }

    [Fact]
    public void A_long_run_of_answers_does_not_underflow_into_NaN()
    {
        var estimator = new AbilityEstimator();
        var responses = Enumerable.Range(0, 200)
            .Select(i => new ScoredResponse(i.ToString(), 2.5, 0))
            .ToList();

        var estimate = estimator.Estimate(responses);

        Assert.False(double.IsNaN(estimate.Theta));
        Assert.False(double.IsNaN(estimate.StandardError));
        Assert.True(double.IsFinite(estimate.Theta));
    }

    [Fact]
    public void Partial_credit_sits_between_a_wrong_and_a_right_answer()
    {
        var estimator = new AbilityEstimator();
        double ThetaFor(double score) =>
            estimator.Estimate([new ScoredResponse("a", 0, score)]).Theta;

        Assert.True(ThetaFor(0.5) > ThetaFor(0.0));
        Assert.True(ThetaFor(0.5) < ThetaFor(1.0));
    }

    [Fact]
    public void Fisher_information_peaks_where_difficulty_equals_ability()
    {
        // This is why "closest difficulty" is the optimal next-item rule.
        var atTarget = AbilityEstimator.Information(0.0, 0.0);
        var offTarget = AbilityEstimator.Information(0.0, 1.5);

        Assert.True(atTarget > offTarget);
    }

    // ── A full run ───────────────────────────────────────────────────────────

    [Fact]
    public void A_learner_who_answers_everything_correctly_places_high()
    {
        var outcome = RunPlacement(CorrectFor);

        foreach (var skill in PlacementItemBank.CefrSkills)
        {
            var level = LevelOf(outcome, skill);
            Assert.NotNull(level);
            Assert.True(level!.Value.Rank() >= CefrLevel.B1.Rank(),
                $"{skill} should not place low after a perfect run");
        }
    }

    [Fact]
    public void A_learner_who_answers_everything_wrongly_places_low()
    {
        var outcome = RunPlacement(WrongFor);

        foreach (var skill in PlacementItemBank.CefrSkills)
        {
            var level = LevelOf(outcome, skill)!.Value;
            Assert.True(level.Rank() <= CefrLevel.A2.Rank(),
                $"{skill} placed at {level} after an all-wrong run");
        }
    }

    [Fact]
    public void Strong_reading_and_weak_listening_produce_different_levels()
    {
        var outcome = RunPlacement(item => item.Skill == SkillType.Listening
            ? WrongFor(item)
            : CorrectFor(item));

        var reading = LevelOf(outcome, SkillType.Reading)!.Value;
        var listening = LevelOf(outcome, SkillType.Listening)!.Value;

        // Skills are measured independently, never averaged.
        Assert.True(reading.Rank() > listening.Rank());
    }

    [Fact]
    public void The_test_asks_between_12_and_22_questions()
    {
        var random = new Random(3);
        var responses = new List<PlacementResponse>();

        while (true)
        {
            var item = Engine.NextItem(responses, random);
            if (item is null) break;
            Assert.True(responses.Count < 200, "the queue must terminate");

            responses.Add(new PlacementResponse(
                item.Id, item.Skill,
                Config.Scale.DifficultyOf(item.Level),
                Engine.ScoreAnswer(item, CorrectFor(item))));
        }

        // The documented bound (docs/06-PLACEMENT-ALGORITHM.md §4).
        Assert.InRange(responses.Count, 12, 22);
    }

    [Fact]
    public void No_question_is_ever_asked_twice()
    {
        var random = new Random(11);
        var responses = new List<PlacementResponse>();
        var seen = new HashSet<string>();

        while (true)
        {
            var item = Engine.NextItem(responses, random);
            if (item is null) break;

            Assert.True(seen.Add(item.Id), $"{item.Id} was repeated");
            responses.Add(new PlacementResponse(
                item.Id, item.Skill,
                Config.Scale.DifficultyOf(item.Level),
                Engine.ScoreAnswer(item, CorrectFor(item))));
        }
    }

    [Fact]
    public void Every_CEFR_skill_contributes_at_least_its_minimum_items()
    {
        var random = new Random(5);
        var responses = new List<PlacementResponse>();

        while (true)
        {
            var item = Engine.NextItem(responses, random);
            if (item is null) break;
            responses.Add(new PlacementResponse(
                item.Id, item.Skill,
                Config.Scale.DifficultyOf(item.Level),
                Engine.ScoreAnswer(item, CorrectFor(item))));
        }

        foreach (var skill in Config.SkillOrder)
        {
            var asked = responses.Count(r => r.Skill == skill);
            Assert.True(asked >= Config.LimitsFor(skill).MinItems,
                $"{skill} was asked {asked} times");
        }
    }

    // ── Spelling ─────────────────────────────────────────────────────────────

    [Fact]
    public void Spelling_is_measured_but_never_assigned_a_CEFR_level()
    {
        var outcome = RunPlacement(CorrectFor);
        var spelling = outcome.Levels.Single(l => l.Skill == SkillType.Spelling);

        Assert.Null(spelling.Level);
        Assert.True(outcome.SpellingItemsAnswered > 0);
    }

    [Fact]
    public void A_strong_speller_starts_on_free_typing_and_a_weak_one_on_tiles()
    {
        var strong = RunPlacement(CorrectFor);
        var weak = RunPlacement(WrongFor);

        Assert.Equal(SpellingInputMode.FreeTyping, strong.SpellingSupportMode);
        Assert.Equal(SpellingInputMode.LetterTiles, weak.SpellingSupportMode);
        Assert.Equal(strong.SpellingItemsAnswered, strong.SpellingCorrect);
        Assert.Equal(0, weak.SpellingCorrect);
    }

    // ── Uncertainty ──────────────────────────────────────────────────────────

    [Fact]
    public void An_erratic_learner_is_placed_with_low_confidence_and_flagged()
    {
        // Alternating right and wrong is exactly the pattern that leaves the
        // posterior wide.
        var flip = false;
        var outcome = RunPlacement(item =>
        {
            flip = !flip;
            return flip ? CorrectFor(item) : WrongFor(item);
        });

        Assert.True(outcome.HasLowConfidence,
            "inconsistent answers must not yield a confident level");
        Assert.All(outcome.Levels.Where(l => l.Level is not null),
            l => Assert.InRange(l.Confidence, 0.0, 1.0));
    }

    [Fact]
    public void A_learner_inside_the_banks_range_is_placed_more_confidently_than_one_who_tops_it_out()
    {
        // Under a Rasch model the posterior width is driven by how informative
        // the items were — how close their difficulty sat to the learner's
        // ability — not by whether the answers were consistent. So the
        // meaningful contrast is a mid-band learner against a perfect scorer,
        // whose ability runs off the top of the bank. A wide posterior in the
        // second case is honest: the test only established "at least C1".
        var midBand = RunPlacement(item =>
            item.Level.Rank() <= CefrLevel.B1.Rank()
                ? CorrectFor(item)
                : WrongFor(item));
        var topsOut = RunPlacement(CorrectFor);

        static double MeanConfidence(PlacementOutcome o) =>
            o.Levels.Where(l => l.Level is not null).Average(l => l.Confidence);

        Assert.True(MeanConfidence(midBand) > MeanConfidence(topsOut));
    }

    [Fact]
    public void A_level_is_always_assigned_even_when_confidence_is_low()
    {
        // We never refuse to place a learner — a level is a starting point, and
        // the level engine corrects it from real sessions (§8).
        var flip = false;
        var outcome = RunPlacement(item =>
        {
            flip = !flip;
            return flip ? CorrectFor(item) : WrongFor(item);
        });

        foreach (var skill in PlacementItemBank.CefrSkills)
        {
            Assert.NotNull(LevelOf(outcome, skill));
        }
    }

    // ── Scoring ──────────────────────────────────────────────────────────────

    [Fact]
    public void An_empty_free_text_answer_scores_zero_rather_than_crashing()
    {
        var outcome = RunPlacement(item => item.IsFreeText ? "" : WrongFor(item));

        Assert.NotNull(LevelOf(outcome, SkillType.Writing));
        Assert.NotNull(LevelOf(outcome, SkillType.Speaking));
    }

    [Fact]
    public void A_one_word_answer_scores_far_below_a_competent_one()
    {
        var item = PlacementItemBank.All.First(i => i.IsFreeText);
        var scorer = new HeuristicFreeResponseScorer();

        var terse = scorer.Score(item, "no");
        var competent = scorer.Score(item, CompetentAnswerFor(item));

        // A single word trivially has 100% lexical variety, which is why
        // variety discounts length rather than adding to it.
        Assert.True(terse < 0.3, $"a one-word answer scored {terse:F2}");
        Assert.True(competent > 0.7);
    }

    [Fact]
    public void The_item_bank_matches_the_specification()
    {
        // Generated from the Dart spec, so drift shows up here.
        Assert.Equal(35, PlacementItemBank.All.Count);
        Assert.Equal(10, PlacementItemBank.ForSkill(SkillType.Reading).Count);
        Assert.Equal(6, PlacementItemBank.ForSkill(SkillType.Spelling).Count);

        // Listening items are spoken, never shown.
        Assert.All(PlacementItemBank.ForSkill(SkillType.Listening),
            i => Assert.False(string.IsNullOrWhiteSpace(i.AudioText)));

        // Productive skills are free text.
        Assert.All(PlacementItemBank.ForSkill(SkillType.Writing),
            i => Assert.True(i.IsFreeText));
        Assert.All(PlacementItemBank.ForSkill(SkillType.Speaking),
            i => Assert.True(i.IsFreeText));

        // Every multiple-choice item has a correct answer among its options.
        foreach (var item in PlacementItemBank.All.Where(i => !i.IsFreeText))
        {
            Assert.NotNull(item.CorrectAnswer);
            Assert.Contains(item.CorrectAnswer!, item.Options);
        }
    }

    /// <summary>
    /// A response long and varied enough that the offline scorer treats it as
    /// competent for the item's band.
    /// </summary>
    private static string CompetentAnswerFor(BankItem item)
    {
        const string sentence =
            "I usually plan my week carefully because it helps me focus, and when " +
            "something unexpected happens I adjust the plan instead of abandoning it " +
            "entirely, which keeps my progress steady over time and reduces stress.";

        var words = sentence.Split(' ');
        var needed = item.ExpectedWords == 0 ? 8 : item.ExpectedWords;
        return string.Join(' ', words.Take(Math.Clamp(needed, 1, words.Length)));
    }
}
