using System.Globalization;
using WordOs.Domain.Analytics;
using WordOs.Domain.Common;
using WordOs.Domain.Levels;
using WordOs.Domain.Review;
using WordOs.Domain.Sessions;
using WordOs.Domain.Users;
using WordOs.Domain.Words;

namespace WordOs.Api.Admin;

/// <summary>
/// The global filters (admin brief §29), as the query string carries them.
/// </summary>
/// <param name="From">First reporting day, inclusive (<c>yyyy-MM-dd</c>).</param>
/// <param name="To">Last reporting day, inclusive.</param>
/// <param name="Days">Shorthand for "the last N days, today included".</param>
/// <param name="Level">A CEFR band — <c>B1</c> matches B1 and B1+.</param>
/// <param name="Status"><c>active</c> or <c>inactive</c>, by <see cref="AdminIntelOptions.InactiveDays"/>.</param>
/// <param name="Segment">A cohort key; see <see cref="Segments"/>.</param>
public sealed record IntelFilter(
    string? From = null,
    string? To = null,
    int? Days = null,
    string? Level = null,
    string? Skill = null,
    string? Interest = null,
    string? Status = null,
    string? Segment = null,
    Guid? UserId = null)
{
    public SkillType? SkillType =>
        Enum.TryParse<SkillType>(Skill, ignoreCase: true, out var s) && Enum.IsDefined(s) ? s : null;

    public IntelPeriod PeriodFor(IntelDataset data)
    {
        var today = data.Day(data.Now);

        var to = DateOnly.TryParseExact(To, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var t) ? t : today;
        if (to > today) to = today;

        DateOnly from;
        if (DateOnly.TryParseExact(From, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var f))
            from = f;
        else
            from = to.AddDays(-(Math.Clamp(Days ?? 30, 1, 3650) - 1));

        if (from > to) (from, to) = (to, from);

        // Ten years at most: a typed-in date must not become an overflow.
        if (to.DayNumber - from.DayNumber > 3650) from = to.AddDays(-3650);

        return new IntelPeriod(data.StartOf(from), data.StartOf(to.AddDays(1)));
    }
}

/// <summary>
/// The dataset, narrowed to the learners and period a page is asking about.
/// </summary>
public sealed class IntelScope
{
    public IntelDataset Data { get; }
    public IntelFilter Filter { get; }
    public IntelPeriod Period { get; }
    public SkillType? Skill { get; }

    public IReadOnlyList<User> Learners { get; }
    public IReadOnlySet<Guid> Ids { get; }

    public IReadOnlyList<Word> Words { get; }
    public IReadOnlyList<SkillSession> Sessions { get; }
    public IReadOnlyList<WeeklyReview> Reviews { get; }
    public IReadOnlyList<LevelChangeRecord> LevelChanges { get; }
    public IReadOnlyList<AnalyticsEvent> Events { get; }
    public IReadOnlyList<FeedbackMessage> Feedback { get; }

    public IntelScope(IntelDataset data, IntelFilter filter)
    {
        Data = data;
        Filter = filter;
        Period = filter.PeriodFor(data);
        Skill = filter.SkillType;

        var segments = new Segments(data);

        Learners = data.Learners.Where(u =>
                (filter.UserId is null || u.Id == filter.UserId)
                && MatchesLevel(u, filter.Level)
                && MatchesInterest(u, filter.Interest)
                && MatchesStatus(u, filter.Status)
                && (string.IsNullOrWhiteSpace(filter.Segment) || segments.Matches(u, filter.Segment)))
            .ToList();

        Ids = Learners.Select(u => u.Id).ToHashSet();

        Words = data.Words.Where(w => Ids.Contains(w.UserId)).ToList();
        Sessions = data.Sessions.Where(s => Ids.Contains(s.UserId)).ToList();
        Reviews = data.Reviews.Where(r => Ids.Contains(r.UserId)).ToList();
        LevelChanges = data.LevelChanges.Where(l => Ids.Contains(l.UserId)).ToList();
        Events = data.Events.Where(e => Ids.Contains(e.UserId)).ToList();
        Feedback = data.Feedback.Where(f => Ids.Contains(f.UserId)).ToList();
    }

    /// <summary>Sessions started in the period, honouring the skill filter.</summary>
    public IEnumerable<SkillSession> SessionsIn(IntelPeriod? period = null, bool previous = false)
    {
        var p = period ?? Period;
        return Sessions.Where(s =>
            (previous ? p.PrevContains(s.StartedAt) : p.Contains(s.StartedAt))
            && (Skill is null || s.Skill == Skill));
    }

    public IEnumerable<AnalyticsEvent> EventsIn(string name, bool previous = false) =>
        Events.Where(e => e.Name == name
                          && (previous ? Period.PrevContains(e.OccurredAt) : Period.Contains(e.OccurredAt)));

    private bool MatchesLevel(User u, string? level)
    {
        if (string.IsNullOrWhiteSpace(level)) return true;
        var mine = IntelDataset.LevelOf(u);
        return mine is not null
               && mine.Value.ToWire().StartsWith(level.Trim().ToUpperInvariant(), StringComparison.Ordinal);
    }

    private static bool MatchesInterest(User u, string? interest) =>
        string.IsNullOrWhiteSpace(interest)
        || u.Interests.Any(i => string.Equals(i.Interest, interest.Trim(), StringComparison.OrdinalIgnoreCase));

    private bool MatchesStatus(User u, string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return true;
        var last = Data.LastActiveAt(u.Id);
        var active = last is not null && Data.Now - last.Value < TimeSpan.FromDays(Data.Options.InactiveDays);
        return status.Equals("active", StringComparison.OrdinalIgnoreCase) ? active : !active;
    }
}

/// <summary>
/// Named cohorts (admin brief §23), each a rule over the whole history.
/// </summary>
/// <remarks>
/// Keys are <c>name</c> or <c>name:argument</c>, so a link can carry one:
/// <c>joined:30</c>, <c>inactive:3</c>, <c>active-days:10</c>,
/// <c>weak:speaking</c>, <c>hoarders</c>, <c>heavy</c>, <c>light</c>,
/// <c>frequent</c>, <c>rare</c>, <c>level:B1</c>, <c>interest:sports</c>,
/// <c>funnel:first_word</c> (reached that stage and stopped), and
/// <c>attention:recently_inactive</c>.
/// </remarks>
public sealed class Segments(IntelDataset data)
{
    public static readonly IReadOnlyList<(string Key, string Label)> Catalogue =
    [
        ("all", "كل المستخدمين"),
        ("joined:7", "انضموا خلال 7 أيام"),
        ("joined:30", "انضموا خلال 30 يومًا"),
        ("inactive:3", "غير نشطين منذ 3 أيام"),
        ("inactive:7", "غير نشطين منذ 7 أيام"),
        ("active-days:10", "نشطون 10 أيام أو أكثر"),
        ("weak:speaking", "ضعف في Speaking"),
        ("weak:writing", "ضعف في Writing"),
        ("weak:listening", "ضعف في Listening"),
        ("weak:reading", "ضعف في Reading"),
        ("weak:spelling", "ضعف في Spelling"),
        ("hoarders", "أضافوا كثيرًا وأتقنوا قليلًا"),
        ("heavy", "Heavy users"),
        ("light", "Light users"),
        ("frequent", "Frequent — 4 أيام من آخر 7"),
        ("rare", "Rare — يوم أو أقل من آخر 14"),
    ];

    public bool Matches(User u, string key)
    {
        var (name, arg) = Split(key);
        var today = data.Day(data.Now);
        var days = data.ActiveDaysOf(u.Id);

        switch (name)
        {
            case "all":
                return true;
            case "joined":
                return data.Now - u.CreatedAt <= TimeSpan.FromDays(Int(arg, 30));
            case "inactive":
            {
                var last = data.LastActiveAt(u.Id);
                return last is null || data.Now - last.Value >= TimeSpan.FromDays(Int(arg, 3));
            }
            case "active-days":
                return days.Count >= Int(arg, 10);
            case "weak":
            {
                if (!Enum.TryParse<SkillType>(arg, ignoreCase: true, out var skill)) return false;
                var (first, decided) = FirstAttempt(u.Id, skill);
                return decided >= data.Options.MinimumSample
                       && (double)first / decided < data.Options.WeakAccuracy;
            }
            case "hoarders":
            {
                var words = data.WordsOf(u.Id).Where(w => w.State != WordState.Deleted).ToList();
                return words.Count >= 10
                       && words.Count(w => w.State is WordState.Active or WordState.Archived) <= words.Count / 10;
            }
            case "heavy":
                return days.Count(d => d > today.AddDays(-30)) >= 8;
            case "light":
                return days.Count(d => d > today.AddDays(-30)) is >= 1 and <= 2;
            case "frequent":
                return days.Count(d => d > today.AddDays(-7)) >= 4;
            case "rare":
                return days.Count > 0 && days.Count(d => d > today.AddDays(-14)) <= 1;
            case "level":
                return IntelDataset.LevelOf(u)?.ToWire()
                    .StartsWith((arg ?? "").ToUpperInvariant(), StringComparison.Ordinal) == true;
            case "interest":
                return u.Interests.Any(i => string.Equals(i.Interest, arg, StringComparison.OrdinalIgnoreCase));
            case "funnel":
                return Funnel.StageOf(data, u) == arg;
            case "funnel-reached":
                return Funnel.Reached(data, u, arg ?? "");
            case "attention":
                return Attention.Reasons(data, u).Any(r => r.Key == arg);
            default:
                return false;
        }
    }

    public static string LabelOf(string key) =>
        Catalogue.FirstOrDefault(c => c.Key == key).Label
        ?? key switch
        {
            _ when key.StartsWith("level:") => $"المستوى {key[6..]}",
            _ when key.StartsWith("interest:") => $"الاهتمام: {key[9..]}",
            _ when key.StartsWith("funnel:") => $"توقفوا عند: {Funnel.LabelOf(key[7..])}",
            _ when key.StartsWith("attention:") => Attention.LabelOf(key[10..]),
            _ => key,
        };

    /// <summary>Words passed first time at a skill, over words decided at it.</summary>
    public (int First, int Decided) FirstAttempt(Guid userId, SkillType skill)
    {
        var first = 0;
        var decided = 0;
        foreach (var w in data.WordsOf(userId))
        {
            var decisions = w.Events
                .Where(e => e.Skill == skill && e.Type is WordEventType.SkillPassed or WordEventType.SkillFailed)
                .OrderBy(e => e.CreatedAt)
                .ToList();
            if (decisions.Count == 0) continue;
            decided++;
            if (decisions[0].Type == WordEventType.SkillPassed) first++;
        }
        return (first, decided);
    }

    private static (string Name, string? Arg) Split(string key)
    {
        var i = key.IndexOf(':');
        return i < 0 ? (key.Trim().ToLowerInvariant(), null) : (key[..i].Trim().ToLowerInvariant(), key[(i + 1)..].Trim());
    }

    private static int Int(string? s, int fallback) =>
        int.TryParse(s, out var n) && n > 0 ? Math.Min(n, 3650) : fallback;
}
