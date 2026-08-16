using WordOs.Application.Words;

namespace WordOs.Domain.Tests;

/// <summary>
/// What counts as an Active word being met in generated content.
/// </summary>
/// <remarks>
/// This decides real exposure counts, which archiving reads (rule R8), so the
/// boundary between "the learner met this word" and "these letters happened to
/// appear" has to be exact in both directions.
/// </remarks>
public class ExposureDetectionTests
{
    [Theory]
    // The word itself, wherever it sits in the sentence.
    [InlineData("They began the research last spring.", "research", true)]
    [InlineData("Research shapes everything we know.", "research", true)]
    [InlineData("The result, research, and review followed.", "research", true)]
    // Inflections are the same word met in another form.
    [InlineData("She researched it carefully.", "research", true)]
    [InlineData("They are researching the coast.", "research", true)]
    [InlineData("He uses two theories.", "theory", false)]
    [InlineData("The theories disagree.", "theories", true)]
    [InlineData("She used it twice.", "use", true)]
    [InlineData("They are using it now.", "use", true)]
    // Derivations are different words, and must not count.
    [InlineData("The researcher arrived late.", "research", false)]
    [InlineData("It was theoretical at best.", "theory", false)]
    // Substrings must not count.
    [InlineData("The programme started at noon.", "art", false)]
    [InlineData("He was thoughtful about it.", "though", false)]
    // Case is irrelevant; the learner met the word either way.
    [InlineData("RESEARCH is expensive.", "research", true)]
    public void Detects_a_word_only_when_it_was_genuinely_used(
        string text, string word, bool expected) =>
        Assert.Equal(expected, ActiveWordReuseDetector.Contains(text, word));

    [Fact]
    public void A_multi_word_entry_survives_a_line_break()
    {
        const string text = "The operating\n  system manages memory.";
        Assert.True(ActiveWordReuseDetector.Contains(text, "operating system"));
    }

    [Fact]
    public void A_word_repeated_many_times_is_still_one_exposure()
    {
        const string text =
            "Research is slow. The research continued. More research followed.";

        var found = ActiveWordReuseDetector.Detect(
            text, new[] { "research", "theory" }, w => w);

        Assert.Equal(["research"], found);
    }

    [Fact]
    public void Empty_content_exposes_nothing()
    {
        Assert.Empty(ActiveWordReuseDetector.Detect(
            null, new[] { "research" }, w => w));
        Assert.Empty(ActiveWordReuseDetector.Detect(
            "   ", new[] { "research" }, w => w));
    }
}
