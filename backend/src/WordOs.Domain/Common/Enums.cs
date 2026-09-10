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

    /// <summary>
    /// The learner removed it from their vocabulary (ADR-071).
    /// </summary>
    /// <remarks>
    /// Gone as far as the learner is concerned — a global query filter keeps it
    /// out of every list, session and review — but the row and its whole
    /// history stay, because this service exists to measure whether the
    /// pipeline works (<c>docs/00-PROJECT-PLAN.md</c> §1) and a word that
    /// vanishes takes its evidence with it. The Owner still sees it.
    /// </remarks>
    Deleted,
}

/// <summary>
/// Where a word's Arabic meaning came from (ADR-072).
/// </summary>
/// <remarks>
/// Recorded because the three are not equally trustworthy and the experiment
/// has to be able to tell them apart. <see cref="Lexicon"/> is a curated gloss;
/// <see cref="Passage"/> is what the generator meant by the word in one
/// sentence; <see cref="Learner"/> is whatever the learner typed. If words with
/// learner-written meanings turn out to fail Spelling twice as often, that is a
/// finding — and it is unreadable if every meaning looks alike in the data.
/// </remarks>
public enum MeaningSource
{
    /// <summary>Copied from <c>lexicon_entries</c>, the curated join.</summary>
    Lexicon,

    /// <summary>The learner wrote it themselves in Add Word (ADR-072).</summary>
    Learner,

    /// <summary>
    /// The meaning the passage generator gave the word in the sentence the
    /// learner tapped it in (ADR-073).
    /// </summary>
    Passage,
}

/// <summary>
/// What the meaning checker made of a learner-written meaning (ADR-074).
/// </summary>
/// <remarks>
/// Only ever set for <see cref="MeaningSource.Learner"/>. A lexicon gloss and a
/// passage gloss are not the learner's guesses and are not checked.
///
/// <para><see cref="Overridden"/> is the one that earns its place. The learner
/// may save a meaning the checker rejected, because the checker is sometimes
/// wrong and this whole feature exists to escape an automated source that was
/// (ADR-072). But "the model said no and the learner said yes anyway" is
/// exactly the fact that explains a word failing Spelling four times a fortnight
/// later, and it is unrecoverable if it was never written down.</para>
/// </remarks>
public enum MeaningCheckResult
{
    /// <summary>The checker agreed the Arabic means what the word means.</summary>
    Approved,

    /// <summary>The checker disagreed; the learner saved it regardless.</summary>
    Overridden,
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

    /// <summary>The learner removed the word (ADR-071).</summary>
    Deleted,
}

public enum SpellingInputMode
{
    LetterTiles,
    FreeTyping,
}

/// <summary>
/// The rungs of the spelling hint ladder, hardest first.
/// </summary>
/// <remarks>
/// The order is the ladder: each rung is easier and more explicit than the one
/// above it. Where a learner joins depends on their level — a C1 learner starts
/// at the dictionary definition, an A2 learner at the translation — and every
/// press of "hint" steps down one rung.
///
/// Handing over the whole ladder at once would make the task trivial; handing
/// over none of it strands anyone who cannot start. Stepping down does neither.
/// </remarks>
public enum SpellingClueKind
{
    /// <summary>The dictionary definition, as written.</summary>
    DefinitionEn,

    /// <summary>The first gloss only, without the elaboration.</summary>
    SimplifiedDefinition,

    /// <summary>Another word for it.</summary>
    Synonym,

    /// <summary>What it means, in Arabic.</summary>
    ArabicMeaning,

    /// <summary>How many letters it has — the last resort.</summary>
    LetterCount,
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

/// <summary>
/// What a session item asks the learner to do.
/// </summary>
/// <remarks>
/// The instruction is a <b>key</b>, not a sentence, because instructions are
/// part of the interface rather than part of the material: a learner reading the
/// app in Arabic should be told what to do in Arabic, while the English they are
/// there to learn — the passage, the questions about it, the meanings on offer —
/// stays English (ADR-035).
///
/// So the server decides <i>what</i> is being asked and the client renders it in
/// the language the learner chose. A question generated for one passage has no
/// key: its text is the content.
/// </remarks>
public enum SessionPromptKey
{
    /// <summary>Spell the word you are being shown clues for.</summary>
    WriteTheWord,

    /// <summary>Use this word in a sentence.</summary>
    WriteASentence,

    /// <summary>Use this word in a sentence about your own life.</summary>
    WriteASentenceAboutYourself,
}
