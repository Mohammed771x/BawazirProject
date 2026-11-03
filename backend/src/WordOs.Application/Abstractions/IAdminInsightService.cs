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

// ── WordOS AI (ADR-133) ─────────────────────────────────────────────────────

/// <param name="Data">The aggregates an earlier answer rested on, offered for reuse.</param>
public sealed record AgentTurn(string Role, string Text, string? Data = null);

/// <param name="ArgsJson">A JSON object, as text; the backend parses and validates it.</param>
public sealed record AgentToolCall(string Tool, string ArgsJson, string Why);

public sealed record AgentRound(IReadOnlyList<AgentToolCall> Calls, IReadOnlyList<string> Results);

/// <summary>
/// One step of the admin agent: the conversation, the tools already run for
/// this message, and what the backend can run. Aggregates only — no learner's
/// name, address or words.
/// </summary>
public sealed record AgentStepRequest(
    string Message,
    IReadOnlyList<AgentTurn> Conversation,
    IReadOnlyList<AgentRound> Steps,
    string Tools,
    string Dictionary,
    string Today,
    bool MustAnswer);

/// <param name="Action">
/// <c>tools</c> or <c>answer</c> — or <c>limited</c> when the model's free
/// quota is used up, with how long until it is back (ADR-134).
/// </param>
/// <param name="Fallback">
/// <c>quota</c> or <c>unavailable</c> when Groq could not write this step and
/// Gemini did; null when Groq answered.
/// </param>
public sealed record AgentStepResult(
    string Action,
    IReadOnlyList<AgentToolCall> Calls,
    string Answer,
    IReadOnlyList<string> Attachments,
    string Title,
    string Model,
    string PromptVersion,
    int Tokens,
    int? RetryAfterSeconds = null,
    string? Fallback = null)
{
    public static AgentStepResult Limited(int? retryAfterSeconds) =>
        new("limited", [], string.Empty, [], string.Empty, string.Empty, string.Empty, 0, retryAfterSeconds);
}

/// <summary>The agent's model side, separate so tests can script it.</summary>
public interface IAdminAgentService
{
    /// <summary>Null when the model could not be reached.</summary>
    Task<AgentStepResult?> StepAsync(AgentStepRequest request, CancellationToken ct = default);
}
