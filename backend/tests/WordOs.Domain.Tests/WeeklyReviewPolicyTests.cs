using WordOs.Domain.Common;
using WordOs.Domain.Review;
using WordOs.Domain.Words;

namespace WordOs.Domain.Tests;

/// <summary>
/// Which words the weekly challenge may ask about, and how many at once
/// (ADR-089).
/// </summary>
public class WeeklyReviewPolicyTests
{
    private static readonly WordOsConfiguration Config = new();
    private static readonly DateTimeOffset T0 =
        new(2026, 9, 17, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid UserId = Guid.CreateVersion7();

    private static Word WordAddedAt(DateTimeOffset at, string text = "research") =>
        Word.Add(
            userId: UserId,
            senseId: $"sense-{text}-{at.Ticks}",
            text: text,
            meaning: "بحث علمي",
            definitionEn: "careful study to discover new facts",
            partOfSpeech: "noun",
            cefrLevel: CefrLevel.B1,
            config: Config,
            now: at);

    private static List<Word> WordsAddedAt(int count, DateTimeOffset at) =>
        Enumerable.Range(0, count)
            .Select(i => WordAddedAt(at.AddSeconds(i), $"word{i}"))
            .ToList();

    // ── Ripening ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_word_added_today_is_not_reviewable_today()
    {
        // The gap is the feature. A word tested the evening it was added is not
        // being reviewed — the learner still has it in mind, answers correctly,
        // and the score says nothing about retention, which is the only thing
        // rule R9 lets this measure.
        var words = new[] { WordAddedAt(T0) };

        Assert.Empty(WeeklyReviewPolicy.NextSitting(words, T0, Config));
        Assert.Empty(WeeklyReviewPolicy.NextSitting(
            words, T0.AddDays(Config.WeeklyReviewMaturityDays - 1), Config));
    }

    [Fact]
    public void It_becomes_reviewable_the_moment_the_week_is_up()
    {
        var words = new[] { WordAddedAt(T0) };
        var ripe = T0.AddDays(Config.WeeklyReviewMaturityDays);

        Assert.Single(WeeklyReviewPolicy.NextSitting(words, ripe, Config));
        Assert.Equal(ripe, WeeklyReviewPolicy.RipensAt(T0, Config));
    }

    [Fact]
    public void A_word_recalled_correctly_leaves_the_pool_for_good()
    {
        var word = WordAddedAt(T0);
        var at = T0.AddDays(Config.WeeklyReviewMaturityDays);

        Assert.Single(WeeklyReviewPolicy.NextSitting([word], at, Config));

        word.MarkReviewed(at, passed: true);

        Assert.Empty(WeeklyReviewPolicy.NextSitting([word], at, Config));
        Assert.Empty(WeeklyReviewPolicy.NextSitting(
            [word], at.AddDays(90), Config));
    }

    [Fact]
    public void A_word_the_learner_missed_comes_back_a_week_later()
    {
        var word = WordAddedAt(T0);
        var at = T0.AddDays(Config.WeeklyReviewMaturityDays);

        word.MarkReviewed(at, passed: false);

        // Not tomorrow: it gets its own week to be forgotten in, or the
        // challenge becomes the same lesson again (ADR-099).
        Assert.Empty(WeeklyReviewPolicy.NextSitting([word], at, Config));
        Assert.Empty(WeeklyReviewPolicy.NextSitting(
            [word], at.AddDays(1), Config));

        var again = at.AddDays(Config.WeeklyReviewMaturityDays);
        Assert.Single(WeeklyReviewPolicy.NextSitting([word], again, Config));
    }

    [Fact]
    public void Getting_it_right_the_second_time_is_not_recalling_it()
    {
        // The weekly score counts first attempts only, and so does this: a
        // word rescued on the retry was not remembered (R9, ADR-099).
        var word = WordAddedAt(T0);
        var at = T0.AddDays(Config.WeeklyReviewMaturityDays);

        word.MarkReviewed(at, passed: false);
        word.MarkReviewed(at, passed: false);

        Assert.Single(WeeklyReviewPolicy.NextSitting(
            [word], at.AddDays(Config.WeeklyReviewMaturityDays), Config));
    }

    [Fact]
    public void The_date_the_challenge_reopens_counts_from_the_last_one()
    {
        var word = WordAddedAt(T0);
        var at = T0.AddDays(Config.WeeklyReviewMaturityDays);

        word.MarkReviewed(at, passed: false);

        Assert.Equal(
            at.AddDays(Config.WeeklyReviewMaturityDays),
            WeeklyReviewPolicy.OpensAt([word], Config));
    }

    [Fact]
    public void Nothing_reopens_once_every_word_has_been_recalled()
    {
        var word = WordAddedAt(T0);
        word.MarkReviewed(T0.AddDays(Config.WeeklyReviewMaturityDays), true);

        Assert.Null(WeeklyReviewPolicy.OpensAt([word], Config));
    }

    [Fact]
    public void A_deleted_word_is_never_asked_about()
    {
        var word = WordAddedAt(T0);
        word.Delete(T0);

        Assert.Empty(WeeklyReviewPolicy.NextSitting(
            [word], T0.AddDays(30), Config));
    }

    // ── Carrying over ────────────────────────────────────────────────────────

    [Fact]
    public void Words_from_a_skipped_week_wait_rather_than_expiring()
    {
        // Carrying over is the point: the words worth asking about are exactly
        // the ones belonging to the week somebody was too busy to review.
        var lastMonth = WordsAddedAt(3, T0.AddDays(-30));
        var lastWeek = WordsAddedAt(2, T0.AddDays(-8));

        var sitting = WeeklyReviewPolicy.NextSitting(
            lastMonth.Concat(lastWeek), T0, Config);

        Assert.Equal(5, sitting.Count);
    }

    [Fact]
    public void The_oldest_words_are_asked_first()
    {
        // A word that has been waiting a fortnight is the one most likely to
        // have been forgotten, and it is the one the cap must not keep pushing
        // to the back of the queue week after week.
        var newest = WordAddedAt(T0.AddDays(-8), "newest");
        var oldest = WordAddedAt(T0.AddDays(-40), "oldest");

        var sitting = WeeklyReviewPolicy.NextSitting(
            new[] { newest, oldest }, T0, Config);

        Assert.Equal("oldest", sitting[0].Text);
    }

    // ── The ceiling ──────────────────────────────────────────────────────────

    [Fact]
    public void One_sitting_never_asks_more_than_the_configured_maximum()
    {
        // Without a ceiling, carrying over becomes a punishment for missing a
        // fortnight: a hundred and thirty questions is not a challenge, it is a
        // reason to close the app.
        var words = WordsAddedAt(63, T0.AddDays(-20));

        var sitting = WeeklyReviewPolicy.NextSitting(words, T0, Config);

        Assert.Equal(Config.WeeklyReviewMaxWords, sitting.Count);
        Assert.Equal(50, sitting.Count);
    }

    [Fact]
    public void What_is_behind_the_ceiling_is_still_ripe()
    {
        // Not lost, and not silently dropped — the rest are offered again, and
        // the reminder the next day says so (ADR-090).
        var words = WordsAddedAt(63, T0.AddDays(-20));

        Assert.Equal(63, WeeklyReviewPolicy.Ripe(words, T0, Config).Count);
        Assert.Equal(
            13,
            WeeklyReviewPolicy.Ripe(words, T0, Config).Count
            - WeeklyReviewPolicy.NextSitting(words, T0, Config).Count);
    }

    [Fact]
    public void The_ceiling_is_configuration_and_not_a_constant()
    {
        // Rule R3. The right ceiling is a judgement about people, not about
        // software, and the product owner must be able to move it.
        var tighter = Config with { WeeklyReviewMaxWords = 10 };
        var words = WordsAddedAt(63, T0.AddDays(-20));

        Assert.Equal(
            10, WeeklyReviewPolicy.NextSitting(words, T0, tighter).Count);
    }

    // ── Naming the date ──────────────────────────────────────────────────────

    [Fact]
    public void A_learner_in_their_first_week_has_a_date_to_be_given()
    {
        var words = new[] { WordAddedAt(T0.AddDays(-2)) };

        Assert.Equal(
            T0.AddDays(-2).AddDays(Config.WeeklyReviewMaturityDays),
            WeeklyReviewPolicy.OpensAt(words, Config));
    }

    [Fact]
    public void The_date_is_the_earliest_one_not_the_latest()
    {
        var words = new[]
        {
            WordAddedAt(T0.AddDays(-1), "newer"),
            WordAddedAt(T0.AddDays(-5), "older"),
        };

        Assert.Equal(
            T0.AddDays(-5).AddDays(Config.WeeklyReviewMaturityDays),
            WeeklyReviewPolicy.OpensAt(words, Config));
    }

    [Fact]
    public void There_is_no_date_when_there_is_nothing_coming()
    {
        // "Your challenge opens on the 24th" said to somebody who has added
        // nothing is a promise about a thing that will not happen.
        Assert.Null(WeeklyReviewPolicy.OpensAt([], Config));
    }
}
