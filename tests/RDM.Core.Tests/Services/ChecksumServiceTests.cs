using System.Security.Cryptography;
using FluentAssertions;
using RDM.Core.Services;
using Xunit;

namespace RDM.Core.Tests.Services;

public sealed class ChecksumServiceTests : IDisposable
{
    private readonly string _tempFilePath;

    public ChecksumServiceTests()
    {
        _tempFilePath = Path.GetTempFileName();
    }

    public void Dispose()
    {
        if (File.Exists(_tempFilePath)) File.Delete(_tempFilePath);
    }

    [Fact]
    public async Task ComputeAsync_ReturnsLowercaseHexSha256OfFileContent()
    {
        var content = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        await File.WriteAllBytesAsync(_tempFilePath, content);

        var expected = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        var checksum = await new ChecksumService().ComputeAsync(_tempFilePath);

        checksum.Should().Be(expected);
        checksum.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public async Task ComputeAsync_IsDeterministic_ForSameContent()
    {
        await File.WriteAllBytesAsync(_tempFilePath, new byte[] { 42, 42, 42 });
        var service = new ChecksumService();

        var first  = await service.ComputeAsync(_tempFilePath);
        var second = await service.ComputeAsync(_tempFilePath);

        first.Should().Be(second);
    }

    [Fact]
    public async Task ComputeAsync_ProducesDifferentChecksums_ForDifferentContent()
    {
        var otherFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(_tempFilePath, new byte[] { 1, 1, 1 });
            await File.WriteAllBytesAsync(otherFile,     new byte[] { 2, 2, 2 });
            var service = new ChecksumService();

            var a = await service.ComputeAsync(_tempFilePath);
            var b = await service.ComputeAsync(otherFile);

            a.Should().NotBe(b);
        }
        finally
        {
            if (File.Exists(otherFile)) File.Delete(otherFile);
        }
    }
}
