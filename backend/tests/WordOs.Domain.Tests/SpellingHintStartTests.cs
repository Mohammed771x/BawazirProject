using WordOs.Domain.Common;
using WordOs.Domain.Users;

namespace WordOs.Domain.Tests;

/// <summary>Where a learner's Spelling hints start (ADR-115).</summary>
public class SpellingHintStartTests
{
    private static User NewUser() => User.Register(
        "hints@test.dev", "hash", "Learner", new WordOsConfiguration(),
        DateTimeOffset.UnixEpoch, phoneCountryCode: "967", phoneNumber: "771234567");

    [Fact]
    public void A_new_learner_starts_where_their_level_puts_them()
    {
        Assert.Null(NewUser().SpellingHintStart);
    }

    [Theory]
    [InlineData(SpellingClueKind.DefinitionEn)]
    [InlineData(SpellingClueKind.SimplifiedDefinition)]
    [InlineData(SpellingClueKind.Synonym)]
    [InlineData(SpellingClueKind.ArabicMeaning)]
    public void Any_rung_above_the_last_can_be_the_start(SpellingClueKind start)
    {
        var user = NewUser();
        user.ChooseSpellingHintStart(start);
        Assert.Equal(start, user.SpellingHintStart);

        user.ChooseSpellingHintStart(null);
        Assert.Null(user.SpellingHintStart);
    }

    [Fact]
    public void The_letter_count_is_the_last_hint_not_a_start()
    {
        var user = NewUser();
        Assert.Throws<ArgumentException>(
            () => user.ChooseSpellingHintStart(SpellingClueKind.LetterCount));
        Assert.Null(user.SpellingHintStart);
    }

    [Fact]
    public void Spelling_follows_the_reading_level_when_it_has_none_of_its_own()
    {
        var user = NewUser();
        Assert.Equal(CefrLevel.B1, user.SpellingContentLevel());
    }
}
