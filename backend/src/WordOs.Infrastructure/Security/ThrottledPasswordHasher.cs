using WordOs.Application.Abstractions;

namespace WordOs.Infrastructure.Security;

/// <summary>
/// A password hasher that lets only so many hashes run at once (ADR-051).
/// </summary>
/// <remarks>
/// Argon2id is memory-hard by design: 19 MiB and a core for the duration of
/// every verification. That is the property that makes a stolen database
/// expensive to crack, and it is also the property that turns a crowd of
/// sign-ins into an out-of-memory kill — a thousand at once would ask for
/// 19 GiB.
///
/// So the work is bounded rather than the arrivals: a hash waits for a slot,
/// and a wait that goes on too long is refused with an error the caller can
/// turn into "try again in a moment". Waiting a second to sign in is a normal
/// morning; a server that dies at nine o'clock is not.
///
/// The cap is on this process. Rate limiting is still the first line — it
/// stops one address from making a thousand attempts — but a real crowd is a
/// thousand different addresses, and no per-caller limit sees that.
/// </remarks>
public sealed class ThrottledPasswordHasher : IPasswordHasher, IDisposable
{
    private readonly IPasswordHasher _inner;
    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _wait;

    public ThrottledPasswordHasher(
        IPasswordHasher inner, int concurrency, int waitSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);

        _inner = inner;
        _slots = new SemaphoreSlim(concurrency, concurrency);
        _wait = TimeSpan.FromSeconds(waitSeconds);
    }

    // The synchronous pair is for callers outside a request — startup, tooling,
    // the constant-time dummy hash. They are not a crowd, so they are not
    // queued.
    public string Hash(string password) => _inner.Hash(password);

    public bool Verify(string password, string hash) =>
        _inner.Verify(password, hash);

    public Task<string> HashAsync(string password, CancellationToken ct = default) =>
        RunAsync(() => _inner.Hash(password), ct);

    public Task<bool> VerifyAsync(
        string password, string hash, CancellationToken ct = default) =>
        RunAsync(() => _inner.Verify(password, hash), ct);

    private async Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        // The wait is asynchronous on purpose. Blocking here would hold a
        // thread-pool thread for the whole queue, so a burst of sign-ins would
        // starve every other request in the process — the opposite of what a
        // limit is for.
        if (!await _slots.WaitAsync(_wait, ct))
            throw new PasswordHashingBusyException();

        try
        {
            // Off the request thread: this is seconds of solid CPU, and running
            // it inline stalls whatever else that thread was going to do.
            return await Task.Run(work, ct);
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose() => _slots.Dispose();
}

/// <summary>
/// Thrown when a sign-in waited for a hashing slot and did not get one.
/// </summary>
/// <remarks>
/// Deliberately not a failed login: the credentials were never checked, and
/// telling a learner their password is wrong when the server was merely busy is
/// both untrue and the kind of thing that makes people reset a password that
/// worked perfectly well.
/// </remarks>
public sealed class PasswordHashingBusyException : Exception
{
    public PasswordHashingBusyException()
        : base("The server is busy verifying sign-ins. Try again in a moment.")
    {
    }
}
