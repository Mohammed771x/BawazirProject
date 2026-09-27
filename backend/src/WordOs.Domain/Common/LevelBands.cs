namespace WordOs.Domain.Common;

/// <summary>
/// What a word's meaning looks like when it is offered as an answer.
/// </summary>
/// <remarks>
/// The options on "what does this word mean here?" are not one thing at every
/// level (ADR-088). An A2 learner shown four English definitions is being
/// tested on the definitions; a C1 learner shown four Arabic words is being
/// asked to translate, which is easier than the word is. So the options change
/// register with the learner, and the question stays the same question.
/// </remarks>
public enum MeaningOptionStyle
{
    /// <summary>Arabic — the meaning as the learner added it (A1–A2+).</summary>
    ArabicMeaning,

    /// <summary>
    /// English, but written plainly for a learner still building the language
    /// (B1–B2). Not a dictionary line: short, ordinary words, one idea.
    /// </summary>
    SimpleDefinition,

    /// <summary>
    /// English as a dictionary writes it (B2+ and above) — the lexicon's own
    /// gloss, which is the register that band reads unaided.
    /// </summary>
    DictionaryDefinition,
}

/// <summary>
/// Where the CEFR ladder is cut, for decisions that are about the learner's
/// standing rather than about a word.
/// </summary>
/// <remarks>
/// One place, because two separate rules read the same ladder and drifting
/// boundaries between them would be invisible: a learner could be given English
/// options and an Arabic instruction, or the reverse, and nothing would say so.
///
/// The bands are the product owner's, given on 2026-09-17 (ADR-088):
/// A1–A2+ Arabic, B1–B2 plain English, B2+ and above dictionary English.
/// </remarks>
public static class LevelBands
{
    /// <summary>The register this level's answer options are written in.</summary>
    public static MeaningOptionStyle OptionStyleFor(CefrLevel level) =>
        level.Rank() >= CefrLevel.B2Plus.Rank()
            ? MeaningOptionStyle.DictionaryDefinition
            : level.Rank() >= CefrLevel.B1.Rank()
                ? MeaningOptionStyle.SimpleDefinition
                : MeaningOptionStyle.ArabicMeaning;

    /// <summary>
    /// Whether a task instruction is given in English rather than the learner's
    /// own language.
    /// </summary>
    /// <remarks>
    /// B1 and above (ADR-088). Below it the instruction is scaffolding and has
    /// to be understood instantly or the task is a reading test with a writing
    /// task attached; at B1 the instruction is itself worth reading in English,
    /// and a learner about to write English has already started in it.
    ///
    /// The boundary is deliberately the same rung that turns the options
    /// English, so a session does not speak two languages about itself.
    /// </remarks>
    public static bool InstructionsInEnglish(CefrLevel level) =>
        level.Rank() >= CefrLevel.B1.Rank();

    /// <summary>The wire value used by the REST contract (SCREAMING_SNAKE).</summary>
    public static string ToWire(this MeaningOptionStyle style) => style switch
    {
        MeaningOptionStyle.ArabicMeaning => "ARABIC_MEANING",
        MeaningOptionStyle.SimpleDefinition => "SIMPLE_DEFINITION",
        MeaningOptionStyle.DictionaryDefinition => "DICTIONARY_DEFINITION",
        _ => throw new ArgumentOutOfRangeException(nameof(style)),
    };
}
