using System.ComponentModel.DataAnnotations;

namespace WordOs.Infrastructure.Email;

/// <summary>
/// Where password-reset email comes from, and the key that sends it.
/// </summary>
/// <remarks>
/// Brevo was chosen over a domain-verified provider for one practical reason:
/// it will verify a single <i>sender address</i> — an ordinary Gmail account —
/// where most transactional providers require a whole domain the operator does
/// not own (ADR-078). Its free allowance is 300 messages a day, which is far
/// more resets than a cohort of learners will ever ask for.
/// </remarks>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>
    /// The Brevo API key. Absent in development, where the code is logged
    /// instead of sent.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// The address the learner sees. **Must be verified in Brevo** — an
    /// unverified sender is accepted by the API and then silently not
    /// delivered, which looks exactly like the feature not working.
    /// </summary>
    [EmailAddress, MaxLength(320)]
    public string FromAddress { get; init; } = "";

    public string FromName { get; init; } = "WordOS";

    public string BaseUrl { get; init; } = "https://api.brevo.com/";

    public int TimeoutSeconds { get; init; } = 15;

    /// <summary>Whether a real provider is configured.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(FromAddress);
}
