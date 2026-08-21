using WordOs.Application.Words;
using WordOs.Domain.Common;
using WordOs.Domain.Words;

namespace WordOs.Api.Tests;

/// <summary>
/// What the generator is told about the shape of a word (ADR-047).
/// </summary>
public class WordFormTests
{
    private static Word Add(string text, string pos, string senseId) =>
        Word.Add(
            userId: Guid.CreateVersion7(),
            senseId: senseId,
            text: text,
            meaning: "معنى",
            definitionEn: "a definition",
            partOfSpeech: pos,
            cefrLevel: CefrLevel.B1,
            config: new WordOsConfiguration(),
            now: DateTimeOffset.UtcNow);

    [Theory]
    [InlineData("play%2:29:00::#pst", "past tense")]
    [InlineData("play%2:29:00::#pp", "past participle")]
    [InlineData("play%2:29:00::#ing", "-ing form")]
    [InlineData("mouse%1:05:00::#pl", "plural")]
    // A lemma has no form marker, and asks for none.
    [InlineData("play%2:29:00::", null)]
    public void The_form_is_read_back_from_the_sense_it_came_from(
        string senseId, string? expected)
    {
        Assert.Equal(expected, WordForms.FormOf(Add("played", "v", senseId)));
    }

    [Fact]
    public void A_noun_with_a_regular_plural_may_be_pluralised()
    {
        // `book` and `books` are one word to a learner, and meeting both is
        // worth more than meeting one.
        var book = Add("book", "n", "book%1:06:00::");

        Assert.True(WordForms.MayPluralise(book, hasIrregularPlural: false));
    }

    [Fact]
    public void A_noun_whose_plural_is_another_word_may_not()
    {
        // `mice` is a vocabulary item of its own (ADR-045), and a learner who
        // added `mouse` has not been taught it.
        var mouse = Add("mouse", "n", "mouse%1:05:00::");

        Assert.False(WordForms.MayPluralise(mouse, hasIrregularPlural: true));
    }

    [Theory]
    // A verb is not pluralised, and neither is a form: `played` is the
    // participle the learner asked for, and `mice` is already the plural.
    [InlineData("run", "v", "run%2:38:00::")]
    [InlineData("played", "v", "play%2:29:00::#pp")]
    [InlineData("mice", "n", "mouse%1:05:00::#pl")]
    public void Nothing_else_is_pluralised(string text, string pos, string senseId)
    {
        Assert.False(
            WordForms.MayPluralise(Add(text, pos, senseId), hasIrregularPlural: false));
    }

    // ── What the learner is told the word is (ADR-056) ───────────────────────

    [Theory]
    [InlineData("go%2:38:01::#pst", "past")]
    [InlineData("play%2:29:00::#pp", "pastParticiple")]
    [InlineData("run%2:38:00::#ing", "ing")]
    [InlineData("mouse%1:05:00::#pl", "plural")]
    // The plain word needs no label saying it is itself.
    [InlineData("book%1:06:00::", null)]
    [InlineData("go%2:38:01::", null)]
    public void The_form_key_names_the_inflection_and_nothing_else(
        string senseId, string? expected)
    {
        var word = Add("whatever", "v", senseId);

        // A key rather than a sentence: the learner reads it in their own
        // language, and the server does not know which that is (ADR-035).
        Assert.Equal(expected, WordForms.FormKey(word));
    }

    [Fact]
    public void An_unknown_suffix_is_no_label_rather_than_a_wrong_one()
    {
        // A sense id from a future importer, or a corrupt row. Showing the
        // learner "#xyz" would be worse than showing them nothing.
        var word = Add("thing", "n", "thing%1:06:00::#xyz");

        Assert.Null(WordForms.FormKey(word));
    }
}
