using WordOs.Domain.Common;

namespace WordOs.Domain.Tests;

/// <summary>
/// How many due words one session takes (ADR-124): the learner's daily target,
/// and never more than ten up to A2+.
/// </summary>
public class SessionWordCapTests
{
    private static readonly WordOsConfiguration Config = new();

    [Theory]
    [InlineData(CefrLevel.A1)]
    [InlineData(CefrLevel.A1Plus)]
    [InlineData(CefrLevel.A2)]
    [InlineData(CefrLevel.A2Plus)]
    public void A_beginner_session_carries_at_most_ten_words(CefrLevel level)
    {
        Assert.Equal(10, Config.SessionWordCap(level, 15));
        // A smaller target is the learner's own choice and stands.
        Assert.Equal(6, Config.SessionWordCap(level, 6));
    }

    [Theory]
    [InlineData(CefrLevel.B1)]
    [InlineData(CefrLevel.B2Plus)]
    [InlineData(CefrLevel.C2)]
    public void From_B1_the_daily_target_runs_to_fifteen(CefrLevel level)
    {
        Assert.Equal(15, Config.SessionWordCap(level, 15));
    }
}
