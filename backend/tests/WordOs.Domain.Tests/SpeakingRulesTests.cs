using WordOs.Domain.Sessions;

namespace WordOs.Domain.Tests;

/// <summary>
/// What passes Speaking, and what does not.
/// </summary>
/// <remarks>
/// These run without a model, which is the point of keeping the rule in C#
/// (ADR-015, ADR-019): a prompt edit cannot change any of these outcomes, and a
/// change to the rule has to be made here, deliberately, in front of a test.
/// </remarks>
public class SpeakingRulesTests
{
    private static SpokenWordObservation Observation(
        bool used = true,
        bool meaningCorrect = true,
        bool understandable = true,
        bool grammarAcceptable = true,
        bool majorGrammarProblem = false) =>
        new(used, meaningCorrect, understandable, grammarAcceptable,
            majorGrammarProblem);

    [Fact]
    public void A_word_used_correctly_passes()
    {
        Assert.True(SpeakingRules.Passed(Observation()));
    }

    [Fact]
    public void A_minor_grammar_mistake_does_not_fail_correct_use()
    {
        // "I research about AI yesterday." — the tense is wrong, the meaning is
        // right. `MVP Core.txt` §32 is explicit that this must not fail.
        var slip = Observation(grammarAcceptable: false, majorGrammarProblem: false);

        Assert.True(SpeakingRules.Passed(slip),
            "a small grammar slip must never fail a correctly used word");
    }

    [Fact]
    public void The_wrong_meaning_fails_even_though_the_word_was_said()
    {
        // "The database is my phone." — the word is there, the meaning is not.
        var wrongSense = Observation(meaningCorrect: false);

        Assert.False(SpeakingRules.Passed(wrongSense));
    }

    [Fact]
    public void A_word_that_was_never_said_fails()
    {
        Assert.False(SpeakingRules.Passed(Observation(used: false)));
    }

    [Fact]
    public void Grammar_broken_enough_to_obscure_the_meaning_fails()
    {
        // The distinction that matters: "major" means nobody can tell what was
        // meant, at which point the word's use cannot be judged correct.
        var broken = Observation(
            grammarAcceptable: false, majorGrammarProblem: true);

        Assert.False(SpeakingRules.Passed(broken));
    }

    [Fact]
    public void An_incomprehensible_answer_fails()
    {
        Assert.False(SpeakingRules.Passed(Observation(understandable: false)));
    }

    [Fact]
    public void Pronunciation_is_not_part_of_the_rule()
    {
        // There is no pronunciation input to give it — deliberately. The
        // transcript comes from speech recognition, so a "mispronunciation"
        // cannot be told apart from a recogniser error.
        var fields = typeof(SpokenWordObservation)
            .GetProperties()
            .Select(p => p.Name.ToLowerInvariant());

        Assert.DoesNotContain("pronunciation", fields);
        Assert.DoesNotContain("accent", fields);
    }
}
