namespace WordOs.Application.Abstractions;

/// <summary>
/// Hashes and verifies passwords.
/// </summary>
/// <remarks>
/// An interface so the algorithm can be upgraded without touching a single use
/// case, and so tests can substitute a fast fake — a real Argon2id verification
/// is deliberately slow, which would make an integration suite crawl.
/// </remarks>
public interface IPasswordHasher
{
    string Hash(string password);

    /// <summary>
    /// Verifies in constant time with respect to the password.
    /// </summary>
    /// <remarks>
    /// Returns false rather than throwing on a malformed stored hash: a corrupt
    /// row must fail the login, not crash the endpoint.
    /// </remarks>
    bool Verify(string password, string hash);

    /// <summary>
    /// Hashes without holding a request thread while it waits its turn.
    /// </summary>
    /// <remarks>
    /// Hashing is deliberately expensive, so an implementation may make callers
    /// queue. Queueing on a synchronous call blocks a thread-pool thread, and
    /// two hundred blocked threads is how a server stops answering requests
    /// that have nothing to do with signing in — measured: the same load served
    /// half as fast through a blocking wait (ADR-051).
    ///
    /// The default is the synchronous call, so a hasher with no queue — every
    /// test fake — needs to say nothing.
    /// </remarks>
    Task<string> HashAsync(string password, CancellationToken ct = default) =>
        Task.FromResult(Hash(password));

    Task<bool> VerifyAsync(
        string password, string hash, CancellationToken ct = default) =>
        Task.FromResult(Verify(password, hash));
}
