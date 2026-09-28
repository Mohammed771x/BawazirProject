using System.Net;

namespace WordOs.Api.Endpoints;

/// <summary>
/// Who is calling, as far as the rate limiter is concerned (ADR-106).
/// </summary>
public static class ClientAddress
{
    /// <summary>
    /// The caller's address: from <paramref name="header"/> when it is named
    /// and holds a valid address, otherwise from the socket.
    /// </summary>
    /// <remarks>
    /// A value that does not parse as an address is ignored rather than used
    /// as a partition key: a key the caller chooses freely is a key they can
    /// change on every request.
    /// </remarks>
    public static string Of(HttpContext context, string? header)
    {
        if (!string.IsNullOrWhiteSpace(header)
            && context.Request.Headers.TryGetValue(header, out var values))
        {
            // A list is possible (X-Forwarded-For); the first entry is the one
            // the edge proxy recorded for the client.
            var first = values.ToString().Split(',')[0].Trim();
            if (IPAddress.TryParse(first, out var address))
                return address.ToString();
        }

        return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }
}
