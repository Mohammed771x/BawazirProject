using System.ComponentModel.DataAnnotations;
using System.Security.Claims;

namespace WordOs.Api.Endpoints;

/// <summary>
/// Error responses in the shape the API contract defines:
/// <c>{ "error": { "code", "message" } }</c>.
/// </summary>
/// <remarks>
/// Never a stack trace, never SQL, never an inner exception — in any
/// environment (docs/07-SECURITY.md §9). The <c>code</c> is what clients branch
/// on; the <c>message</c> is a plain sentence safe to show a learner.
/// </remarks>
public static class Problems
{
    private static IResult Error(int status, string code, string message) =>
        Results.Json(
            new { error = new { code, message } },
            statusCode: status);

    public static IResult BadRequest(string code, string message) =>
        Error(StatusCodes.Status400BadRequest, code, message);

    public static IResult Unauthorized(string code, string message) =>
        Error(StatusCodes.Status401Unauthorized, code, message);

    public static IResult Forbidden(string code, string message) =>
        Error(StatusCodes.Status403Forbidden, code, message);

    public static IResult NotFound(string code, string message) =>
        Error(StatusCodes.Status404NotFound, code, message);

    public static IResult Conflict(string code, string message) =>
        Error(StatusCodes.Status409Conflict, code, message);

    /// <summary>
    /// A dependency is down and the caller should try again — not their fault
    /// and not a permanent failure, which is what 503 says and 500 does not.
    /// </summary>
    public static IResult Unavailable(string code, string message) =>
        Error(StatusCodes.Status503ServiceUnavailable, code, message);
}

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// The caller's id, taken from the signed token and nowhere else.
    /// </summary>
    /// <remarks>
    /// Every query scopes on this. A user id from a route, query string or body
    /// is an IDOR waiting to happen (docs/07-SECURITY.md §4).
    /// </remarks>
    public static Guid? UserId(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue("sub");
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    public static bool IsOwner(this ClaimsPrincipal principal) =>
        principal.IsInRole(nameof(Domain.Common.UserRole.Owner));
}

/// <summary>Runs DataAnnotations on a request record.</summary>
public static class MiniValidator
{
    public static bool TryValidate(
        object instance,
        out Dictionary<string, string[]> errors)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(instance);
        var valid = Validator.TryValidateObject(
            instance, context, results, validateAllProperties: true);

        errors = results
            .SelectMany(r => r.MemberNames.DefaultIfEmpty(""),
                (r, member) => (member, r.ErrorMessage ?? "Invalid"))
            .GroupBy(x => x.member)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Item2).ToArray());

        return valid;
    }
}

/// <summary>
/// The language the learner is reading the app in.
/// </summary>
/// <remarks>
/// Sent as <c>Accept-Language</c> rather than stored, because it is a device
/// setting and not an account fact (ADR-010): the same learner may read the app
/// in Arabic on a phone and English on a tablet, and neither is more true.
///
/// It affects what the app <i>says to</i> the learner — instructions, feedback —
/// and never what it teaches them: passages, questions, options and the
/// placement test stay English, because that is the material (ADR-035).
/// </remarks>
public static class LearnerLanguage
{
    /// <summary>Arabic, per ADR-010 — the product's default audience.</summary>
    public const string Default = "ar";

    private static readonly string[] Supported = ["ar", "en"];

    public static string From(HttpRequest request)
    {
        var header = request.Headers.AcceptLanguage.ToString();
        if (string.IsNullOrWhiteSpace(header)) return Default;

        // "ar-YE,ar;q=0.9,en;q=0.8" — the first tag wins. Quality values are
        // not weighed: the client sends its own single setting, and a browser's
        // full preference list is not something a learner chose here.
        foreach (var part in header.Split(','))
        {
            var tag = part.Split(';')[0].Trim();
            if (tag.Length == 0) continue;

            var primary = tag.Split('-')[0].ToLowerInvariant();
            if (Supported.Contains(primary)) return primary;
        }

        return Default;
    }
}

/// <summary>
/// Free text as it arrives from a client, made safe to compare against.
/// </summary>
/// <remarks>
/// A search box takes whatever a keyboard, a paste or a broken client sends,
/// including control characters. PostgreSQL rejects a NUL byte inside a text
/// value outright, which turned one pasted character into a 500 — the query is
/// parameterised, so this was never an injection risk, just a crash.
///
/// Everything below U+0020 goes, because none of it is searchable: a learner
/// looking for a word never means a vertical tab.
/// </remarks>
public static class SearchTerm
{
    public static string Clean(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        var kept = raw.Where(c => !char.IsControl(c)).ToArray();
        return new string(kept).Trim();
    }
}

/// <summary>
/// The reporting window an admin query covers.
/// </summary>
/// <remarks>
/// The number of days is typed in by hand, so it arrives as whatever was typed.
/// `DateTimeOffset.AddDays` throws on anything past year 9999, which turned a
/// slip of the keyboard into a 500 and an empty dashboard; and a window longer
/// than the product has existed is the same question as "all time" anyway.
///
/// So the count is clamped rather than trusted, in one place, because the
/// overview and the learner list have to agree on what "the last 5 days" means.
/// </remarks>
public static class ReportingWindow
{
    /// <summary>Ten years — longer than any real reporting question.</summary>
    public const int MaximumDays = 3650;

    /// <summary>
    /// The instant the window opens, or <see cref="DateTimeOffset.MinValue"/>
    /// for all time.
    /// </summary>
    /// <remarks>
    /// "Today" is day 1, so a five-day window is today plus the four before it,
    /// starting at midnight UTC rather than sliding with the hour the Owner
    /// happens to look.
    /// </remarks>
    public static DateTimeOffset From(int? days, DateTimeOffset now)
    {
        if (days is not > 0) return DateTimeOffset.MinValue;

        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        return today.AddDays(-(Math.Min(days.Value, MaximumDays) - 1));
    }
}

/// <summary>Named rate-limit policies (docs/07-SECURITY.md §6).</summary>
public static class RateLimitPolicies
{
    /// <summary>Login and registration — the credential-stuffing surface.</summary>
    public const string Authentication = "auth";

    /// <summary>Lookup fires on every keystroke, so its budget is larger.</summary>
    public const string Lookup = "lookup";

    /// <summary>Each of these costs an AI call, so the budget is tight.</summary>
    public const string Expensive = "expensive";
}
