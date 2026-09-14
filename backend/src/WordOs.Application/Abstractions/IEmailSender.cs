namespace WordOs.Application.Abstractions;

/// <summary>An email WordOS sends to a learner.</summary>
/// <param name="ToAddress">The recipient. Always an address already in the database.</param>
/// <param name="Subject">Plain text, one line.</param>
/// <param name="HtmlBody">What most clients render.</param>
/// <param name="TextBody">
/// The same message as plain text. Not optional: a message with no text part
/// is markedly more likely to be filed as spam, which for a password reset
/// means a learner who simply never receives it.
/// </param>
public sealed record EmailMessage(
    string ToAddress,
    string? ToName,
    string Subject,
    string HtmlBody,
    string TextBody);

/// <summary>
/// Sends transactional email.
/// </summary>
/// <remarks>
/// An interface for the usual reason — the provider is a detail and the tests
/// need a fake — and for one specific to this app: the only email WordOS sends
/// is a password reset code, so every implementation handles a live credential
/// and must never log the body (docs/07-SECURITY.md §9).
/// </remarks>
public interface IEmailSender
{
    /// <summary>
    /// Returns whether the provider accepted the message.
    /// </summary>
    /// <remarks>
    /// Returns false rather than throwing. The endpoint that calls this answers
    /// the same way whether the address exists or not, so a provider outage
    /// must not become the one response that differs and gives the answer away
    /// (ADR-078).
    /// </remarks>
    Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default);
}
