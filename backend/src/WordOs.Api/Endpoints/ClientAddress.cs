using System.Net;

namespace WordOs.Api.Endpoints;

/// <summary>
/// Who is calling, as far as the rate limiter is concerned (ADR-106).
/// </summary>
public static class ClientAddress
{
    /// <summary>
    /// Headers worth seeing when a request is refused: the ones some proxy in
    /// front of this service may have written the caller's address into.
    /// </summary>
    public static readonly string[] Candidates =
        ["X-Forwarded-For", "True-Client-IP", "CF-Connecting-IP", "X-Real-IP"];

    /// <summary>
    /// The caller's address: from <paramref name="header"/> when it is named
    /// and holds a valid address at <paramref name="entry"/>, otherwise from
    /// the socket.
    /// </summary>
    /// <remarks>
    /// <paramref name="entry"/> picks from a comma-separated list: 0 is the
    /// first, -1 the last, -2 the one before it. It matters because proxies
    /// <i>append</i>: on a list like X-Forwarded-For only the entries the
    /// platform's own proxies added are trustworthy — everything to their
    /// left is whatever the caller chose to send.
    ///
    /// A value that does not parse as an address is ignored rather than used
    /// as a partition key: a key the caller chooses freely is a key they can
    /// change on every request.
    /// </remarks>
    public static string Of(HttpContext context, string? header, int entry = 0)
    {
        if (!string.IsNullOrWhiteSpace(header)
            && context.Request.Headers.TryGetValue(header, out var values))
        {
            var list = values.ToString().Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var index = entry >= 0 ? entry : list.Length + entry;

            if (index >= 0 && index < list.Length
                && IPAddress.TryParse(list[index], out var address))
                return address.ToString();
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    /// <summary>The candidate headers as they arrived, for a log line.</summary>
    public static string Describe(HttpContext context) =>
        string.Join("; ", Candidates.Select(name =>
            $"{name}={Value(context, name)}"))
        + $"; socket={context.Connection.RemoteIpAddress}";

    private static string Value(HttpContext context, string name)
    {
        var value = context.Request.Headers[name].ToString();
        return value.Length > 0 ? value : "-";
    }
}
