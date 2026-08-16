namespace WordOs.Domain.Common;

/// <summary>
/// The CEFR ladder. Order is meaningful: <see cref="Rank"/> is used for
/// comparisons, promotion by one step, and the archiving distance (ADR-013).
/// </summary>
public enum CefrLevel
{
    A1 = 0,
    A1Plus = 1,
    A2 = 2,
    A2Plus = 3,
    B1 = 4,
    B1Plus = 5,
    B2 = 6,
    B2Plus = 7,
    C1 = 8,
    C1Plus = 9,
    C2 = 10,
}

public enum SkillType
{
    Reading,
    Listening,
    Speaking,
    Writing,
    Spelling,
}

public enum SkillStatus
{
    Pending,
    Available,
    Passed,
    Failed,
}

public enum WordState
{
    Learning,
    Mature,
    Active,
    Archived,
}

public enum UserRole
{
    User,
    Owner,
}

public enum OnboardingStage
{
    Interests,
    Placement,
    Complete,
}

/// <summary>
/// Why a level moved. Only <see cref="SystemValidatedChange"/> may drive
/// progression and archiving (rule R6).
/// </summary>
public enum LevelChangeType
{
    Placement,
    UserManualChange,
    SystemValidatedChange,
}

public enum WordEventType
{
    Added,
    SkillStarted,
    SkillPassed,
    SkillFailed,
    BecameMature,
    EnteredActive,
    ExposureIncremented,
    Archived,
}

public enum SpellingInputMode
{
    LetterTiles,
    FreeTyping,
}

public enum SpellingClueKind
{
    ArabicMeaning,
    DefinitionEn,
    Synonym,
}

public static class CefrLevelExtensions
{
    /// <summary>Position on the ladder — for ordering and comparisons only.</summary>
    public static int Rank(this CefrLevel level) => (int)level;

    /// <summary>
    /// Moves <paramref name="steps"/> along the ladder, or null past either end.
    /// Promotion is deliberately one step (<c>B1 → B1+</c>), never a whole band
    /// (<c>B1 → B2</c>) — <c>MVP Core.txt</c> §23.
    /// </summary>
    public static CefrLevel? Step(this CefrLevel level, int steps)
    {
        var index = level.Rank() + steps;
        return index < 0 || index > (int)CefrLevel.C2 ? null : (CefrLevel)index;
    }

    /// <summary>The wire value used by the REST contract (SCREAMING_SNAKE).</summary>
    public static string ToWire(this CefrLevel level) => level switch
    {
        CefrLevel.A1 => "A1",
        CefrLevel.A1Plus => "A1_PLUS",
        CefrLevel.A2 => "A2",
        CefrLevel.A2Plus => "A2_PLUS",
        CefrLevel.B1 => "B1",
        CefrLevel.B1Plus => "B1_PLUS",
        CefrLevel.B2 => "B2",
        CefrLevel.B2Plus => "B2_PLUS",
        CefrLevel.C1 => "C1",
        CefrLevel.C1Plus => "C1_PLUS",
        CefrLevel.C2 => "C2",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    public static CefrLevel? TryFromWire(string? wire) => wire switch
    {
        "A1" => CefrLevel.A1,
        "A1_PLUS" => CefrLevel.A1Plus,
        "A2" => CefrLevel.A2,
        "A2_PLUS" => CefrLevel.A2Plus,
        "B1" => CefrLevel.B1,
        "B1_PLUS" => CefrLevel.B1Plus,
        "B2" => CefrLevel.B2,
        "B2_PLUS" => CefrLevel.B2Plus,
        "C1" => CefrLevel.C1,
        "C1_PLUS" => CefrLevel.C1Plus,
        "C2" => CefrLevel.C2,
        _ => null,
    };
}
