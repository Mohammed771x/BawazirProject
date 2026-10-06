using Microsoft.EntityFrameworkCore;
using WordOs.Domain.Analytics;
using WordOs.Domain.Common;
using WordOs.Domain.Levels;
using WordOs.Domain.Review;
using WordOs.Domain.Sessions;
using WordOs.Domain.Users;
using WordOs.Domain.Words;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Admin;

/// <summary>
/// Every threshold the admin area judges by (rule R3).
/// </summary>
/// <remarks>
/// "Overdue", "abandoned", "inactive", "weak" — each is a number someone will
/// want to argue with, so none of them is a constant. Section <c>AdminIntel</c>.
/// </remarks>
public sealed class AdminIntelOptions
{
    public const string SectionName = "AdminIntel";

    /// <summary>A due skill untouched this long is overdue.</summary>
    public int OverdueDays { get; init; } = 3;

    /// <summary>An unfinished session older than this was abandoned, whether or not anyone said so.</summary>
    public int AbandonAfterHours { get; init; } = 24;

    /// <summary>No activity for this long reads as inactive.</summary>
    public int InactiveDays { get; init; } = 3;

    /// <summary>
    /// A session longer than this is a resumed one, not a long one, and is left
    /// out of durations — the median was 16 seconds and the mean 45 minutes
    /// before this existed (see the Overview's own remark).
    /// </summary>
    public int SessionCapMinutes { get; init; } = 60;

    /// <summary>First-attempt accuracy below this is "weak" at a skill.</summary>
    public double WeakAccuracy { get; init; } = 0.6;

    /// <summary>Fewest decisions before an accuracy is believed.</summary>
    public int MinimumSample { get; init; } = 3;

    /// <summary>An answer that took longer than this is a long idle.</summary>
    public int LongIdleSeconds { get; init; } = 120;

    /// <summary>Leaving a lesson screen sooner than this is an immediate back.</summary>
    public int ImmediateBackSeconds { get; init; } = 4;

    /// <summary>Replays of one clip that count as struggling to hear it.</summary>
    public int ReplayStruggle { get; init; } = 3;

    /// <summary>Most users any list in the admin area returns at once.</summary>
    public int MaxListedUsers { get; init; } = 200;
}

/// <summary>
/// Everything the admin area reads, loaded once per request (ADR-125).
/// </summary>
/// <remarks>
/// <b>In memory, on purpose, for now.</b> The product owner's instruction was
/// that the user base is small and the design should not be bent around a
/// scale it does not have. At forty learners the whole history is a few
/// thousand rows, and computing in C# keeps every metric's definition in one
/// readable place instead of spread over SQL.
///
/// The ceiling is known and the exit is cheap: when this load grows slow,
/// each metric becomes a query over the same tables — nothing about the
/// definitions or the API shape changes. Measure before moving.
/// </remarks>
public sealed class IntelDataset
{
    public required DateTimeOffset Now { get; init; }
    public required WordOsConfiguration Config { get; init; }
    public required AdminIntelOptions Options { get; init; }
    public required IReadOnlyList<User> Learners { get; init; }
    public required IReadOnlyList<Word> Words { get; init; }
    public required IReadOnlyList<SkillSession> Sessions { get; init; }
    public required IReadOnlyList<WeeklyReview> Reviews { get; init; }
    public required IReadOnlyList<LevelChangeRecord> LevelChanges { get; init; }
    public required IReadOnlyList<ActivityEvent> Activity { get; init; }
    public required IReadOnlyList<AnalyticsEvent> Events { get; init; }
    public required IReadOnlyList<FeedbackMessage> Feedback { get; init; }

    public static async Task<IntelDataset> LoadAsync(
        WordOsDbContext db,
        WordOsConfiguration config,
        AdminIntelOptions options,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Learners only (ADR-052): Owner and Analyst accounts exist to test and
        // to read, and every per-learner figure would otherwise count them.
        var learners = await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.User)
            .Include(u => u.Interests)
            .Include(u => u.SkillLevels)
            .AsSplitQuery()
            .ToListAsync(ct);

        var ids = learners.Select(u => u.Id).ToHashSet();

        // Deleted words included: the journey a learner gave up on is evidence
        // too, and where they gave up is part of it (ADR-071).
        var words = await db.Words.AsNoTracking().IgnoreQueryFilters()
            .Where(w => ids.Contains(w.UserId))
            .Include(w => w.Skills)
            .Include(w => w.Events)
            .AsSplitQuery()
            .ToListAsync(ct);

        var sessions = await db.SkillSessions.AsNoTracking()
            .Where(s => ids.Contains(s.UserId))
            .Include(s => s.Items)
            .AsSplitQuery()
            .ToListAsync(ct);

        var reviews = await db.WeeklyReviews.AsNoTracking()
            .Where(r => ids.Contains(r.UserId))
            .Include(r => r.Items)
            .AsSplitQuery()
            .ToListAsync(ct);

        var levelChanges = await db.LevelChanges.AsNoTracking()
            .Where(l => ids.Contains(l.UserId)).ToListAsync(ct);

        var activity = await db.ActivityEvents.AsNoTracking()
            .Where(a => ids.Contains(a.UserId)).ToListAsync(ct);

        var events = await db.AnalyticsEvents.AsNoTracking()
            .Where(e => ids.Contains(e.UserId)).ToListAsync(ct);

        var feedback = await db.FeedbackMessages.AsNoTracking()
            .Where(f => ids.Contains(f.UserId)).ToListAsync(ct);

        return new IntelDataset
        {
            Now = now,
            Config = config,
            Options = options,
            Learners = learners,
            Words = words,
            Sessions = sessions,
            Reviews = reviews,
            LevelChanges = levelChanges,
            Activity = activity,
            Events = events,
            Feedback = feedback,
        };
    }

    // ── Days ─────────────────────────────────────────────────────────────────

    /// <summary>The reporting day an instant falls on (ADR-052's fixed offset).</summary>
    public DateOnly Day(DateTimeOffset t) =>
        DateOnly.FromDateTime(t.ToOffset(TimeSpan.FromHours(Config.ReportingUtcOffsetHours)).DateTime);

    public DateTimeOffset StartOf(DateOnly day) =>
        new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue),
            TimeSpan.FromHours(Config.ReportingUtcOffsetHours)).ToUniversalTime();

    // ── Per-learner indexes, built on first use ──────────────────────────────

    private Dictionary<Guid, List<Word>>? _wordsByUser;
    public IReadOnlyList<Word> WordsOf(Guid userId) =>
        (_wordsByUser ??= Words.GroupBy(w => w.UserId).ToDictionary(g => g.Key, g => g.ToList()))
        .GetValueOrDefault(userId) ?? [];

    private Dictionary<Guid, List<SkillSession>>? _sessionsByUser;
    public IReadOnlyList<SkillSession> SessionsOf(Guid userId) =>
        (_sessionsByUser ??= Sessions.GroupBy(s => s.UserId).ToDictionary(g => g.Key, g => g.ToList()))
        .GetValueOrDefault(userId) ?? [];

    private Dictionary<Guid, List<WeeklyReview>>? _reviewsByUser;
    public IReadOnlyList<WeeklyReview> ReviewsOf(Guid userId) =>
        (_reviewsByUser ??= Reviews.GroupBy(s => s.UserId).ToDictionary(g => g.Key, g => g.ToList()))
        .GetValueOrDefault(userId) ?? [];

    private Dictionary<Guid, List<AnalyticsEvent>>? _eventsByUser;
    public IReadOnlyList<AnalyticsEvent> EventsOf(Guid userId) =>
        (_eventsByUser ??= Events.GroupBy(s => s.UserId).ToDictionary(g => g.Key, g => g.ToList()))
        .GetValueOrDefault(userId) ?? [];

    private Dictionary<Guid, Word>? _wordById;
    public Word? WordById(Guid? id) => id is null ? null
        : (_wordById ??= Words.ToDictionary(w => w.Id)).GetValueOrDefault(id.Value);

    private Dictionary<Guid, User>? _learnerById;
    public User? LearnerById(Guid id) =>
        (_learnerById ??= Learners.ToDictionary(u => u.Id)).GetValueOrDefault(id);

    private Dictionary<Guid, List<DateTimeOffset>>? _activityTimes;

    /// <summary>
    /// Every moment this learner was demonstrably doing something.
    /// </summary>
    /// <remarks>
    /// The union of everything durable — sign-ins, sessions, words added,
    /// reviews, feedback, and the phone's own events. A moved clock
    /// (<see cref="ActivityType.ScheduleAdvanced"/>) is the Owner's doing, not
    /// the learner's, and is left out.
    /// </remarks>
    public IReadOnlyList<DateTimeOffset> ActivityOf(Guid userId)
    {
        _activityTimes ??= BuildActivity();
        return _activityTimes.GetValueOrDefault(userId) ?? [];
    }

    private Dictionary<Guid, List<DateTimeOffset>> BuildActivity()
    {
        var map = new Dictionary<Guid, List<DateTimeOffset>>();
        void Add(Guid u, DateTimeOffset t)
        {
            if (!map.TryGetValue(u, out var list)) map[u] = list = [];
            list.Add(t);
        }

        foreach (var a in Activity.Where(a => a.Type != ActivityType.ScheduleAdvanced)) Add(a.UserId, a.CreatedAt);
        foreach (var s in Sessions)
        {
            Add(s.UserId, s.StartedAt);
            if (s.CompletedAt is { } c) Add(s.UserId, c);
        }
        foreach (var w in Words) Add(w.UserId, w.AddedAt);
        foreach (var r in Reviews) Add(r.UserId, r.StartedAt);
        foreach (var f in Feedback) Add(f.UserId, f.CreatedAt);
        foreach (var e in Events.Where(e => e.Source == AnalyticsSource.Client)) Add(e.UserId, e.OccurredAt);

        foreach (var list in map.Values) list.Sort();
        return map;
    }

    private Dictionary<Guid, HashSet<DateOnly>>? _activeDays;

    public IReadOnlySet<DateOnly> ActiveDaysOf(Guid userId)
    {
        _activeDays ??= Learners.ToDictionary(
            u => u.Id, u => ActivityOf(u.Id).Select(Day).ToHashSet());
        return _activeDays.GetValueOrDefault(userId) ?? [];
    }

    public DateTimeOffset? LastActiveAt(Guid userId) =>
        ActivityOf(userId) is { Count: > 0 } list ? list[^1] : null;

    // ── Session facts every page needs ───────────────────────────────────────

    /// <summary>
    /// An unfinished session nobody will come back to.
    /// </summary>
    /// <remarks>
    /// Explicitly abandoned sessions are deleted and survive only as
    /// <see cref="AnalyticsEventNames.SessionAbandoned"/>; this is the other
    /// kind — left open, past the point anyone resumes.
    /// </remarks>
    public bool IsStale(SkillSession s) =>
        !s.IsComplete && Now - s.StartedAt > TimeSpan.FromHours(Options.AbandonAfterHours);

    /// <summary>How long a finished session took, or null when that is not knowable.</summary>
    public double? DurationMs(SkillSession s) =>
        s.CompletedAt is { } c && c - s.StartedAt <= TimeSpan.FromMinutes(Options.SessionCapMinutes)
            ? (c - s.StartedAt).TotalMilliseconds
            : null;

    /// <summary>The learner's level, for filtering: Reading's, measured if measured.</summary>
    public static CefrLevel? LevelOf(User u)
    {
        var reading = u.SkillLevels.FirstOrDefault(l => l.Skill == SkillType.Reading);
        return reading?.SystemAssessedLevel ?? reading?.UserSelectedLevel;
    }
}

/// <summary>A period, and the one before it of the same length.</summary>
public sealed record IntelPeriod(DateTimeOffset From, DateTimeOffset To)
{
    public TimeSpan Length => To - From;
    public DateTimeOffset PrevFrom => From - Length;
    public DateTimeOffset PrevTo => From;

    public bool Contains(DateTimeOffset t) => t >= From && t < To;
    public bool PrevContains(DateTimeOffset t) => t >= PrevFrom && t < PrevTo;
}
