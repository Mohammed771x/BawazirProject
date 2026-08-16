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
}
