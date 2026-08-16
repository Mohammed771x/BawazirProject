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
