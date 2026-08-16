using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using WordOs.Application.Abstractions;

namespace WordOs.Infrastructure.Security;

/// <summary>
/// Argon2id password hashing (docs/07-SECURITY.md §2).
/// </summary>
/// <remarks>
/// Argon2id is the current recommendation: memory-hard, so a GPU or ASIC gains
/// far less against it than against PBKDF2 or a fast hash. Never MD5 or a bare
/// SHA — those are designed to be fast, which is the opposite of what a
/// password hash needs.
///
/// The stored format carries its own parameters:
/// <code>$argon2id$v=19$m=19456,t=2,p=1$&lt;salt-b64&gt;$&lt;hash-b64&gt;</code>
/// so raising the cost later does not invalidate existing hashes — old ones
/// still verify with the parameters they were created under.
/// </remarks>
public sealed class Argon2PasswordHasher : IPasswordHasher
{
    // OWASP's baseline: 19 MiB, 2 iterations, 1 degree of parallelism.
    private const int MemoryKib = 19456;
    private const int Iterations = 2;
    private const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Derive(password, salt, MemoryKib, Iterations, Parallelism);

        return string.Join('$',
            "",
            "argon2id",
            "v=19",
            $"m={MemoryKib},t={Iterations},p={Parallelism}",
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash))
            return false;

        try
        {
            // $argon2id$v=19$m=...,t=...,p=...$salt$hash
            var parts = hash.Split('$');
            if (parts.Length != 6 || parts[1] != "argon2id") return false;

            var settings = parts[3].Split(',');
            var memory = int.Parse(settings[0][2..]);
            var iterations = int.Parse(settings[1][2..]);
            var parallelism = int.Parse(settings[2][2..]);

            var salt = Convert.FromBase64String(parts[4]);
            var expected = Convert.FromBase64String(parts[5]);

            var actual = Derive(password, salt, memory, iterations, parallelism);

            // Fixed-time comparison: a byte-by-byte early exit would leak how
            // much of the hash matched.
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (Exception e) when (
            e is FormatException or IndexOutOfRangeException or OverflowException
                or ArgumentException)
        {
            // A malformed stored hash fails the login rather than 500-ing.
            return false;
        }
    }

    private static byte[] Derive(
        string password,
        byte[] salt,
        int memoryKib,
        int iterations,
        int parallelism)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKib,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(HashBytes);
    }
}
