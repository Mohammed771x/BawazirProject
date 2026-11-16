using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Endpoints;

/// <summary>
/// Serialises the requests that act on one thing — a session, a refresh token,
/// a learner's weekly review — with a PostgreSQL advisory lock (ADR-137).
/// </summary>
/// <remarks>
/// <para>The bug this exists for was found by the QA campaign of 2026-11-11:
/// ten "complete" requests released together all read the session as open,
/// all applied it, and the word's history said it had passed Reading ten
/// times. The rows that are <i>updated</i> came out right by accident — each
/// request overwrote the same values — but every row that is <i>inserted</i>
/// (word events, the activity log) was written once per request, and the
/// admin area counts those. The same read-then-write window let one refresh
/// token mint ten live successors, and let ten weekly reviews open at once.</para>
///
/// <para>An advisory lock rather than <c>SELECT … FOR UPDATE</c>, because the
/// latter needs a transaction the caller owns, and a caller-owned transaction
/// cannot be combined with the connection-retry strategy this service runs
/// under without wrapping and replaying the whole handler — including, for
/// Speaking, the AI call that judges the conversation. A session-level
/// advisory lock needs only the connection held open: the second request
/// waits, then re-reads and finds the work done, and answers exactly as it
/// would have a second later.</para>
///
/// <para>Bounded by the command timeout like any query, released explicitly
/// on the way out, and released by PostgreSQL itself if the connection dies.
/// A pooled connection is reset (<c>DISCARD ALL</c>) before reuse, which also
/// drops any lock a crash left behind.</para>
/// </remarks>
public static class RowLock
{
    public static Task<IAsyncDisposable> AcquireAsync(
        WordOsDbContext db, string scope, Guid id, CancellationToken ct) =>
        AcquireAsync(db, scope, id.ToString("N"), ct);

    public static async Task<IAsyncDisposable> AcquireAsync(
        WordOsDbContext db, string scope, string id, CancellationToken ct)
    {
        var key = KeyOf(scope, id);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock({key})", ct);
        }
        catch
        {
            await db.Database.CloseConnectionAsync();
            throw;
        }

        return new Release(db, key);
    }

    /// <summary>A stable 64-bit key: the scope keeps a session id from ever
    /// colliding with a user id that happens to share its bytes.</summary>
    internal static long KeyOf(string scope, string id) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}:{id}")), 0);

    private sealed class Release(WordOsDbContext db, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                // Not the request's token: a request the client abandoned must
                // still give the lock back.
                await db.Database.ExecuteSqlAsync(
                    $"SELECT pg_advisory_unlock({key})", CancellationToken.None);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}
