using WordOs.Domain.Common;

namespace WordOs.Domain.Words;

/// <summary>
/// One of exactly five per word. The fine-grained progress the coarse
/// <see cref="WordState"/> projects from.
/// </summary>
public class WordSkillState
{
    private WordSkillState() { } // EF Core

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid WordId { get; private set; }

    public SkillType Skill { get; private set; }

    public SkillStatus Status { get; private set; }

    /// <summary>
    /// When this skill becomes startable. Null means "not scheduled yet";
    /// a future value is the spaced gap that makes the memory work.
    /// </summary>
    public DateTimeOffset? AvailableAt { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? PassedAt { get; private set; }

    public DateTimeOffset? FailedAt { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    public static WordSkillState Pending(Guid wordId, SkillType skill) =>
        new() { WordId = wordId, Skill = skill, Status = SkillStatus.Pending };

    public static WordSkillState Available(
        Guid wordId,
        SkillType skill,
        DateTimeOffset now) =>
        new()
        {
            WordId = wordId,
            Skill = skill,
            Status = SkillStatus.Available,
            AvailableAt = now,
        };

    /// <summary>
    /// The client is told the <i>effective</i> status: a scheduled skill whose
    /// date has passed reads as Available, never as Pending (rule R1 — the
    /// client must not work this out itself).
    /// </summary>
    public SkillStatus EffectiveStatus(DateTimeOffset now) =>
        Status == SkillStatus.Pending && AvailableAt is not null && AvailableAt <= now
            ? SkillStatus.Available
            : Status;

    internal void ScheduleAt(DateTimeOffset availableAt)
    {
        Status = SkillStatus.Pending;
        AvailableAt = availableAt;
    }

    /// <summary>
    /// Brings a waiting skill forward by <paramref name="days"/>.
    /// </summary>
    /// <remarks>
    /// A testing affordance, not part of the algorithm: the gaps are what the
    /// product is measuring, so waiting two real days to see the next skill
    /// open makes the pipeline impossible to demonstrate or to test end to end.
    ///
    /// It moves the *waiting* only. Nothing that already happened is touched —
    /// no attempt, no pass, no failure, no timestamp of a real event — so the
    /// history stays true and only the future arrives sooner.
    /// </remarks>
    internal void AdvanceSchedule(int days)
    {
        if (AvailableAt is null) return;
        AvailableAt = AvailableAt.Value.AddDays(-days);
    }

    internal void RecordAttempt(DateTimeOffset now)
    {
        Attempts++;
        LastAttemptAt = now;
    }

    internal void Pass(DateTimeOffset now)
    {
        Status = SkillStatus.Passed;
        PassedAt = now;
    }

    internal void Fail(DateTimeOffset now, DateTimeOffset retryAt)
    {
        Status = SkillStatus.Failed;
        FailedAt = now;
        AvailableAt = retryAt;
    }
}

/// <summary>Append-only word history.</summary>
public class WordEvent
{
    private WordEvent() { } // EF Core

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid WordId { get; private set; }

    public WordEventType Type { get; private set; }

    public SkillType? Skill { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    internal static WordEvent Create(
        Guid wordId,
        WordEventType type,
        SkillType? skill,
        DateTimeOffset now) =>
        new() { WordId = wordId, Type = type, Skill = skill, CreatedAt = now };
}
