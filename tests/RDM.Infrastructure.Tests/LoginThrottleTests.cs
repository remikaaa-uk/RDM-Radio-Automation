using FluentAssertions;
using RDM.Infrastructure;
using Xunit;

namespace RDM.Infrastructure.Tests;

/// <summary>
/// Pure unit tests for <see cref="LoginThrottle"/> — no database, deterministic via an injected clock.
/// </summary>
public sealed class LoginThrottleTests
{
    private DateTimeOffset _now = new(2026, 07, 14, 12, 00, 00, TimeSpan.Zero);
    private readonly TimeSpan _lockout = TimeSpan.FromMinutes(15);

    private LoginThrottle Create(int maxAttempts = 3) =>
        new(maxAttempts, _lockout, () => _now);

    private const string Key = "studio-1|operator";

    [Fact]
    public void NotLockedOut_BeforeThreshold()
    {
        var sut = Create(maxAttempts: 3);

        sut.RegisterFailure(Key);
        sut.RegisterFailure(Key); // 2 of 3

        sut.IsLockedOut(Key, out _).Should().BeFalse();
    }

    [Fact]
    public void LocksOut_OnceThresholdReached()
    {
        var sut = Create(maxAttempts: 3);

        sut.RegisterFailure(Key);
        sut.RegisterFailure(Key);
        sut.RegisterFailure(Key); // hits threshold

        sut.IsLockedOut(Key, out var retryAfter).Should().BeTrue();
        retryAfter.Should().Be(_lockout);
    }

    [Fact]
    public void Reset_ClearsFailures()
    {
        var sut = Create(maxAttempts: 3);
        sut.RegisterFailure(Key);
        sut.RegisterFailure(Key);

        sut.Reset(Key);

        sut.RegisterFailure(Key); // fresh count = 1
        sut.IsLockedOut(Key, out _).Should().BeFalse();
    }

    [Fact]
    public void Lockout_ExpiresAfterCooldown()
    {
        var sut = Create(maxAttempts: 3);
        sut.RegisterFailure(Key);
        sut.RegisterFailure(Key);
        sut.RegisterFailure(Key);
        sut.IsLockedOut(Key, out _).Should().BeTrue();

        _now += _lockout + TimeSpan.FromSeconds(1); // cooldown elapses

        sut.IsLockedOut(Key, out _).Should().BeFalse();
    }

    [Fact]
    public void StaleFailure_StartsFreshWindow()
    {
        var sut = Create(maxAttempts: 3);
        sut.RegisterFailure(Key); // count = 1
        sut.RegisterFailure(Key); // count = 2

        _now += _lockout + TimeSpan.FromSeconds(1); // older than the window

        sut.RegisterFailure(Key); // window reset → count = 1, not 3
        sut.IsLockedOut(Key, out _).Should().BeFalse();
    }

    [Fact]
    public void Keys_AreIndependent()
    {
        var sut = Create(maxAttempts: 3);
        for (int i = 0; i < 3; i++) sut.RegisterFailure("studio-1|alice");

        sut.IsLockedOut("studio-1|alice", out _).Should().BeTrue();
        sut.IsLockedOut("studio-1|bob", out _).Should().BeFalse();
    }
}
