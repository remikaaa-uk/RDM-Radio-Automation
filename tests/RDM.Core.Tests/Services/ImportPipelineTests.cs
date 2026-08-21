using FluentAssertions;
using Moq;
using RDM.Core.Entities;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Core.Queues;
using RDM.Core.Services;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Core.Tests.Services;

public sealed class ImportPipelineTests : IDisposable
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private readonly string              _tempFilePath;
    private readonly Mock<IAssetRepository> _mockRepo;
    private readonly Mock<IEventBus>        _mockBus;
    private readonly WaveformQueue          _waveformQueue;
    private readonly LoudnessQueue          _loudnessQueue;

    public ImportPipelineTests()
    {
        _tempFilePath  = Path.GetTempFileName();
        File.WriteAllBytes(_tempFilePath, new byte[256]); // dummy content for SHA-256

        _mockRepo      = new Mock<IAssetRepository>();
        _mockBus       = new Mock<IEventBus>();
        _waveformQueue = new WaveformQueue();
        _loudnessQueue = new LoudnessQueue();

        // Defaults: no duplicate, create succeeds, events succeed
        _mockRepo.Setup(r => r.GetByChecksumAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Asset?)null);
        _mockRepo.Setup(r => r.CreateAsync(It.IsAny<Asset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockBus.Setup(b => b.PublishAsync(It.IsAny<AssetCreatedEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        if (File.Exists(_tempFilePath)) File.Delete(_tempFilePath);
        _waveformQueue.Writer.TryComplete();
        _loudnessQueue.Writer.TryComplete();
    }

    private ImportPipeline Build(params IMetadataReader[] readers) =>
        new(readers, _mockRepo.Object, Mock.Of<IRdmFileWriter>(), _waveformQueue, _loudnessQueue, _mockBus.Object,
            new ImportPipelineSettings(Path.GetTempPath()), new ChecksumService());

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_WhenChecksumAlreadyExists_ReturnsDuplicate()
    {
        _mockRepo.Setup(r => r.GetByChecksumAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Asset { AssetId = "existing-id", Title = "Existing", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });

        var result = await Build().ImportAsync(_tempFilePath);

        result.Should().BeOfType<ImportResult.Duplicate>()
            .Which.AssetId.Should().Be("existing-id");
        _mockRepo.Verify(r => r.CreateAsync(It.IsAny<Asset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ImportAsync_WhenCreateThrowsDuplicateAssetException_ReturnsDuplicate()
    {
        // Simulate race condition: precheck returns null, but insert fails (another thread won)
        var winnerAsset = new Asset { AssetId = "winner-id", Title = "Winner", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        _mockRepo.SetupSequence(r => r.GetByChecksumAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Asset?)null)   // initial check
            .ReturnsAsync(winnerAsset);   // lookup after catching DuplicateAssetException

        _mockRepo.Setup(r => r.CreateAsync(It.IsAny<Asset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DuplicateAssetException());

        var result = await Build().ImportAsync(_tempFilePath);

        result.Should().BeOfType<ImportResult.Duplicate>()
            .Which.AssetId.Should().Be("winner-id");
    }

    [Fact]
    public async Task ImportAsync_WhenNoMetadataAvailable_UsesFilenameAsTitle()
    {
        var result = await Build().ImportAsync(_tempFilePath);

        result.Should().BeOfType<ImportResult.Success>()
            .Which.Asset.Title.Should().Be(Path.GetFileNameWithoutExtension(_tempFilePath));
    }

    [Fact]
    public async Task ImportAsync_WhenDurationIsZero_SetsIsDamagedTrue()
    {
        // No reader provides DurationMs → DurationMs = 0 → IsDamaged = true
        var result = await Build().ImportAsync(_tempFilePath);

        result.Should().BeOfType<ImportResult.Success>()
            .Which.Asset.IsDamaged.Should().BeTrue();
    }

    [Fact]
    public async Task ImportAsync_WhenReaderProvidesCuePoints_DoesNotCallSetCuePointsAsync()
    {
        var reader = new Mock<IMetadataReader>();
        reader.Setup(r => r.TryReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssetMetadata
            {
                Title = "Track", DurationMs = 180_000,
                CuePoints =
                [
                    new CuePointData(MarkerType.Intro, 3_000),
                    new CuePointData(MarkerType.Outro, 170_000)
                ]
            });

        var result = await Build(reader.Object).ImportAsync(_tempFilePath);

        result.Should().BeOfType<ImportResult.Success>();
        _mockRepo.Verify(r => r.CreateAsync(It.IsAny<Asset>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ImportAsync_OnSuccess_EnqueuesWaveformAndLoudnessTasks()
    {
        string capturedAssetId = "";
        _mockRepo.Setup(r => r.CreateAsync(It.IsAny<Asset>(), It.IsAny<CancellationToken>()))
            .Callback<Asset, CancellationToken>((a, _) => capturedAssetId = a.AssetId)
            .Returns(Task.CompletedTask);

        await Build().ImportAsync(_tempFilePath);

        _waveformQueue.Reader.TryRead(out var wvfTask).Should().BeTrue();
        wvfTask!.AssetId.Should().Be(capturedAssetId);
        wvfTask.FilePath.Should().Be(_tempFilePath);

        _loudnessQueue.Reader.TryRead(out var loudTask).Should().BeTrue();
        loudTask!.AssetId.Should().Be(capturedAssetId);
        loudTask.FilePath.Should().Be(_tempFilePath);
    }

    [Fact]
    public async Task ImportAsync_OnSuccess_PublishesAssetCreatedEvent()
    {
        var reader = new Mock<IMetadataReader>();
        reader.Setup(r => r.TryReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssetMetadata { Title = "My Track", Artist = "Artist", DurationMs = 120_000 });

        await Build(reader.Object).ImportAsync(_tempFilePath);

        _mockBus.Verify(b => b.PublishAsync(
            It.Is<AssetCreatedEvent>(e => e.Title == "My Track" && e.Artist == "Artist"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ImportAsync_WhenReaderThrows_ContinuesWithNextReader()
    {
        var failReader = new Mock<IMetadataReader>();
        failReader.Setup(r => r.TryReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("reader error"));

        var goodReader = new Mock<IMetadataReader>();
        goodReader.Setup(r => r.TryReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssetMetadata { Title = "From Good Reader", DurationMs = 90_000 });

        var result = await Build(failReader.Object, goodReader.Object).ImportAsync(_tempFilePath);

        result.Should().BeOfType<ImportResult.Success>()
            .Which.Asset.Title.Should().Be("From Good Reader");
    }

    [Theory]
    [InlineData(1800)]   // below MySQL YEAR range
    [InlineData(0)]      // common ID3 garbage
    [InlineData(3000)]   // above range
    public async Task ImportAsync_WhenYearOutOfMySqlRange_StoresNullInsteadOfThrowing(int badYear)
    {
        var reader = new Mock<IMetadataReader>();
        reader.Setup(r => r.TryReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssetMetadata { Title = "T", DurationMs = 120_000, Year = badYear });

        var result = await Build(reader.Object).ImportAsync(_tempFilePath);

        result.Should().BeOfType<ImportResult.Success>()
            .Which.Asset.Year.Should().BeNull();
    }

    [Fact]
    public async Task ImportAsync_WhenYearInRange_IsPreserved()
    {
        var reader = new Mock<IMetadataReader>();
        reader.Setup(r => r.TryReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssetMetadata { Title = "T", DurationMs = 120_000, Year = 2024 });

        var result = await Build(reader.Object).ImportAsync(_tempFilePath);

        result.Should().BeOfType<ImportResult.Success>()
            .Which.Asset.Year.Should().Be(2024);
    }
}
