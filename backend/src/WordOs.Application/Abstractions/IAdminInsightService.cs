namespace WordOs.Application.Abstractions;

/// <summary>
/// The evidence an investigation computed, handed over for interpretation.
/// </summary>
/// <param name="EvidenceJson">
/// Aggregates only — counts, rates, labels, and the names of the charts the
/// backend drew. No learner's name, address or words leave the backend for
/// this: the model is asked what figures might mean, not about anyone.
/// </param>
public sealed record InsightRequest(string Section, string Question, string EvidenceJson);

public sealed record InsightLead(string Title, string Why);

/// <summary>
/// What the model made of the evidence (ADR-125).
/// </summary>
/// <remarks>
/// Interpretation and hypotheses, never a decision and never a number of its
/// own: every figure the admin sees was computed by the backend, and the page
/// keeps the two visibly apart (admin brief §26 — Data vs Interpretation vs
/// Hypothesis). Rule R2 in another room: the AI describes, people decide.
/// </remarks>
public sealed record InsightResponse(
    string Summary,
    IReadOnlyList<string> Interpretation,
    IReadOnlyList<string> Hypotheses,
    IReadOnlyList<InsightLead> Investigate,
    IReadOnlyList<string> Charts,
    string Model,
    string PromptVersion);

public interface IAdminInsightService
{
    /// <summary>Null when the model could not be reached; the caller falls back to rules.</summary>
    Task<InsightResponse?> InterpretAsync(InsightRequest request, CancellationToken ct = default);
}
