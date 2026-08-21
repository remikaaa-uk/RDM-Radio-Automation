using FluentAssertions;
using RDM.Infrastructure.Security;
using Xunit;

namespace RDM.Infrastructure.Tests;

/// <summary>
/// Exercises real Windows DPAPI — no mocks. The blobs are machine-bound, so these tests only
/// prove the round-trip on the machine that runs them, which is exactly the guarantee we need.
/// </summary>
public sealed class SecretProtectorTests
{
    private readonly SecretProtector _sut = new();

    [Fact]
    public void ProtectThenUnprotect_ShouldReturnOriginal()
    {
        const string secret = "hasło-do-serwera-123!@#";

        var cipher = _sut.Protect(secret);
        var result = _sut.Unprotect(cipher);

        result.Should().Be(secret);
    }

    [Fact]
    public void Protect_ShouldNotLeavePlaintextInTheBlob()
    {
        const string secret = "SuperTajneHaslo";

        var cipher = _sut.Protect(secret)!;

        System.Text.Encoding.UTF8.GetString(cipher).Should().NotContain(secret);
    }

    [Fact]
    public void ProtectThenUnprotect_ShouldSurviveNonAsciiAndLongInput()
    {
        var secret = new string('ż', 500) + "🎙";

        _sut.Unprotect(_sut.Protect(secret)).Should().Be(secret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Protect_ShouldReturnNullForEmptyInput(string? input)
        => _sut.Protect(input).Should().BeNull();

    [Fact]
    public void Unprotect_ShouldReturnNullForNullOrEmptyBlob()
    {
        _sut.Unprotect(null).Should().BeNull();
        _sut.Unprotect(Array.Empty<byte>()).Should().BeNull();
    }

    [Fact]
    public void Unprotect_ShouldThrowOnCorruptBlob()
    {
        var act = () => _sut.Unprotect(new byte[] { 1, 2, 3, 4, 5 });

        act.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }
}
