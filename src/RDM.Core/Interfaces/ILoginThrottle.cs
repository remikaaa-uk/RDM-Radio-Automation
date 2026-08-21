namespace RDM.Core.Interfaces;

/// <summary>
/// In-memory brute-force guard for login. Tracks consecutive failed attempts per identity
/// and locks that identity out for a cooldown once a threshold is exceeded.
///
/// MUST be registered as a singleton — counters have to survive across scoped requests
/// (AuthService is scoped in the API host).
/// </summary>
public interface ILoginThrottle
{
    /// <summary>
    /// True if the identity is currently locked out. When true, <paramref name="retryAfter"/>
    /// carries the remaining cooldown.
    /// </summary>
    bool IsLockedOut(string key, out TimeSpan retryAfter);

    /// <summary>Record a failed authentication attempt for the identity.</summary>
    void RegisterFailure(string key);

    /// <summary>Clear all state for the identity (call after a successful authentication).</summary>
    void Reset(string key);
}
