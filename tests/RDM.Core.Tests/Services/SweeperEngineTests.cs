using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RDM.Core.Entities;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Core.Tests.Services;

public sealed class SweeperEngineTests : IAsyncDisposable
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private readonly Mock<IAudioEngine>             _audioEngine  = new();
    private readonly InMemoryEventBus               _eventBus     = new();
    private readonly Mock<IAssetRepository>         _assetRepo    = new();
    private readonly Mock<IAudioSettingsRepository> _settingsRepo = new();
    private readonly StudioContext                  _studioCtx    = new();
    private readonly SweeperEngine                  _engine;

    private const string StudioId  = "studio-1";
    private const string FormatId  = "format-sweeper";
    private const string PrevId    = "prev-asset";
    private const string CurrentId = "current-asset";
    private const string SweepId   = "sweep-asset";
    private const string SweepPath = @"C:\sweepers\jingle.mp3";

    public SweeperEngineTests()
    {
        _studioCtx.Initialize(StudioId);
        SetupDefaultSettings(sweeperEnabled: true, formatId: FormatId, minIntroMs: 5_000);
        SetupDefaultAudioEngine();
        SetupPreviousTrack();

        _engine = new SweeperEngine(
            _audioEngine.Object,
            _eventBus,
            _assetRepo.Object,
            _settingsRepo.Object,
            _studioCtx,
            NullLogger<SweeperEngine>.Instance);
    }

    public async ValueTask DisposeAsync() => await _engine.DisposeAsync();

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SweeperEngine_SchedulesToEndAtIntroCue()
    {
        // Arrange — current track intro = 34 000ms, sweeper = 8 000ms.
        // The sweeper must end exactly at the intro cue → start = 34 000 - 8 000 = 26 000ms.
        SetupCurrentTrack(introMs: 34_000);
        SetupSweeper(SweepId, SweepPath, durationMs: 8_000);

        // Act — TRACK→TRACK: previous track first, then the current track.
        await FireTrackStartedAsync(PrevId);
        await FireTrackStartedAsync(CurrentId);

        // Assert
        _audioEngine.Verify(e => e.ScheduleSweeperAsync(
            26_000L, SweepPath, It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SweeperEngine_PassesDuckingValueFromSettings()
    {
        // Arrange — ducking configured to 9 dB should be forwarded to the audio engine.
        SetupDefaultSettings(sweeperEnabled: true, formatId: FormatId, minIntroMs: 5_000, duckingDb: 9f);
        SetupCurrentTrack(introMs: 34_000);
        SetupSweeper(SweepId, SweepPath, durationMs: 8_000);

        // Act
        await FireTrackStartedAsync(PrevId);
        await FireTrackStartedAsync(CurrentId);

        // Assert
        _audioEngine.Verify(e => e.ScheduleSweeperAsync(
            It.IsAny<long>(), SweepPath, 9f, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SweeperEngine_SkipsWhenPreviousNotTrack()
    {
        // Arrange — first event of the session: no previous track yet.
        SetupCurrentTrack(introMs: 34_000);
        SetupSweeper(SweepId, SweepPath, durationMs: 8_000);

        // Act — only the current track fires (previous is null → not TRACK→TRACK).
        await FireTrackStartedAsync(CurrentId);

        // Assert
        _audioEngine.Verify(e => e.ScheduleSweeperAsync(
            It.IsAny<long>(), It.IsAny<string>(), It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SweeperEngine_SkipsWhenSweeperDisabled()
    {
        // Arrange
        SetupDefaultSettings(sweeperEnabled: false, formatId: FormatId, minIntroMs: 5_000);
        SetupCurrentTrack(introMs: 34_000);
        SetupSweeper(SweepId, SweepPath, durationMs: 8_000);

        // Act
        await FireTrackStartedAsync(PrevId);
        await FireTrackStartedAsync(CurrentId);

        // Assert
        _audioEngine.Verify(e => e.ScheduleSweeperAsync(
            It.IsAny<long>(), It.IsAny<string>(), It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SweeperEngine_SkipsWhenNoSweeperAvailable()
    {
        // Arrange
        SetupCurrentTrack(introMs: 34_000);
        _assetRepo
            .Setup(r => r.GetByFormatAsync(FormatId, AssetStatus.Active, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Asset>());

        // Act
        await FireTrackStartedAsync(PrevId);
        await FireTrackStartedAsync(CurrentId);

        // Assert
        _audioEngine.Verify(e => e.ScheduleSweeperAsync(
            It.IsAny<long>(), It.IsAny<string>(), It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SweeperEngine_SkipsWhenIntroTooShort()
    {
        // Arrange — current track intro (3 000ms) < sweeper_min_intro_ms (5 000ms)
        SetupCurrentTrack(introMs: 3_000);
        SetupSweeper(SweepId, SweepPath, durationMs: 2_000);

        // Act
        await FireTrackStartedAsync(PrevId);
        await FireTrackStartedAsync(CurrentId);

        // Assert
        _audioEngine.Verify(e => e.ScheduleSweeperAsync(
            It.IsAny<long>(), It.IsAny<string>(), It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SweeperEngine_SkipsWhenSweeperLongerThanIntro()
    {
        // Arrange — sweeper (20 000ms) does not fit within the intro (6 000ms)
        SetupCurrentTrack(introMs: 6_000);
        SetupSweeper(SweepId, SweepPath, durationMs: 20_000);

        // Act
        await FireTrackStartedAsync(PrevId);
        await FireTrackStartedAsync(CurrentId);

        // Assert
        _audioEngine.Verify(e => e.ScheduleSweeperAsync(
            It.IsAny<long>(), It.IsAny<string>(), It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SweeperEngine_SkipsWhenCurrentHasNoIntroCue()
    {
        // Arrange — current track has no intro cue → no sweeper window
        SetupCurrentTrack(introMs: null);
        SetupSweeper(SweepId, SweepPath, durationMs: 8_000);

        // Act
        await FireTrackStartedAsync(PrevId);
        await FireTrackStartedAsync(CurrentId);

        // Assert
        _audioEngine.Verify(e => e.ScheduleSweeperAsync(
            It.IsAny<long>(), It.IsAny<string>(), It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task FireTrackStartedAsync(string assetId)
    {
        await _eventBus.PublishAsync(
            new TrackStartedEvent(assetId, null, null, 0), CancellationToken.None);
        await Task.Delay(150);
    }

    private void SetupDefaultSettings(
        bool sweeperEnabled, string? formatId, uint minIntroMs, float duckingDb = 6f)
    {
        _settingsRepo
            .Setup(r => r.GetByStudioAsync(StudioId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AudioSettings
            {
                SettingsId        = "s1",
                StudioId          = StudioId,
                SweeperEnabled    = sweeperEnabled,
                SweeperFormatId   = formatId,
                SweeperMinIntroMs = minIntroMs,
                SweeperDuckingDb  = duckingDb,
                SampleRate        = AudioSampleRate.Hz48000,
                BufferSize        = AudioBufferSize.Samples512,
                OutputMode        = DriverType.WasapiExclusive
            });
    }

    private void SetupDefaultAudioEngine()
    {
        _audioEngine
            .Setup(e => e.ScheduleSweeperAsync(
                It.IsAny<long>(), It.IsAny<string>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    // A prior music track so the TRACK→TRACK rule is satisfied on the second event.
    private void SetupPreviousTrack()
    {
        _assetRepo
            .Setup(r => r.GetByIdAsync(PrevId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeAsset(PrevId, AssetType.Track, 240_000));
    }

    private void SetupCurrentTrack(uint? introMs)
    {
        _assetRepo
            .Setup(r => r.GetByIdAsync(CurrentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Asset
            {
                AssetId    = CurrentId,
                AssetType  = AssetType.Track,
                Title      = $"Asset {CurrentId}",
                DurationMs = 240_000,
                Checksum   = $"cs-{CurrentId}",
                CueIntro   = introMs.HasValue ? introMs.Value / 1000.0 : null,
                Status     = AssetStatus.Active,
                CreatedAt  = DateTime.UtcNow,
                UpdatedAt  = DateTime.UtcNow
            });
    }

    private void SetupSweeper(string assetId, string filePath, uint durationMs)
    {
        _assetRepo
            .Setup(r => r.GetByFormatAsync(FormatId, AssetStatus.Active, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeAsset(assetId, AssetType.Sweeper, durationMs, filePath) });
    }

    private static Asset MakeAsset(
        string assetId, AssetType type, uint durationMs, string? path = null) =>
        new()
        {
            AssetId     = assetId,
            AssetType   = type,
            Title       = $"Asset {assetId}",
            DurationMs  = durationMs,
            Checksum    = $"cs-{assetId}",
            RdmFilePath = path,
            Status      = AssetStatus.Active,
            CreatedAt   = DateTime.UtcNow,
            UpdatedAt   = DateTime.UtcNow
        };
}
