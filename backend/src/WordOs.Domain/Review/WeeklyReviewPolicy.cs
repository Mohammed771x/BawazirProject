using WordOs.Domain.Common;
using WordOs.Domain.Words;

namespace WordOs.Domain.Review;

/// <summary>
/// Which words the weekly challenge may ask about, and how many at once.
/// </summary>
/// <remarks>
/// The review used to ask about <i>everything added in the last seven days</i>,
/// which meant a word added this morning was tested this evening. That is not a
/// review — it is the same lesson again, and a learner who answers it correctly
/// has demonstrated nothing about retention, which is the only thing this
/// feature measures (rule R9).
///
/// <para>So a word <b>ripens</b>: it becomes reviewable once
/// <see cref="WordOsConfiguration.WeeklyReviewMaturityDays"/> have passed since
/// it was last put in front of the learner — added, or asked about in a
/// challenge. A learner's first week therefore has no challenge in it at all,
/// and the first one opens on a date that can be named in advance — which is
/// what the notification announces (ADR-089).</para>
///
/// <para>What takes a word <b>out</b> of the pool is recalling it correctly,
/// not merely being asked (ADR-099). Getting it wrong used to retire it just
/// as effectively as getting it right, so the one word a learner had just
/// proved they did not remember was the one word the challenge never mentioned
/// again. Now a missed word ripens again a week later and comes back; a word
/// recalled first time is finished with.</para>
///
/// <para>Unreviewed words <b>carry over</b> rather than expiring. A learner who
/// skipped last week's challenge finds last week's words waiting alongside this
/// week's, because the alternative is a feature that silently forgets the words
/// of anyone who was busy — and those are exactly the words worth asking
/// about.</para>
///
/// <para>Carrying over needs a ceiling or it becomes a punishment for missing a
/// week. <see cref="WordOsConfiguration.WeeklyReviewMaxWords"/> caps one sitting
/// at fifty; the rest stay ripe and are offered again the next day. Fifty is a
/// product decision and lives in configuration (rule R3).</para>
/// </remarks>
public static class WeeklyReviewPolicy
{
    /// <summary>The moment a word ripens — a week after it was last seen.</summary>
    /// <remarks>
    /// Anchored on the last review rather than on the date added, so a word
    /// that came back gets its own week to be forgotten in before it is asked
    /// again. For a word never reviewed the two are the same thing.
    /// </remarks>
    public static DateTimeOffset RipensAt(Word word, WordOsConfiguration config) =>
        RipensAt(word.LastReviewedAt ?? word.AddedAt, config);

    /// <summary>The moment a word last seen at <paramref name="seenAt"/> ripens.</summary>
    public static DateTimeOffset RipensAt(
        DateTimeOffset seenAt,
        WordOsConfiguration config) =>
        seenAt.AddDays(config.WeeklyReviewMaturityDays);

    /// <summary>
    /// Whether this word may be asked about now.
    /// </summary>
    /// <remarks>
    /// Deliberately not a question about the word's <i>pipeline</i> state. A
    /// word still on Reading counts exactly as much as one that matured: what
    /// is being measured is what the learner remembers, not how far the word
    /// travelled. Rule R9 in one predicate.
    /// </remarks>
    public static bool IsRipe(
        Word word,
        DateTimeOffset now,
        WordOsConfiguration config) =>
        word.State != WordState.Deleted &&
        word.ReviewPassedAt is null &&
        RipensAt(word, config) <= now;

    /// <summary>Every ripe word, oldest first — the backlog, uncapped.</summary>
    /// <remarks>
    /// Oldest first because a word that has been waiting a fortnight is the one
    /// most likely to have been forgotten, and it is the one the cap must not
    /// keep pushing to the back.
    /// </remarks>
    public static IReadOnlyList<Word> Ripe(
        IEnumerable<Word> words,
        DateTimeOffset now,
        WordOsConfiguration config) =>
        words
            .Where(w => IsRipe(w, now, config))
            .OrderBy(w => w.AddedAt)
            .ToList();

    /// <summary>What one sitting asks about: the backlog, capped.</summary>
    public static IReadOnlyList<Word> NextSitting(
        IEnumerable<Word> words,
        DateTimeOffset now,
        WordOsConfiguration config) =>
        Ripe(words, now, config).Take(config.WeeklyReviewMaxWords).ToList();

    /// <summary>
    /// When the next word ripens, for a learner with none ripe yet.
    /// </summary>
    /// <remarks>
    /// Null when they own no unreviewed words at all — there is no date to name,
    /// and "your challenge opens on the 24th" said to somebody who has added
    /// nothing is a promise about a thing that will not happen.
    /// </remarks>
    public static DateTimeOffset? OpensAt(
        IEnumerable<Word> words,
        WordOsConfiguration config)
    {
        var next = words
            .Where(w => w.State != WordState.Deleted && w.ReviewPassedAt is null)
            .Select(w => RipensAt(w, config))
            .OrderBy(at => at)
            .Cast<DateTimeOffset?>()
            .FirstOrDefault();

        return next;
    }
}
