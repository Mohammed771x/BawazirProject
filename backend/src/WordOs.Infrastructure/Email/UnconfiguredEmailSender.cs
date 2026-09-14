using Microsoft.Extensions.Logging;
using WordOs.Application.Abstractions;

namespace WordOs.Infrastructure.Email;

/// <summary>
/// Stands in when no provider key is configured (ADR-078).
/// </summary>
/// <remarks>
/// Two very different situations end up here, and conflating them would be a
/// security bug:
///
/// <b>Development</b>, where there is no key on purpose. The reset code is
/// written to the log so the flow can be walked end to end without a provider
/// account. That is a live credential in a log file, which is exactly what
/// docs/07-SECURITY.md §9 forbids — permitted only because the log is a
/// developer's own terminal and the account is a throwaway.
///
/// <b>Production with a missing key</b>, which is a misconfiguration. The code
/// is <i>not</i> logged: Render's log is readable by anyone with dashboard
/// access, and a password-reset code sitting in it is a way into a learner's
/// account. The request fails, loudly, and the learner is told to contact
/// support — the same answer as any other delivery failure.
/// </remarks>
public sealed class UnconfiguredEmailSender(
    bool isDevelopment,
    ILogger<UnconfiguredEmailSender> log) : IEmailSender
{
    public Task<bool> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (!isDevelopment)
        {
            log.LogError(
                "A password reset was requested but Email:ApiKey and " +
                "Email:FromAddress are not configured, so nothing was sent. " +
                "See docs/09-DEPLOYMENT.md §2.");
            return Task.FromResult(false);
        }

        log.LogWarning(
            "No email provider configured — development only. Message to {To}:\n{Body}",
            message.ToAddress, message.TextBody);

        return Task.FromResult(true);
    }
}
