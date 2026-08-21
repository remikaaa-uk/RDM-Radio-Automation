using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RDM.API.Services;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Infrastructure.FileSystem;
using Xunit;

namespace RDM.API.Tests;

/// <summary>
/// Direct unit tests for <see cref="ScanJobService"/> using a real DI container
/// with a mocked repository and checksum service. These assert the "path first,
/// checksum only on a path miss" ordering — a performance contract the HTTP
/// integration tests cannot observe (they cannot see whether SHA-256 ran).
/// </summary>
public sealed class ScanJobServiceTests : IDisposable
{
    private readonly Mock<IAssetRepository> _repo = new();
    private readonly Mock<IChecksumService> _checksum = new();
    private readonly List<string> _tempFiles = new();

    public ScanJobServiceTests()
    {
        // Default: nothing exists in the library (both lookups miss).
        _repo.Setup(r => r.FindByFilePathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((Asset?)null);
        _repo.Setup(r => r.GetByChecksumAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((Asset?)null);
        // A distinct checksum per path, so path→checksum mapping is unambiguous.
        _checksum.Setup(c => c.ComputeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((string path, CancellationToken _) => "sum:" + path);
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { }
    }

    private string NewTempFile()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[16]);
        _tempFiles.Add(path);
        return path;
    }

    private ScanJobService BuildService()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _repo.Object);
        services.AddScoped<IMetadataReader, Id3TagReader>();
        var provider = services.BuildServiceProvider();
        return new ScanJobService(provider, _checksum.Object, Mock.Of<Microsoft.Extensions.Logging.ILogger<ScanJobService>>());
    }

    private static async Task<ScanJob> RunAsync(ScanJobService svc, IReadOnlyList<string> files)
    {
        await svc.StartAsync(CancellationToken.None);
        try
        {
            var id = svc.Enqueue(files);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var job = svc.GetJob(id)!;
                if (job.Status is "COMPLETED" or "FAILED") return job;
                await Task.Delay(50);
            }
            throw new TimeoutException("Scan did not complete.");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenFileExistsByPath_ChecksumIsNeverComputed_AndFileIsSkipped()
    {
        var path = NewTempFile();
        _repo.Setup(r => r.FindByFilePathAsync(path, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new Asset { AssetId = "a", Title = "A", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });

        var job = await RunAsync(BuildService(), new[] { path });

        job.Status.Should().Be("COMPLETED");
        job.Results.Should().BeEmpty();
        // The core performance contract: no SHA-256 on files already tracked by path.
        _checksum.Verify(c => c.ComputeAsync(path, It.IsAny<CancellationToken>()), Times.Never);
        _repo.Verify(r => r.GetByChecksumAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhenFileMissesPathButMatchesChecksum_ChecksumIsComputed_AndFileIsSkipped()
    {
        var path = NewTempFile();
        _repo.Setup(r => r.GetByChecksumAsync("sum:" + path, It.IsAny<CancellationToken>()))
             .ReturnsAsync(new Asset { AssetId = "b", Title = "B", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });

        var job = await RunAsync(BuildService(), new[] { path });

        job.Status.Should().Be("COMPLETED");
        job.Results.Should().BeEmpty();
        _checksum.Verify(c => c.ComputeAsync(path, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenFileIsNew_ItAppearsInResults_WithFilenameAndFolder()
    {
        var path = NewTempFile();

        var job = await RunAsync(BuildService(), new[] { path });

        job.Status.Should().Be("COMPLETED");
        var only = job.Results.Should().ContainSingle().Subject;
        only.FilePath.Should().Be(path);
        only.Filename.Should().Be(Path.GetFileName(path));
        only.Folder.Should().Be(Path.GetDirectoryName(path));
        // Metadata read fails on a non-audio temp file → title falls back to the file name.
        only.Title.Should().Be(Path.GetFileNameWithoutExtension(path));
    }

    [Fact]
    public async Task WhenOneFileThrows_RemainingFilesStillProcessed_AndCountersComplete()
    {
        var bad  = NewTempFile();
        var good = NewTempFile();
        _checksum.Setup(c => c.ComputeAsync(bad, It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new IOException("locked"));

        var job = await RunAsync(BuildService(), new[] { bad, good });

        job.Status.Should().Be("COMPLETED");
        job.Done.Should().Be(2);
        job.Total.Should().Be(2);
        // The good file survives; the throwing one is silently skipped.
        job.Results.Should().ContainSingle().Which.FilePath.Should().Be(good);
    }
}
