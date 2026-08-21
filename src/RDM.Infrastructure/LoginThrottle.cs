using RDM.Core.Interfaces;

namespace RDM.Infrastructure;

/// <summary>
/// Thread-safe, in-memory <see cref="ILoginThrottle"/>. Login volume is tiny, so a single
/// gate around a dictionary is both correct and simplest.
///
/// Policy: after <c>maxAttempts</c> consecutive failures the identity is locked for
/// <c>lockoutDuration</c>. A successful login (<see cref="Reset"/>) clears the counter, and a
/// failure that arrives more than <c>lockoutDuration</c> after the previous one starts a fresh
/// window (so counters do not accumulate forever).
/// </summary>
public sealed class LoginThrottle : ILoginThrottle
{
    private readonly int _maxAttempts;
    private readonly TimeSpan _lockoutDuration;
    private readonly Func<DateTimeOffset> _clock;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="maxAttempts">Consecutive failures that trigger a lockout (default 5).</param>
    /// <param name="lockoutDuration">Cooldown once locked, and the sliding-window size (default 15 min).</param>
    /// <param name="clock">Time source; overridable for tests. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public LoginThrottle(int maxAttempts = 5, TimeSpan? lockoutDuration = null, Func<DateTimeOffset>? clock = null)
    {
        _maxAttempts     = maxAttempts;
        _lockoutDuration = lockoutDuration ?? TimeSpan.FromMinutes(15);
        _clock           = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public bool IsLockedOut(string key, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var e) && e.LockoutUntil is { } until)
            {
                var now = _clock();
                if (until > now)
                {
                    retryAfter = until - now;
                    return true;
                }
                // Cooldown elapsed — drop the stale entry so the next attempt starts clean.
                _entries.Remove(key);
            }
            return false;
        }
    }

    public void RegisterFailure(string key)
    {
        lock (_gate)
        {
            var now = _clock();
            if (!_entries.TryGetValue(key, out var e) || now - e.LastFailure > _lockoutDuration)
            {
                e = new Entry();
            }

            e.Count++;
            e.LastFailure = now;
            if (e.Count >= _maxAttempts)
            {
                e.LockoutUntil = now + _lockoutDuration;
            }

            _entries[key] = e;
        }
    }

    public void Reset(string key)
    {
        lock (_gate)
        {
            _entries.Remove(key);
        }
    }

    private sealed class Entry
    {
        public int Count;
        public DateTimeOffset LastFailure;
        public DateTimeOffset? LockoutUntil;
    }
}
