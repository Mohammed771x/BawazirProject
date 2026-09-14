using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WordOs.Application.Abstractions;

namespace WordOs.Infrastructure.Email;

/// <summary>
/// Sends through Brevo's transactional API (ADR-078).
/// </summary>
public sealed class BrevoEmailSender(
    HttpClient http,
    IOptions<EmailOptions> options,
    ILogger<BrevoEmailSender> log) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public async Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var payload = new BrevoRequest(
            Sender: new BrevoContact(_options.FromAddress, _options.FromName),
            To: [new BrevoContact(message.ToAddress, message.ToName)],
            Subject: message.Subject,
            HtmlContent: message.HtmlBody,
            TextContent: message.TextBody);

        try
        {
            using var response = await http.PostAsJsonAsync("v3/smtp/email", payload, ct);

            if (response.IsSuccessStatusCode)
                return true;

            // The status and Brevo's own error code, never the body of the
            // message — the body is a live password-reset code
            // (docs/07-SECURITY.md §9). The recipient is not logged either: an
            // address plus "we tried to reset this" is exactly the pairing the
            // endpoint spends so much effort not disclosing.
            var detail = await response.Content.ReadAsStringAsync(ct);
            log.LogError(
                "Brevo refused a password-reset email: {Status} {Detail}",
                (int)response.StatusCode, Truncate(detail));

            return false;
        }
        // A provider outage must not become an exception the caller has to
        // handle differently from a refusal — the endpoint above answers
        // identically either way, on purpose.
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogError(e, "Could not reach Brevo to send a password-reset email.");
            return false;
        }
    }

    // Brevo's failures are short JSON objects; a runaway body is a sign
    // something else answered, and does not belong in the log either way.
    private static string Truncate(string value) =>
        value.Length <= 300 ? value : value[..300];

    private sealed record BrevoContact(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("name")] string? Name);

    private sealed record BrevoRequest(
        [property: JsonPropertyName("sender")] BrevoContact Sender,
        [property: JsonPropertyName("to")] IReadOnlyList<BrevoContact> To,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("htmlContent")] string HtmlContent,
        [property: JsonPropertyName("textContent")] string TextContent);
}
