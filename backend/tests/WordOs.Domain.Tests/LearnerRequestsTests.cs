using WordOs.Domain.Sessions;

namespace WordOs.Domain.Tests;

/// <summary>
/// A request is not an answer (ADR-113): which sentences of a Speaking turn
/// count as the learner using a word.
/// </summary>
/// <remarks>
/// The first three request cases are the product owner's own examples, word
/// for word. The answers below them are the other half of the rule, and
/// matter as much: a pattern that swallowed real answers would stop a
/// learner's words from ever counting.
/// </remarks>
public class LearnerRequestsTests
{
    private static readonly string[] Football = ["football"];

    [Theory]
    // The product owner's examples.
    [InlineData("Can you give me another question using football?")]
    [InlineData("Can you explain what football means?")]
    [InlineData("How can I use football in a sentence?")]
    // The same requests, as learners actually say them.
    [InlineData("give me another question with football")]
    [InlineData("Please give me a new question about football.")]
    [InlineData("Ask me an easier question with football")]
    [InlineData("What does football mean?")]
    [InlineData("what's the meaning of football")]
    [InlineData("Explain football.")]
    [InlineData("Can you explain the word football please?")]
    [InlineData("What is football?")]
    [InlineData("How do I say football?")]
    [InlineData("How to use football")]
    [InlineData("Give me an example with football.")]
    [InlineData("Let me use football in a sentence.")]
    [InlineData("I don't understand the word football.")]
    [InlineData("I don't know what football means.")]
    [InlineData("Can you change the question about football?")]
    [InlineData("Can you repeat the question about football?")]
    [InlineData("Could you explain football to me again?")]
    public void A_request_about_the_word_is_not_an_answer(string turn)
    {
        var answer = LearnerRequests.AnswerPart(turn, Football);

        Assert.DoesNotContain("football", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("I play football with my friends every Friday.")]
    [InlineData("My brother loves football more than anything.")]
    [InlineData("Do you like football?")]
    [InlineData("What is your favourite football team?")]
    [InlineData("I think football is the best sport in the world")]
    [InlineData("Yes, I watched football yesterday. It was great.")]
    [InlineData("Can you believe I scored in football today?")]
    [InlineData("I can explain why football is popular: everyone can play it.")]
    [InlineData("We usually play football after school, for example on Mondays.")]
    // A request, then a sentence of their own: the second one counts.
    [InlineData("Can you change the question? Football is hard.")]
    [InlineData("Football.")]
    public void A_sentence_that_uses_the_word_is_an_answer(string turn)
    {
        var answer = LearnerRequests.AnswerPart(turn, Football);

        Assert.Contains("football", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_turn_that_answers_and_then_asks_keeps_its_answer()
    {
        var answer = LearnerRequests.AnswerPart(
            "I play football every weekend. Can you give me another question?",
            Football);

        Assert.Equal("I play football every weekend.", answer);
    }

    [Fact]
    public void A_turn_that_asks_and_then_answers_keeps_its_answer()
    {
        var answer = LearnerRequests.AnswerPart(
            "What does football mean? Oh, I know — I play football at school.",
            Football);

        Assert.Contains("I play football at school.", answer);
        Assert.DoesNotContain("mean", answer);
    }

    [Fact]
    public void Curly_apostrophes_are_read_like_straight_ones()
    {
        Assert.True(LearnerRequests.IsRequest(
            "What’s the meaning of football?", Football));
        Assert.True(LearnerRequests.IsRequest(
            "I don’t understand the question", Football));
    }

    [Fact]
    public void A_phrase_is_matched_as_a_whole()
    {
        string[] phrase = ["look after"];

        Assert.True(LearnerRequests.IsRequest("What is look after?", phrase));
        Assert.False(LearnerRequests.IsRequest(
            "I look after my little sister after school.", phrase));
    }

    [Fact]
    public void A_transcript_without_punctuation_is_one_sentence()
    {
        Assert.Single(LearnerRequests.Sentences("i play football on friday"));
        Assert.Equal(3, LearnerRequests.Sentences("One. Two? Three!").Count);
    }

    [Fact]
    public void A_long_rambling_turn_is_judged_without_failing()
    {
        // Production, 2026-09-30: a regex timeout here failed every Speaking
        // turn with a 500. Whatever the input, judging it must never throw.
        var ramble = string.Concat(Enumerable.Repeat(
            "so what is it that I really want to say about football and my day ", 400));

        var answer = LearnerRequests.AnswerPart(ramble + "I play football.", Football);

        Assert.Contains("I play football.", answer);
    }

    [Fact]
    public void An_empty_turn_has_no_answer()
    {
        Assert.Equal(string.Empty, LearnerRequests.AnswerPart("", Football));
        Assert.Equal(string.Empty, LearnerRequests.AnswerPart("   ", Football));
    }
}
