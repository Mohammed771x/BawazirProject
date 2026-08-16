using WordOs.Domain.Common;

namespace WordOs.Domain.Lexicon;

/// <summary>
/// One sense from the vocabulary source: an English word in one specific
/// meaning, with its Arabic gloss and CEFR level.
/// </summary>
/// <remarks>
/// Global, not per-user. Built offline by joining three sources on the WordNet
/// synset id (ADR-012):
///
/// <code>
/// English word ─┐
///               ├─ synset ─┬─ definition_en  (Open English WordNet)
/// part of speech┘          ├─ meaning_ar     (Arabic WordNet)
///                          └─ cefr_level     (CEFR-J / Octanove)
/// </code>
///
/// <see cref="SenseId"/> is the identity. <c>book</c> alone is not:
/// <c>book = كتاب</c> and <c>book = يحجز</c> are different synsets and
/// therefore different vocabulary items.
///
/// Rows are <b>never</b> written from a client request. <c>POST /words</c>
/// treats the request body as a lookup key and copies the stored row, so a
/// forged level or definition cannot enter the system.
/// </remarks>
public class LexiconEntry
{
    private LexiconEntry() { } // EF Core

    /// <summary>WordNet synset id, e.g. <c>oewn-06410904-n</c>.</summary>
    public string SenseId { get; private set; } = string.Empty;

    /// <summary>The surface form the learner searches for.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Lowercased <see cref="Text"/>, for prefix search.</summary>
    public string TextNormalized { get; private set; } = string.Empty;

    public string Lemma { get; private set; } = string.Empty;

    public string PartOfSpeech { get; private set; } = string.Empty;

    public string DefinitionEn { get; private set; } = string.Empty;

    public string MeaningAr { get; private set; } = string.Empty;

    /// <summary>Null when no source supplied a band for this sense.</summary>
    public CefrLevel? CefrLevel { get; private set; }

    /// <summary>Lower is more frequent. Used to order autocomplete results.</summary>
    public int? FrequencyRank { get; private set; }

    /// <summary>
    /// Which source supplied each field, so an entry whose Arabic gloss came
    /// from the machine-translated portion of Arabic WordNet stays auditable
    /// and replaceable (ADR-012).
    /// </summary>
    public string SourceFlags { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }

    public static LexiconEntry Create(
        string senseId,
        string text,
        string lemma,
        string partOfSpeech,
        string definitionEn,
        string meaningAr,
        CefrLevel? cefrLevel,
        int? frequencyRank,
        string sourceFlags,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(senseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(meaningAr);

        return new LexiconEntry
        {
            SenseId = senseId.Trim(),
            Text = text.Trim(),
            TextNormalized = text.Trim().ToLowerInvariant(),
            Lemma = lemma.Trim(),
            PartOfSpeech = partOfSpeech.Trim(),
            DefinitionEn = definitionEn.Trim(),
            MeaningAr = meaningAr.Trim(),
            CefrLevel = cefrLevel,
            FrequencyRank = frequencyRank,
            SourceFlags = sourceFlags,
            UpdatedAt = now,
        };
    }
}
