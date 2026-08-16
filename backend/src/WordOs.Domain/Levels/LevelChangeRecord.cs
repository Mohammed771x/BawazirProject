using WordOs.Domain.Common;

namespace WordOs.Domain.Levels;

/// <summary>
/// One entry in the level history — the audit trail behind rule R6.
/// </summary>
/// <remarks>
/// Manual and system-validated changes live in one table, distinguished by
/// <see cref="ChangeType"/>. The gap between what a learner claims and what the
/// system proved is the metric the Owner dashboard reports
/// (<c>MVP Core.txt</c> §60).
/// </remarks>
public class LevelChangeRecord
{
    private LevelChangeRecord() { } // EF Core

    public Guid Id { get; private set; } = Guid.CreateVersion7();

    public Guid UserId { get; private set; }

    public SkillType Skill { get; private set; }

    public CefrLevel? PreviousLevel { get; private set; }

    public CefrLevel? NewLevel { get; private set; }

    public LevelChangeType ChangeType { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public int SessionsConsidered { get; private set; }

    public double Accuracy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static LevelChangeRecord Create(
        Guid userId,
        SkillType skill,
        CefrLevel? previous,
        CefrLevel? next,
        LevelChangeType changeType,
        DateTimeOffset now,
        string reason = "",
        int sessionsConsidered = 0,
        double accuracy = 0) =>
        new()
        {
            UserId = userId,
            Skill = skill,
            PreviousLevel = previous,
            NewLevel = next,
            ChangeType = changeType,
            Reason = reason,
            SessionsConsidered = sessionsConsidered,
            Accuracy = accuracy,
            CreatedAt = now,
        };
}
