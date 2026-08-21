using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RDM.Core.Entities;
using RDM.Core.Models;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Core.Queues;
using RDM.Core.Services;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Core.Tests.Services;

public sealed class PlaylistEngineTests : IAsyncDisposable
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private readonly Mock<IAudioEngine>             _audioEngine  = new();
    private readonly InMemoryEventBus               _eventBus     = new();
    private readonly Mock<IPlaylistRepository>      _playlistRepo = new();
    private readonly Mock<IAssetRepository>         _assetRepo    = new();
    private readonly Mock<IAudioSettingsRepository> _settingsRepo = new();
    private readonly StudioContext                  _studioCtx    = new();
    private readonly WaveformQueue                  _waveformQueue = new();
    private readonly CueAnalysisQueue               _cueAnalysisQueue = new();
    private readonly PlaylistEngine                 _engine;

    private const string StudioId    = "studio-1";
    private const string PlaylistId  = "playlist-1";
    private const string Item1Id     = "item-1";
    private const string Item2Id     = "item-2";
    private const string Asset1Id    = "asset-1";
    private const string Asset2Id    = "asset-2";
    private const string Asset1Path  = @"C:\music\track1.mp3";
    private const string Asset2Path  = @"C:\music\track2.mp3";

    public PlaylistEngineTests()
    {
        _studioCtx.Initialize(StudioId);

        SetupDefaultSettings(PlaylistMode.Auto);
        SetupDefaultAudioEngine();

        _engine = new PlaylistEngine(
            _audioEngine.Object,
            _eventBus,
            _playlistRepo.Object,
            _assetRepo.Object,
            _settingsRepo.Object,
            _studioCtx,
            _waveformQueue,
            _cueAnalysisQueue,
            NullLogger<PlaylistEngine>.Instance);
    }

    public async ValueTask DisposeAsync() => await _engine.DisposeAsync();

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlaylistEngine_AutoMode_AdvancesTrack_OnStartNextReached()
    {
        // Arrange
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        await _engine.InitializeAsync();              // mode = AUTO
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();                    // plays asset-1

        // Act — simulate StartNext marker reached on asset-1 (no cue markers → no fade delay)
        await _eventBus.PublishAsync(
            new TrackStartNextReachedEvent(Asset1Id), CancellationToken.None);

        await Task.Delay(500); // let fire-and-forget tasks complete

        // Assert — engine advanced to asset-2
        _audioEngine.Verify(e => e.CancelScheduledSweepersAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset2Id, Asset2Path,
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _audioEngine.Verify(e => e.PlayAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2)); // once for asset-1, once for asset-2
    }

    [Fact]
    public async Task PlaylistEngine_AutoMode_Crossfades_WhenCrossfadeConfigured()
    {
        // Arrange — incoming item (asset-2) carries the crossfade duration
        SetupCrossfadePlaylist(crossfadeMs: 4000);
        _audioEngine
            .Setup(e => e.CrossfadeToAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<uint>(), It.IsAny<uint>(), It.IsAny<uint>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync(); // plays asset-1 (hard start)

        // Act — StartNext on asset-1
        await _eventBus.PublishAsync(
            new TrackStartNextReachedEvent(Asset1Id), CancellationToken.None);
        await Task.Delay(500);

        // Assert — asset-2 enters via an overlapping crossfade, not a hard load
        _audioEngine.Verify(e => e.CrossfadeToAsync(
                Asset2Id, Asset2Path,
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<uint>(), 4000u, 0u,
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset2Id, It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _audioEngine.Verify(e => e.PlayAsync(It.IsAny<CancellationToken>()),
            Times.Once); // only the initial asset-1 start; crossfade starts asset-2 itself
    }

    [Fact]
    public async Task PlaylistEngine_LiveAssist_DoesNotAdvance_WithoutCommand()
    {
        // Arrange
        SetupDefaultSettings(PlaylistMode.LiveAssist);
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();

        // Act
        await _eventBus.PublishAsync(
            new TrackOutroReachedEvent(Asset1Id, 5000), CancellationToken.None);

        await Task.Delay(300);

        // Assert — no automatic advance to asset-2
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset2Id, It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _audioEngine.Verify(e => e.PlayAsync(It.IsAny<CancellationToken>()),
            Times.Once); // only the initial PlayAsync call
    }

    [Fact]
    public async Task PlaylistEngine_Manual_NeverAutoAdvances()
    {
        // Arrange
        SetupDefaultSettings(PlaylistMode.Manual);
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();

        // Act — outro fires
        await _eventBus.PublishAsync(
            new TrackOutroReachedEvent(Asset1Id, 3000), CancellationToken.None);
        // and even when track fully ends
        await _eventBus.PublishAsync(
            new TrackEndedEvent(Asset1Id, "NATURAL"), CancellationToken.None);

        await Task.Delay(300);

        // Assert — no automatic advance, no second load
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset2Id, It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _audioEngine.Verify(e => e.PlayAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Repeat current track (loop) ───────────────────────────────────────────
    //
    // Loop must hold in AUTO too, so each of the four automatic exits from a track gets its
    // own test: StartNext, FadeOut, CueEnd and the natural end.

    [Fact]
    public async Task PlaylistEngine_Loop_DoesNotAdvance_OnStartNextReached_InAuto()
    {
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        await _engine.InitializeAsync();              // mode = AUTO
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();                    // plays asset-1
        await _engine.SetLoopCurrentAsync(true);

        await _eventBus.PublishAsync(
            new TrackStartNextReachedEvent(Asset1Id), CancellationToken.None);
        await Task.Delay(300);

        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset2Id, It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PlaylistEngine_Loop_DoesNotFadeOut_OnFadeOutMarker()
    {
        // A looping track that faded its own tail would go silent on every pass.
        SetupCrossfadePlaylist(3000);
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();
        await _engine.SetLoopCurrentAsync(true);

        await _eventBus.PublishAsync(
            new TrackFadeOutReachedEvent(Asset1Id), CancellationToken.None);
        await Task.Delay(300);

        _audioEngine.Verify(e => e.FadeOutAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PlaylistEngine_Loop_DoesNotAdvanceOrStop_OnCueEndReached()
    {
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();
        await _engine.SetLoopCurrentAsync(true);

        await _eventBus.PublishAsync(
            new TrackCueEndReachedEvent(Asset1Id), CancellationToken.None);
        await Task.Delay(300);

        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset2Id, It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        // CueEnd is the loop point — the engine rewound there, so nothing may stop the stream.
        _audioEngine.Verify(e => e.StopAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PlaylistEngine_Loop_DoesNotDequeue_OnNaturalEnd()
    {
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();
        await _engine.SetLoopCurrentAsync(true);

        await _eventBus.PublishAsync(
            new TrackEndedEvent(Asset1Id, "NATURAL"), CancellationToken.None);
        await Task.Delay(300);

        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset2Id, It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // The looping item is still the current one, not retired from the queue.
        var info = _engine.GetNowPlayingInfo();
        info.CurrentItemId.Should().Be(Item1Id);
        info.LoopCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task PlaylistEngine_Loop_ArmedBeforePlay_IsAppliedWithTheTracksCueRegion()
    {
        // Regression: the toggle used to be a no-op with nothing loaded, and the flag was lost
        // again on every load — so pressing Loop and then Play never looped anything.
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        SetupAsset(Asset1Id, Asset1Path, cueStart: 1.5, cueEnd: 200.0);
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);

        await _engine.SetLoopCurrentAsync(true);      // nothing playing yet
        await _engine.PlayAsync();
        await Task.Delay(300);

        _audioEngine.Verify(e => e.SetPlayerLoopAsync(
                true, 1500u, 200_000u, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlaylistEngine_LoopSwitchedOff_AdvancesAgain_OnStartNextReached()
    {
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();

        await _engine.SetLoopCurrentAsync(true);
        await _engine.SetLoopCurrentAsync(false);

        await _eventBus.PublishAsync(
            new TrackStartNextReachedEvent(Asset1Id), CancellationToken.None);
        await Task.Delay(300);

        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset2Id, Asset2Path,
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlaylistEngine_Loop_RewindsReportedPosition_OnEachPass()
    {
        // Position is wall-clock elapsed since the track started. A looping track never ends and
        // gets no second TrackStarted, so without handling the rewind the reported position keeps
        // running past the end of the track — the playhead freezes after the first pass.
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        SetupAsset(Asset1Id, Asset1Path, cueStart: 12.59);
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();
        await _eventBus.PublishAsync(
            new TrackStartedEvent(Asset1Id, "Track 1", "Artist", 240_000), CancellationToken.None);
        await _engine.SetLoopCurrentAsync(true);
        // Long enough that a position which failed to rewind is unmistakably out of range.
        await Task.Delay(1200);

        await _eventBus.PublishAsync(
            new PlayerLoopedEvent(Asset1Id, 12_590), CancellationToken.None);
        await Task.Delay(100);

        // Back at the cue-in, not at cue-in + everything that already elapsed (~13 800).
        _engine.GetNowPlayingInfo().PositionMs.Should().BeInRange(12_590, 13_000);
    }

    [Fact]
    public async Task PlaylistEngine_Loop_ReportsThePositionTheEngineRewoundTo()
    {
        // Pins the position to the loop point the engine reports, not to wherever the track
        // happened to start: the two differ once a pause has folded elapsed time into the total.
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        SetupAsset(Asset1Id, Asset1Path, cueStart: 12.59);
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();
        await _eventBus.PublishAsync(
            new TrackStartedEvent(Asset1Id, "Track 1", "Artist", 240_000), CancellationToken.None);
        await _engine.SetLoopCurrentAsync(true);
        await Task.Delay(100);

        await _eventBus.PublishAsync(
            new PlayerLoopedEvent(Asset1Id, 5_000), CancellationToken.None);
        await Task.Delay(100);

        _engine.GetNowPlayingInfo().PositionMs.Should().BeInRange(5_000, 5_400);
    }

    [Fact]
    public async Task PlaylistEngine_Loop_DoesNotBlockManualStop()
    {
        // Loop suppresses the automatic exits only — an operator Stop must still retire the item.
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();
        await _eventBus.PublishAsync(
            new TrackStartedEvent(Asset1Id, "Track 1", "Artist", 240_000), CancellationToken.None);
        await Task.Delay(100);
        await _engine.SetLoopCurrentAsync(true);

        await _engine.StopAsync();
        await Task.Delay(200);

        var info = _engine.GetNowPlayingInfo();
        info.State.Should().Be(SessionState.Idle);
        info.CurrentItemId.Should().BeNull();
        // The toggle itself survives Stop — it is a player-level setting.
        info.LoopCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task PlaylistEngine_LiveAssist_PlayAfterTrackEnded_PlaysNextTrack()
    {
        // Arrange — bug fix: after natural track end, PlayAsync must load the NEXT track, not the same one
        SetupDefaultSettings(PlaylistMode.LiveAssist);
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync(); // plays asset-1

        // Simulate TrackStarted so engine knows asset-1 is current
        await _eventBus.PublishAsync(
            new TrackStartedEvent(Asset1Id, "Track 1", "Artist", 240_000), CancellationToken.None);
        await Task.Delay(100);

        // Track finishes naturally
        await _eventBus.PublishAsync(
            new TrackEndedEvent(Asset1Id, "NATURAL"), CancellationToken.None);
        await Task.Delay(200);

        // Act — operator presses Start again
        await _engine.PlayAsync();

        // Assert — should load and play asset-2, not asset-1 again
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset2Id, Asset2Path,
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset1Id, It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once); // only the initial load, not a second time
    }

    // ── Variable-duration assets (e.g. news bulletins re-recorded under the same path) ──

    [Fact]
    public async Task PlaylistEngine_VariableDurationAsset_UsesLiveDetectedEnd_OverridingStaleDbValue()
    {
        // Arrange — asset-1 carries a stale CueEnd (30s, from a previous shorter recording) and
        // is flagged IsVariableDuration; live analysis of the (now longer) file detects End=58.2s
        // and a real duration of 61s (the DB's DurationMs, from SetupAsset's default, stays 240s).
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        SetupAsset(Asset1Id, Asset1Path, cueEnd: 30.0, isVariableDuration: true);
        SetupAsset(Asset2Id, Asset2Path);

        _audioEngine
            .Setup(e => e.AnalyzeCuePointsAsync(
                Asset1Path,
                It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(((double?)0.0, (double?)null, (double?)58.2, (double?)61.0));

        PlaylistItemStartedEvent? started = null;
        _eventBus.Subscribe<PlaylistItemStartedEvent>(e => started = e);

        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);

        // Act
        await _engine.PlayAsync();
        await _eventBus.PublishAsync(
            new TrackStartedEvent(Asset1Id, "Track 1", "Artist", 240_000), CancellationToken.None);
        await Task.Delay(100);

        // Assert — live analysis was actually consulted for the flagged asset...
        _audioEngine.Verify(e => e.AnalyzeCuePointsAsync(
                Asset1Path,
                It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // ...and its freshly detected End (58200ms) — not the stale DB value (30000ms) — was
        // registered as the End cue for this load.
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset1Id, Asset1Path,
                It.Is<IReadOnlyList<AssetCuePoint>>(cues =>
                    cues.Any(c => c.MarkerType == MarkerType.End && c.PositionMs == 58_200) &&
                    !cues.Any(c => c.MarkerType == MarkerType.End && c.PositionMs == 30_000)),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // ...the published DurationMs reflects the live-measured 61000ms, not the stale 240000ms
        // from the asset row...
        started.Should().NotBeNull();
        started!.DurationMs.Should().Be(61_000u);

        // ...and a waveform regeneration task was enqueued for the current file content.
        _waveformQueue.Reader.Count.Should().Be(1);
        _waveformQueue.Reader.TryRead(out var waveformTask).Should().BeTrue();
        waveformTask!.AssetId.Should().Be(Asset1Id);
        waveformTask.FilePath.Should().Be(Asset1Path);
    }

    [Fact]
    public async Task PlaylistEngine_NonVariableDurationAsset_NeverCallsLiveAnalysis_UsesStoredCueEnd()
    {
        // Arrange — same stale CueEnd, but the asset is NOT flagged as variable-duration.
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        SetupAsset(Asset1Id, Asset1Path, cueEnd: 30.0, isVariableDuration: false);
        SetupAsset(Asset2Id, Asset2Path);

        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);

        // Act
        await _engine.PlayAsync();

        // Assert — live analysis is never consulted for a non-flagged asset...
        _audioEngine.Verify(e => e.AnalyzeCuePointsAsync(
                It.IsAny<string>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // ...and the stored DB CueEnd (30000ms) is used as-is.
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset1Id, Asset1Path,
                It.Is<IReadOnlyList<AssetCuePoint>>(cues =>
                    cues.Any(c => c.MarkerType == MarkerType.End && c.PositionMs == 30_000)),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        // ...and no waveform regeneration was queued for a non-flagged asset.
        _waveformQueue.Reader.Count.Should().Be(0);
    }

    // ── Queue swap while a track is playing (CLEAR/LOAD from a scheduled event) ──

    [Fact]
    public async Task PlaylistEngine_AutoMode_QueueSwapWhilePlaying_FinishesCurrentThenPlaysNewQueueHead()
    {
        // Arrange — AUTO, asset-1 on air from the initial queue
        const string Playlist2 = "playlist-2";
        const string Item3 = "item-3", Asset3 = "asset-3";
        const string Item4 = "item-4", Asset4 = "asset-4";

        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        SetupPlaylist(Playlist2, Item3, Asset3, Item4, Asset4);
        await _engine.InitializeAsync();               // mode = AUTO
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();                      // plays asset-1
        await _eventBus.PublishAsync(
            new TrackStartedEvent(Asset1Id, "Track 1", "Artist", 240_000), CancellationToken.None);
        await Task.Delay(100);                          // engine marks asset-1 as Playing

        // Act — a scheduled event swaps in a whole new queue while asset-1 is still on air
        await _engine.LoadPlaylistAsync(Playlist2);

        // Assert — the swap must NOT interrupt the current track
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset3, It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _audioEngine.Verify(e => e.StopAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Act — asset-1 finishes naturally
        await _eventBus.PublishAsync(
            new TrackEndedEvent(Asset1Id, "NATURAL"), CancellationToken.None);
        await Task.Delay(300);

        // Assert — AUTO auto-advances into the head of the newly loaded queue (asset-3)
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset3, It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlaylistEngine_Manual_QueueSwapWhilePlaying_CuesNewHeadWithoutAutoPlay()
    {
        // Arrange — Manual, asset-1 on air
        const string Playlist2 = "playlist-2";
        const string Item3 = "item-3", Asset3 = "asset-3";
        const string Item4 = "item-4", Asset4 = "asset-4";

        SetupDefaultSettings(PlaylistMode.Manual);
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        SetupPlaylist(Playlist2, Item3, Asset3, Item4, Asset4);
        await _engine.InitializeAsync();               // mode = Manual
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();                      // plays asset-1
        await _eventBus.PublishAsync(
            new TrackStartedEvent(Asset1Id, "Track 1", "Artist", 240_000), CancellationToken.None);
        await Task.Delay(100);

        // Act — swap queue while playing, then asset-1 ends naturally
        await _engine.LoadPlaylistAsync(Playlist2);
        await _eventBus.PublishAsync(
            new TrackEndedEvent(Asset1Id, "NATURAL"), CancellationToken.None);
        await Task.Delay(300);

        // Assert — Manual never auto-advances: asset-3 is not started on its own
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset3, It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // Act — operator presses Play; the cursor now sits at the new queue's head
        await _engine.PlayAsync();

        // Assert — Play starts the new queue's first track (asset-3), proving the swap re-cued it
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset3, It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlaylistEngine_AutoMode_ClearWhilePlaying_FinishesCurrentThenStops()
    {
        // Arrange — AUTO, asset-1 on air
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();
        await _eventBus.PublishAsync(
            new TrackStartedEvent(Asset1Id, "Track 1", "Artist", 240_000), CancellationToken.None);
        await Task.Delay(100);

        PlaylistStoppedEvent? stopped = null;
        _eventBus.Subscribe<PlaylistStoppedEvent>(e => stopped = e);

        // Act — clear the queue mid-track; the current track must keep playing
        await _engine.ClearAsync();
        _audioEngine.Verify(e => e.StopAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Act — asset-1 finishes naturally with an empty queue behind it
        await _eventBus.PublishAsync(
            new TrackEndedEvent(Asset1Id, "NATURAL"), CancellationToken.None);
        await Task.Delay(300);

        // Assert — playback ends naturally after the current track (no crash, no second load)
        stopped.Should().NotBeNull();
        stopped!.Reason.Should().Be("NATURAL");
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset2Id, It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PlaylistEngine_NextTrackCommand_AdvancesRegardlessOfMode()
    {
        // Arrange
        SetupDefaultSettings(PlaylistMode.LiveAssist);
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();

        // Act — operator clicks NEXT
        await _engine.NextTrackAsync();

        // Assert
        _audioEngine.Verify(e => e.CancelScheduledSweepersAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset2Id, Asset2Path,
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlaylistEngine_AutoMode_Idempotency_NoDuplicateAdvance()
    {
        // Arrange
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();

        // Act — fire StartNext twice for same asset (idempotency guard via _startNextTriggeredItemId)
        var startNextEvt = new TrackStartNextReachedEvent(Asset1Id);
        await _eventBus.PublishAsync(startNextEvt, CancellationToken.None);
        await _eventBus.PublishAsync(startNextEvt, CancellationToken.None);

        await Task.Delay(500);

        // Assert — LoadTrack for asset-2 called exactly once
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset2Id, It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlaylistEngine_StopAsync_CancelsSweeperAndEmitsEnded()
    {
        // Arrange
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();

        // Simulate TrackStarted so the engine knows asset-1 is playing
        await _eventBus.PublishAsync(
            new TrackStartedEvent(Asset1Id, "Track 1", "Artist", 240_000), CancellationToken.None);
        await Task.Delay(100);

        PlaylistItemEndedEvent? receivedEnded = null;
        _eventBus.Subscribe<PlaylistItemEndedEvent>(e => receivedEnded = e);

        // Act
        await _engine.StopAsync();

        // Assert
        _audioEngine.Verify(e => e.CancelScheduledSweepersAsync(It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        _audioEngine.Verify(e => e.StopAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()), Times.Once);
        receivedEnded.Should().NotBeNull();
        receivedEnded!.EndReason.Should().Be("STOPPED");
    }

    [Fact]
    public async Task PlaylistEngine_GetSnapshot_ReflectsCurrentState()
    {
        // Arrange
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);

        // Act
        var snapshot = _engine.GetSnapshot();

        // Assert
        snapshot.StudioId.Should().Be(StudioId);
        snapshot.PlaylistId.Should().Be(PlaylistId);
        snapshot.Mode.Should().Be(PlaylistMode.Auto);
        snapshot.State.Should().Be(SessionState.Idle);
    }

    [Fact]
    public async Task PlaylistEngine_AutoMode_SkipsToNextTrack_WhenAssetNotFoundInDb()
    {
        // Arrange — item-1 references an asset that doesn't exist in the repo
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        SetupAssetNotFound(Asset1Id);   // override: asset-1 missing
        SetupAsset(Asset2Id, Asset2Path);

        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);

        // Act
        await _engine.PlayAsync();
        await Task.Delay(300);

        // Assert — jumped over asset-1, loaded asset-2
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset1Id, It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset2Id, Asset2Path,
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlaylistEngine_AutoMode_SkipsToNextTrack_WhenFilePathIsNull()
    {
        // Arrange — item-1 asset exists in DB but has no file path
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        SetupAssetNoFilePath(Asset1Id); // override: asset-1 has no path
        SetupAsset(Asset2Id, Asset2Path);

        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);

        // Act
        await _engine.PlayAsync();
        await Task.Delay(300);

        // Assert — skipped asset-1, plays asset-2
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset1Id, It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset2Id, Asset2Path,
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlaylistEngine_AutoMode_SkipsToNextTrack_WhenAssetIsDamaged()
    {
        // Arrange — item-1 asset is marked IsDamaged
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        SetupAssetDamaged(Asset1Id);    // override: asset-1 is damaged
        SetupAsset(Asset2Id, Asset2Path);

        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);

        // Act
        await _engine.PlayAsync();
        await Task.Delay(300);

        // Assert — skipped asset-1, plays asset-2
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset1Id, It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset2Id, Asset2Path,
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlaylistEngine_AutoMode_SkipsToNextTrack_WhenFileMissingOnDisk()
    {
        // Arrange — asset-1 looks healthy in the DB (path set, not damaged), but its
        // physical file was deleted externally, so the audio engine throws
        // FileNotFoundException on load. The engine must skip it, not crash the request.
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        _audioEngine
            .Setup(e => e.LoadTrackAsync(Asset1Id, It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(), It.IsAny<IReadOnlyList<EnvelopePoint>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FileNotFoundException("Audio file not found", Asset1Path));

        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);

        // Act
        await _engine.PlayAsync();
        await Task.Delay(300);

        // Assert — recovered by advancing to asset-2
        _audioEngine.Verify(e => e.LoadTrackAsync(
                Asset2Id, Asset2Path,
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlaylistEngine_AddItem_AppendsWithoutCollision_WhenQueueDoesNotStartAtPositionOne()
    {
        // Arrange — reproduces the production bug: queue occupies positions 3..8 (the
        // first tracks were already played/removed). Appending normalizes every row
        // DOWN to 1..6, which trips the uq_item_position unique key unless renumbering
        // is done in two phases (park, then write).
        var store = SetupStatefulPlaylist(new[]
        {
            ("it-a", "as-a", 3u), ("it-b", "as-b", 4u), ("it-c", "as-c", 5u),
            ("it-d", "as-d", 6u), ("it-e", "as-e", 7u), ("it-f", "as-f", 8u),
        });
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);

        // Act — append (the UI sends int.MaxValue for "add to end")
        var newItemId = await _engine.AddItemAsync("as-new", int.MaxValue);

        // Assert — no duplicate-key thrown; positions collapse to a contiguous 1..7
        // and the new item lands last.
        var final = store.Values.OrderBy(x => x.Position).ToList();
        final.Select(x => x.Position).Should().Equal(1u, 2u, 3u, 4u, 5u, 6u, 7u);
        final[^1].ItemId.Should().Be(newItemId);
    }

    [Fact]
    public async Task PlaylistEngine_AddItem_PrependsWithoutCollision_WhenQueueDoesNotStartAtPositionOne()
    {
        // Arrange — same offset queue; prepending shifts existing rows UP instead,
        // exercising the other collision direction.
        var store = SetupStatefulPlaylist(new[]
        {
            ("it-a", "as-a", 3u), ("it-b", "as-b", 4u), ("it-c", "as-c", 5u),
            ("it-d", "as-d", 6u), ("it-e", "as-e", 7u), ("it-f", "as-f", 8u),
        });
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);

        // Act — insert at the front of the queue
        var newItemId = await _engine.AddItemAsync("as-new", 0);

        // Assert — new item first at position 1, the rest follow 2..7
        var final = store.Values.OrderBy(x => x.Position).ToList();
        final.Select(x => x.Position).Should().Equal(1u, 2u, 3u, 4u, 5u, 6u, 7u);
        final[0].ItemId.Should().Be(newItemId);
    }

    [Fact]
    public async Task PlaylistEngine_AutoMode_DropsFinishedItem_SoColdStartRestoresRemainder()
    {
        // Arrange — RadioDJ-style queue consumption: once a track finishes it must drop off
        // the ON_AIR queue, so restarting the app restores only what was still queued.
        var store = SetupStatefulPlaylist(new[]
        {
            ("it-1", "as-1", 1u), ("it-2", "as-2", 2u), ("it-3", "as-3", 3u),
        });
        await _engine.InitializeAsync();              // mode = AUTO
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();                    // plays as-1

        // Act — as-1 reaches its StartNext marker → engine advances to as-2
        await _eventBus.PublishAsync(
            new TrackStartNextReachedEvent("as-1"), CancellationToken.None);
        await Task.Delay(500);

        // Assert — the played item is gone from storage; the rest survives in order
        store.Should().NotContainKey("it-1");
        store.Keys.Should().BeEquivalentTo("it-2", "it-3");

        // A cold start (fresh engine, same backing store) restores only the remainder.
        await using var coldEngine = NewEngine();
        await coldEngine.InitializeAsync();
        var restored = await coldEngine.GetCurrentItemsAsync();
        restored.Select(i => i.ItemId).Should().Equal("it-2", "it-3");
    }

    [Fact]
    public async Task PlaylistEngine_ManualSkip_DropsSkippedItem_FromQueue()
    {
        // Arrange
        var store = SetupStatefulPlaylist(new[]
        {
            ("it-1", "as-1", 1u), ("it-2", "as-2", 2u), ("it-3", "as-3", 3u),
        });
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();                    // plays as-1

        // Act — operator clicks NEXT (hard skip)
        await _engine.NextTrackAsync();

        // Assert — the skipped track is consumed (removed), as-2 is now playing
        store.Should().NotContainKey("it-1");
        store.Keys.Should().BeEquivalentTo("it-2", "it-3");
        _audioEngine.Verify(e => e.LoadTrackAsync(
                "as-2", It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlaylistEngine_Stop_DropsPartiallyPlayedItem_SoPlayStartsTheNextTrack()
    {
        // Arrange — the operator stops a track part-way through. A partially aired track is
        // still aired: it must leave the player, exactly as a skip or a natural end does.
        // Regression: Stop used to leave it loaded while the UI (which hides CurrentItemId
        // from the queue) already showed as-2 at position 1, so Play replayed as-1.
        SetupDefaultSettings(PlaylistMode.LiveAssist);
        var store = SetupStatefulPlaylist(new[]
        {
            ("it-1", "as-1", 1u), ("it-2", "as-2", 2u), ("it-3", "as-3", 3u),
        });
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();                    // plays as-1

        await _eventBus.PublishAsync(
            new TrackStartedEvent("as-1", "Track 1", "Artist", 240_000), CancellationToken.None);
        await Task.Delay(100);

        // Act — operator hits STOP mid-track
        await _engine.StopAsync();

        // The player must be EMPTY, not silently pre-loaded with the next track: as-2 stays
        // visible at position 1 in the queue view (which hides whatever CurrentItemId names)
        // until the operator actually starts it.
        _engine.GetNowPlayingInfo().CurrentItemId.Should().BeNull();
        _engine.GetNowPlayingInfo().NextItem!.ItemId.Should().Be("it-2");
        _audioEngine.Verify(e => e.LoadTrackAsync(
                "as-2", It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        await _engine.PlayAsync();

        // Assert — the stopped track is consumed and PLAY started as-2, not as-1 again
        store.Should().NotContainKey("it-1");
        store.Keys.Should().BeEquivalentTo("it-2", "it-3");
        _audioEngine.Verify(e => e.LoadTrackAsync(
                "as-2", It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        _audioEngine.Verify(e => e.LoadTrackAsync(
                "as-1", It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);   // only the original play, never a replay
    }

    [Fact]
    public async Task PlaylistEngine_StopWhileIdle_DropsNothing()
    {
        // Arrange — nothing has ever played; the player is empty.
        SetupDefaultSettings(PlaylistMode.LiveAssist);
        var store = SetupStatefulPlaylist(new[]
        {
            ("it-1", "as-1", 1u), ("it-2", "as-2", 2u),
        });
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);

        // Act — operator hits STOP on an idle deck
        await _engine.StopAsync();

        // Assert — the queue is untouched and no item got silently cued as "current"
        store.Keys.Should().BeEquivalentTo("it-1", "it-2");
        _engine.GetNowPlayingInfo().CurrentItemId.Should().BeNull();
    }

    [Fact]
    public async Task PlaylistEngine_StopAfterNaturalEnd_DoesNotDropTheCuedItem()
    {
        // Arrange — in Live Assist a natural end leaves the NEXT item cued while the engine
        // sits Idle. Stopping there must not consume that item: nobody aired it.
        SetupDefaultSettings(PlaylistMode.LiveAssist);
        var store = SetupStatefulPlaylist(new[]
        {
            ("it-1", "as-1", 1u), ("it-2", "as-2", 2u), ("it-3", "as-3", 3u),
        });
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();                    // plays as-1

        await _eventBus.PublishAsync(
            new TrackStartedEvent("as-1", "Track 1", "Artist", 240_000), CancellationToken.None);
        await Task.Delay(100);
        await _eventBus.PublishAsync(
            new TrackEndedEvent("as-1", "NATURAL"), CancellationToken.None);
        await Task.Delay(200);                        // as-1 dropped, player empty, state Idle

        // Act
        await _engine.StopAsync();

        // Assert — only the track that actually aired is gone
        store.Keys.Should().BeEquivalentTo("it-2", "it-3");
    }

    [Fact]
    public async Task PlaylistEngine_ReportsPosition_FromCueStart_NotFromZero()
    {
        // Playback is seeked to CueStart, and every cue marker is an absolute file offset — so
        // the reported position must start at CueStart too. Counting from 0 made the waveform
        // playhead and the intro/outro countdowns trail the audio by the whole cue-in offset.
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        SetupAsset(Asset1Id, Asset1Path, cueStart: 12.59);

        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();

        // Act — the engine echoes the track start back through the bus
        await _eventBus.PublishAsync(
            new TrackStartedEvent(Asset1Id, "Track 1", "Artist", 240_000), CancellationToken.None);
        await Task.Delay(100);

        // Assert — position begins at the cue-in, not at the head of the file
        _engine.GetNowPlayingInfo().PositionMs.Should().BeInRange(12_590, 14_000);
    }

    [Fact]
    public async Task PlaylistEngine_ReportsPositionFromZero_WhenNoCueStart()
    {
        // Guard the other direction: without a CueStart the position must still start at 0.
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);

        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();

        await _eventBus.PublishAsync(
            new TrackStartedEvent(Asset1Id, "Track 1", "Artist", 240_000), CancellationToken.None);
        await Task.Delay(100);

        _engine.GetNowPlayingInfo().PositionMs.Should().BeLessThan(2_000);
    }

    [Fact]
    public async Task PlaylistEngine_NaturalEnd_LeavesPlayerEmpty_WithNextStillQueued()
    {
        // A finished track must not pull the following one into the player. The queue view hides
        // whatever CurrentItemId names, so pre-loading it would make the operator see track 3 at
        // position 1 while Play would actually start track 2.
        SetupDefaultSettings(PlaylistMode.LiveAssist);
        SetupStatefulPlaylist(new[]
        {
            ("it-1", "as-1", 1u), ("it-2", "as-2", 2u), ("it-3", "as-3", 3u),
        });
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();

        await _eventBus.PublishAsync(
            new TrackStartedEvent("as-1", "Track 1", "Artist", 240_000), CancellationToken.None);
        await Task.Delay(100);

        // Act — as-1 finishes on its own
        await _eventBus.PublishAsync(
            new TrackEndedEvent("as-1", "NATURAL"), CancellationToken.None);
        await Task.Delay(200);

        // Assert — player empty, as-2 is what plays next and is still shown in the queue
        var info = _engine.GetNowPlayingInfo();
        info.CurrentItemId.Should().BeNull();
        info.NextItem!.ItemId.Should().Be("it-2");
    }

    [Fact]
    public async Task PlaylistEngine_LiveAssist_NaturalEnd_DropsFinishedItem_ButStaysIdle()
    {
        // Arrange — in Live Assist a finished track still drops off the queue, but the engine
        // does NOT auto-advance: it cues the next item and waits for the operator.
        SetupDefaultSettings(PlaylistMode.LiveAssist);
        var store = SetupStatefulPlaylist(new[]
        {
            ("it-1", "as-1", 1u), ("it-2", "as-2", 2u), ("it-3", "as-3", 3u),
        });
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();                    // plays as-1

        await _eventBus.PublishAsync(
            new TrackStartedEvent("as-1", "Track 1", "Artist", 240_000), CancellationToken.None);
        await Task.Delay(100);

        // Act — as-1 finishes naturally
        await _eventBus.PublishAsync(
            new TrackEndedEvent("as-1", "NATURAL"), CancellationToken.None);
        await Task.Delay(200);

        // Assert — as-1 dropped from the queue, but as-2 was NOT auto-played
        store.Should().NotContainKey("it-1");
        store.Keys.Should().BeEquivalentTo("it-2", "it-3");
        _audioEngine.Verify(e => e.LoadTrackAsync(
                "as-2", It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PlaylistEngine_QueueSweeper_FloatsAsOverlay_AndCrossfadesPastItIntoNextTrack()
    {
        // [A track] [S sweeper, leadIn 8s] [B track, leadIn 6s]. The sweeper overlaps A's tail,
        // so it must float as a scheduled overlay while A crossfades DIRECTLY into B with the
        // deep overlap the editor shows (8000 + 6000 − 3000 = 11000), not play S as a track.
        var store = SetupSweeperScenario();
        _audioEngine
            .Setup(e => e.CrossfadeToAsync(It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(), It.IsAny<uint>(), It.IsAny<uint>(),
                It.IsAny<uint>(), It.IsAny<IReadOnlyList<EnvelopePoint>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _audioEngine
            .Setup(e => e.ScheduleSweeperAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _engine.InitializeAsync();              // AUTO
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();                    // plays A (as-1)

        // The sweeper is scheduled as an overlay over A's tail: start = 180000 − 8000 = 172000.
        _audioEngine.Verify(e => e.ScheduleSweeperAsync(
                172_000L, @"C:\music\sweeper.mp3", It.IsAny<float>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Act — A reaches its StartNext marker.
        await _eventBus.PublishAsync(new TrackStartNextReachedEvent("as-1"), CancellationToken.None);
        await Task.Delay(500);

        // A crossfades straight into B with the deep 11s overlap.
        _audioEngine.Verify(e => e.CrossfadeToAsync(
                "as-2", It.IsAny<string>(), It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<uint>(), 11_000u, It.IsAny<uint>(), It.IsAny<IReadOnlyList<EnvelopePoint>?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // The sweeper was never promoted to a main stream.
        _audioEngine.Verify(e => e.LoadTrackAsync(
                "as-sw", It.IsAny<string>(), It.IsAny<IReadOnlyList<AssetCuePoint>>(), It.IsAny<IReadOnlyList<EnvelopePoint>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _audioEngine.Verify(e => e.CrossfadeToAsync(
                "as-sw", It.IsAny<string>(), It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<uint>(), It.IsAny<uint>(), It.IsAny<uint>(), It.IsAny<IReadOnlyList<EnvelopePoint>?>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // A and the consumed sweeper drop from the queue; only B remains.
        store.Keys.Should().BeEquivalentTo("it-b");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private PlaylistEngine NewEngine() => new(
        _audioEngine.Object,
        _eventBus,
        _playlistRepo.Object,
        _assetRepo.Object,
        _settingsRepo.Object,
        _studioCtx,
        _waveformQueue,
        _cueAnalysisQueue,
        NullLogger<PlaylistEngine>.Instance);

    /// Wires <see cref="_playlistRepo"/> as a stateful in-memory table that enforces
    /// the uq_item_position unique key (playlist_id is fixed here), so a renumber that
    /// transiently double-books a position throws exactly like MariaDB. Returns the
    /// backing store keyed by item id.
    private Dictionary<string, PlaylistItem> SetupStatefulPlaylist(
        IEnumerable<(string itemId, string assetId, uint position)> seed)
    {
        var store = new Dictionary<string, PlaylistItem>();
        foreach (var (itemId, assetId, position) in seed)
        {
            store[itemId] = new PlaylistItem
            {
                ItemId    = itemId,
                PlaylistId = PlaylistId,
                AssetId   = assetId,
                Position  = position,
                ItemType  = PlaylistItemType.Asset,
                SegueType = SegueType.Auto
            };
            SetupAsset(assetId, $@"C:\music\{assetId}.mp3");
        }
        SetupAsset("as-new", @"C:\music\as-new.mp3");

        var playlist = new Playlist
        {
            PlaylistId = PlaylistId,
            StudioId   = StudioId,
            Name       = "Stateful Playlist",
            CreatedAt  = DateTime.UtcNow
        };

        _playlistRepo
            .Setup(r => r.GetByIdAsync(PlaylistId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(playlist);
        _playlistRepo
            .Setup(r => r.GetOnAirPlaylistAsync(StudioId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(playlist);
        _playlistRepo
            .Setup(r => r.GetItemsAsync(PlaylistId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => store.Values.OrderBy(x => x.Position).ToList());

        // Simulate the uq_item_position unique key: writing a position already held by
        // a different row is rejected (mirrors MySqlException 'Duplicate entry').
        void GuardUnique(PlaylistItem it)
        {
            if (store.Values.Any(x => x.ItemId != it.ItemId && x.Position == it.Position))
                throw new InvalidOperationException(
                    $"Duplicate entry '{it.PlaylistId}-{it.Position}' for key 'uq_item_position'");
        }

        _playlistRepo
            .Setup(r => r.AddItemAsync(It.IsAny<PlaylistItem>(), It.IsAny<CancellationToken>()))
            .Returns((PlaylistItem it, CancellationToken _) => { GuardUnique(it); store[it.ItemId] = it; return Task.CompletedTask; });
        _playlistRepo
            .Setup(r => r.UpdateItemAsync(It.IsAny<PlaylistItem>(), It.IsAny<CancellationToken>()))
            .Returns((PlaylistItem it, CancellationToken _) => { GuardUnique(it); store[it.ItemId] = it; return Task.CompletedTask; });
        _playlistRepo
            .Setup(r => r.RemoveItemAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string id, CancellationToken _) => { store.Remove(id); return Task.CompletedTask; });

        return store;
    }

    // ── Dead-air recovery ───────────────────────────────────────────────────────

    [Fact]
    public async Task OnDeadAirTick_LoadsEmergencyPlaylist_AndWarns_WhenSilentPastThreshold_InAuto()
    {
        const string emergencyId = "emergency-1";
        SetupEmergencyPlaylist(emergencyId);
        await ArmPlaybackAsync();                                    // AUTO + has played
        await _engine.UpdateAudioSettingsAsync(DeadAirSettings(true, 5, emergencyId));

        DeadAirWarningEvent? warning = null;
        _eventBus.Subscribe<DeadAirWarningEvent>(e => warning = e);

        await _engine.OnDeadAirTickAsync(6_000);                    // 6s silent > 5s threshold
        await Task.Delay(200);

        warning.Should().NotBeNull();
        warning!.Mode.Should().Be(PlaylistMode.Auto);
        _playlistRepo.Verify(r => r.GetByIdAsync(emergencyId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task OnDeadAirTick_DoesNothing_BelowThreshold()
    {
        await ArmPlaybackAsync();
        await _engine.UpdateAudioSettingsAsync(DeadAirSettings(true, 5, "emergency-1"));

        DeadAirWarningEvent? warning = null;
        _eventBus.Subscribe<DeadAirWarningEvent>(e => warning = e);

        await _engine.OnDeadAirTickAsync(4_000);                    // 4s < 5s threshold
        await Task.Delay(100);

        warning.Should().BeNull();
    }

    [Fact]
    public async Task OnDeadAirTick_DoesNothing_WhenNotAutoMode()
    {
        await ArmPlaybackAsync();
        await _engine.ChangeModeAsync(PlaylistMode.Manual);
        await _engine.UpdateAudioSettingsAsync(DeadAirSettings(true, 5, "emergency-1"));

        DeadAirWarningEvent? warning = null;
        _eventBus.Subscribe<DeadAirWarningEvent>(e => warning = e);

        await _engine.OnDeadAirTickAsync(10_000);                   // well past threshold
        await Task.Delay(100);

        warning.Should().BeNull();
    }

    [Fact]
    public async Task OnDeadAirTick_DoesNothing_WhenNothingHasPlayedYet()
    {
        // Cold, idle start: AUTO by default mode but no track has ever played.
        await _engine.InitializeAsync();
        await _engine.UpdateAudioSettingsAsync(DeadAirSettings(true, 5, "emergency-1"));

        DeadAirWarningEvent? warning = null;
        _eventBus.Subscribe<DeadAirWarningEvent>(e => warning = e);

        await _engine.OnDeadAirTickAsync(10_000);
        await Task.Delay(100);

        warning.Should().BeNull();
    }

    [Fact]
    public async Task OnDeadAirTick_FiresOncePerSilenceEpisode_AndReArmsWhenAudioReturns()
    {
        const string emergencyId = "emergency-1";
        SetupEmergencyPlaylist(emergencyId);
        await ArmPlaybackAsync();
        await _engine.UpdateAudioSettingsAsync(DeadAirSettings(true, 5, emergencyId));

        int warnings = 0;
        _eventBus.Subscribe<DeadAirWarningEvent>(_ => warnings++);

        await _engine.OnDeadAirTickAsync(6_000);   // fires
        await _engine.OnDeadAirTickAsync(7_000);   // latched — no refire
        await _engine.OnDeadAirTickAsync(0);       // audio returned — clears latch
        await _engine.OnDeadAirTickAsync(6_000);   // fires again
        await Task.Delay(200);

        warnings.Should().Be(2);
    }

    [Fact]
    public async Task OnDeadAirTick_Warns_ButLoadsNothing_WhenNoEmergencyPlaylistSet()
    {
        await ArmPlaybackAsync();
        await _engine.UpdateAudioSettingsAsync(DeadAirSettings(true, 5, emergencyId: null));

        DeadAirWarningEvent? warning = null;
        _eventBus.Subscribe<DeadAirWarningEvent>(e => warning = e);

        await _engine.OnDeadAirTickAsync(6_000);
        await Task.Delay(100);

        warning.Should().NotBeNull();
        _playlistRepo.Verify(
            r => r.GetOnAirPlaylistAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task OnDeadAirTick_DoesNothing_WhenDeadAirDisabled()
    {
        // Armed + AUTO + well past any threshold, but the feature switch is OFF.
        await ArmPlaybackAsync();
        await _engine.UpdateAudioSettingsAsync(DeadAirSettings(enabled: false, 5, "emergency-1"));

        DeadAirWarningEvent? warning = null;
        _eventBus.Subscribe<DeadAirWarningEvent>(e => warning = e);

        await _engine.OnDeadAirTickAsync(10_000);
        await Task.Delay(100);

        warning.Should().BeNull();
        _playlistRepo.Verify(r => r.GetByIdAsync("emergency-1", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnDeadAirTick_DoesNothing_WhenThresholdIsZero()
    {
        // Threshold of 0s must be treated as "disabled", not "fire immediately".
        await ArmPlaybackAsync();
        await _engine.UpdateAudioSettingsAsync(DeadAirSettings(enabled: true, thresholdS: 0, "emergency-1"));

        DeadAirWarningEvent? warning = null;
        _eventBus.Subscribe<DeadAirWarningEvent>(e => warning = e);

        await _engine.OnDeadAirTickAsync(10_000);
        await Task.Delay(100);

        warning.Should().BeNull();
    }

    [Fact]
    public async Task OnDeadAirTick_LoadsAndPlaysEmergencyTrack_InAuto()
    {
        // Full recovery path: past threshold in AUTO must not only warn but actually put the
        // emergency asset on air (LoadSavedPlaylistIntoQueueAsync + PlayAsync).
        const string emergencyId = "emergency-1";
        SetupEmergencyPlaylist(emergencyId);
        await ArmPlaybackAsync();
        await _engine.UpdateAudioSettingsAsync(DeadAirSettings(true, 5, emergencyId));

        await _engine.OnDeadAirTickAsync(6_000);
        await Task.Delay(300);

        _audioEngine.Verify(e => e.LoadTrackAsync(
                "em-asset-1", @"C:\music\emergency.mp3",
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private AudioSettings DeadAirSettings(bool enabled, uint thresholdS, string? emergencyId) => new()
    {
        SettingsId          = "settings-1",
        StudioId            = StudioId,
        DefaultMode         = PlaylistMode.Auto,
        SampleRate          = AudioSampleRate.Hz48000,
        BufferSize          = AudioBufferSize.Samples512,
        OutputMode          = DriverType.WasapiExclusive,
        DeadAirEnabled      = enabled,
        DeadAirThresholdS   = thresholdS,
        EmergencyPlaylistId = emergencyId
    };

    /// Drives the engine through a real track start so dead-air recovery is armed
    /// (_hasEverPlayed) and the mode is AUTO.
    private async Task ArmPlaybackAsync()
    {
        SetupPlaylist(PlaylistId, Item1Id, Asset1Id, Item2Id, Asset2Id);
        await _engine.InitializeAsync();
        await _engine.LoadPlaylistAsync(PlaylistId);
        await _engine.PlayAsync();
        await _eventBus.PublishAsync(
            new TrackStartedEvent(Asset1Id, "Track 1", "Artist", 240_000), CancellationToken.None);
        await Task.Delay(200);
    }

    /// Minimal mocks for LoadSavedPlaylistIntoQueueAsync: a saved emergency playlist with one
    /// item copied into a distinct ON_AIR playlist, then reloaded from ON_AIR.
    private void SetupEmergencyPlaylist(string emergencyId)
    {
        const string onAirId    = "onair-1";
        const string emAssetId  = "em-asset-1";

        var emergency = new Playlist { PlaylistId = emergencyId, StudioId = StudioId, Name = "Awaryjna", CreatedAt = DateTime.UtcNow };
        var onAir     = new Playlist { PlaylistId = onAirId,    StudioId = StudioId, Name = "OnAir",    CreatedAt = DateTime.UtcNow };

        var emItems = new List<PlaylistItem>
        {
            new() { ItemId = "em-item-1", PlaylistId = emergencyId, AssetId = emAssetId, Position = 0, ItemType = PlaylistItemType.Asset }
        };
        var onAirStore = new List<PlaylistItem>();

        _playlistRepo.Setup(r => r.GetByIdAsync(emergencyId, It.IsAny<CancellationToken>())).ReturnsAsync(emergency);
        _playlistRepo.Setup(r => r.GetByIdAsync(onAirId, It.IsAny<CancellationToken>())).ReturnsAsync(onAir);
        _playlistRepo.Setup(r => r.GetOnAirPlaylistAsync(StudioId, It.IsAny<CancellationToken>())).ReturnsAsync(onAir);
        _playlistRepo.Setup(r => r.GetItemsAsync(emergencyId, It.IsAny<CancellationToken>())).ReturnsAsync(emItems);
        _playlistRepo.Setup(r => r.GetItemsAsync(onAirId, It.IsAny<CancellationToken>())).ReturnsAsync(() => onAirStore.OrderBy(i => i.Position).ToList());
        _playlistRepo.Setup(r => r.ClearItemsAsync(onAirId, It.IsAny<CancellationToken>()))
                     .Returns(() => { onAirStore.Clear(); return Task.CompletedTask; });
        _playlistRepo.Setup(r => r.AddItemAsync(It.IsAny<PlaylistItem>(), It.IsAny<CancellationToken>()))
                     .Returns((PlaylistItem it, CancellationToken _) => { onAirStore.Add(it); return Task.CompletedTask; });

        SetupAsset(emAssetId, @"C:\music\emergency.mp3");
    }

    private void SetupDefaultSettings(PlaylistMode defaultMode)
    {
        var settings = new AudioSettings
        {
            SettingsId   = "settings-1",
            StudioId     = StudioId,
            DefaultMode  = defaultMode,
            SampleRate   = AudioSampleRate.Hz48000,
            BufferSize   = AudioBufferSize.Samples512,
            OutputMode   = DriverType.WasapiExclusive
        };
        _settingsRepo
            .Setup(r => r.GetByStudioAsync(StudioId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);
    }

    private void SetupDefaultAudioEngine()
    {
        _audioEngine
            .Setup(e => e.LoadTrackAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<AssetCuePoint>>(),
                It.IsAny<IReadOnlyList<EnvelopePoint>?>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _audioEngine
            .Setup(e => e.PlayAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _audioEngine
            .Setup(e => e.StopAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _audioEngine
            .Setup(e => e.CancelScheduledSweepersAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _audioEngine
            .Setup(e => e.SeekPlaylistStreamAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _audioEngine
            .Setup(e => e.SetPlayerLoopAsync(
                It.IsAny<bool>(), It.IsAny<uint>(), It.IsAny<uint>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private void SetupPlaylist(
        string playlistId,
        string item1Id, string asset1Id,
        string item2Id, string asset2Id)
    {
        var playlist = new Playlist
        {
            PlaylistId = playlistId,
            StudioId   = StudioId,
            Name       = "Test Playlist",
            CreatedAt  = DateTime.UtcNow
        };

        var items = new List<PlaylistItem>
        {
            new() { ItemId = item1Id, PlaylistId = playlistId, AssetId = asset1Id, Position = 0, ItemType = PlaylistItemType.Asset },
            new() { ItemId = item2Id, PlaylistId = playlistId, AssetId = asset2Id, Position = 1, ItemType = PlaylistItemType.Asset }
        };

        _playlistRepo
            .Setup(r => r.GetByIdAsync(playlistId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(playlist);
        _playlistRepo
            .Setup(r => r.GetItemsAsync(playlistId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);

        SetupAsset(asset1Id, Asset1Path);
        SetupAsset(asset2Id, Asset2Path);
    }

    /// Two-item AUTO playlist where the second (incoming) item carries the crossfade duration.
    /// The incoming item owns the crossfade (overlap with its predecessor), so reaching the
    /// first track's StartNext triggers an overlapping crossfade of that length into item 2.
    private void SetupCrossfadePlaylist(uint crossfadeMs)
    {
        var playlist = new Playlist
        {
            PlaylistId = PlaylistId,
            StudioId   = StudioId,
            Name       = "Crossfade Playlist",
            CreatedAt  = DateTime.UtcNow
        };

        var items = new List<PlaylistItem>
        {
            new() { ItemId = Item1Id, PlaylistId = PlaylistId, AssetId = Asset1Id, Position = 0, ItemType = PlaylistItemType.Asset, SegueType = SegueType.Auto },
            new() { ItemId = Item2Id, PlaylistId = PlaylistId, AssetId = Asset2Id, Position = 1, ItemType = PlaylistItemType.Asset, SegueType = SegueType.Auto, CrossfadeMs = crossfadeMs }
        };

        _playlistRepo
            .Setup(r => r.GetByIdAsync(PlaylistId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(playlist);
        _playlistRepo
            .Setup(r => r.GetItemsAsync(PlaylistId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(items);

        SetupAsset(Asset1Id, Asset1Path);
        SetupAsset(Asset2Id, Asset2Path);
    }

    private void SetupAsset(
        string assetId, string filePath,
        uint durationMs = 240_000, AssetType assetType = AssetType.Track,
        string? formatName = "Music",
        double? cueEnd = null, bool isVariableDuration = false,
        double? cueStart = null)
    {
        var asset = new Asset
        {
            AssetId     = assetId,
            Title       = $"Track {assetId}",
            Artist      = "Test Artist",
            DurationMs  = durationMs,
            Checksum    = $"checksum-{assetId}",
            RdmFilePath = filePath,
            AssetType   = assetType,
            FormatName  = formatName,
            Status      = AssetStatus.Active,
            CreatedAt   = DateTime.UtcNow,
            UpdatedAt   = DateTime.UtcNow,
            CueEnd      = cueEnd,
            CueStart    = cueStart,
            IsVariableDuration = isVariableDuration
        };

        _assetRepo
            .Setup(r => r.GetByIdAsync(assetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(asset);
    }

    /// Queue [A track, S sweeper, B track] where the sweeper overlaps A's tail via LeadInMs.
    /// Returns the backing store keyed by item id (RemoveItemAsync mutates it).
    private Dictionary<string, PlaylistItem> SetupSweeperScenario()
    {
        const string sweeperPath = @"C:\music\sweeper.mp3";
        SetupAsset("as-1", Asset1Path, durationMs: 180_000);
        SetupAsset("as-sw", sweeperPath, durationMs: 3_000, assetType: AssetType.Sweeper);
        SetupAsset("as-2", Asset2Path, durationMs: 200_000);

        var store = new Dictionary<string, PlaylistItem>
        {
            ["it-a"]  = new() { ItemId = "it-a",  PlaylistId = PlaylistId, AssetId = "as-1",  Position = 1, ItemType = PlaylistItemType.Asset, SegueType = SegueType.Auto },
            ["it-sw"] = new() { ItemId = "it-sw", PlaylistId = PlaylistId, AssetId = "as-sw", Position = 2, ItemType = PlaylistItemType.Asset, SegueType = SegueType.Auto, LeadInMs = 8_000 },
            ["it-b"]  = new() { ItemId = "it-b",  PlaylistId = PlaylistId, AssetId = "as-2",  Position = 3, ItemType = PlaylistItemType.Asset, SegueType = SegueType.Auto, LeadInMs = 6_000 },
        };

        var playlist = new Playlist
        {
            PlaylistId = PlaylistId, StudioId = StudioId, Name = "Sweeper Scenario", CreatedAt = DateTime.UtcNow
        };
        _playlistRepo.Setup(r => r.GetByIdAsync(PlaylistId, It.IsAny<CancellationToken>())).ReturnsAsync(playlist);
        _playlistRepo.Setup(r => r.GetOnAirPlaylistAsync(StudioId, It.IsAny<CancellationToken>())).ReturnsAsync(playlist);
        _playlistRepo
            .Setup(r => r.GetItemsAsync(PlaylistId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => store.Values.OrderBy(x => x.Position).ToList());
        _playlistRepo
            .Setup(r => r.RemoveItemAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string id, CancellationToken _) => { store.Remove(id); return Task.CompletedTask; });

        return store;
    }

    private void SetupAssetNotFound(string assetId)
    {
        _assetRepo
            .Setup(r => r.GetByIdAsync(assetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Asset?)null);
    }

    private void SetupAssetNoFilePath(string assetId)
    {
        var asset = new Asset
        {
            AssetId     = assetId,
            Title       = $"Track {assetId}",
            Artist      = "Test Artist",
            DurationMs  = 240_000,
            Checksum    = $"checksum-{assetId}",
            RdmFilePath = null,
            AssetType   = AssetType.Track,
            Status      = AssetStatus.Active,
            CreatedAt   = DateTime.UtcNow,
            UpdatedAt   = DateTime.UtcNow
        };

        _assetRepo
            .Setup(r => r.GetByIdAsync(assetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(asset);
    }

    private void SetupAssetDamaged(string assetId)
    {
        var asset = new Asset
        {
            AssetId     = assetId,
            Title       = $"Track {assetId}",
            Artist      = "Test Artist",
            DurationMs  = 0,
            Checksum    = $"checksum-{assetId}",
            RdmFilePath = @"C:\music\damaged.mp3",
            IsDamaged   = true,
            AssetType   = AssetType.Track,
            Status      = AssetStatus.Active,
            CreatedAt   = DateTime.UtcNow,
            UpdatedAt   = DateTime.UtcNow
        };

        _assetRepo
            .Setup(r => r.GetByIdAsync(assetId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(asset);
    }
}
