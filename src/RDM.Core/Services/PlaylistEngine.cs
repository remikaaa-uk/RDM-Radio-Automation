using Microsoft.Extensions.Logging;
using RDM.Core.Entities;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Core.Queues;
using RDM.Shared.Enums;
using PlaylistType = RDM.Shared.Enums.PlaylistType;

namespace RDM.Core.Services;

/// <summary>
/// Manages the active playlist queue — tracks current/next item, handles mode transitions,
/// and translates low-level audio events into playlist-domain events enriched with ItemId.
///
/// SRP: owns no timers and writes no snapshots.
/// PlaybackSessionSnapshotService calls GetSnapshot() every 30 seconds.
/// SweeperEngine calls PeekNextItem() to calculate sweeper timing.
/// </summary>
public sealed class PlaylistEngine : IPlaylistController, IAsyncDisposable
{
    private readonly IAudioEngine                _audioEngine;
    private readonly IEventBus                  _eventBus;
    private readonly IPlaylistRepository        _playlistRepo;
    private readonly IAssetRepository           _assetRepo;
    private readonly IAudioSettingsRepository   _settingsRepo;
    private readonly StudioContext              _studioContext;
    private readonly WaveformQueue              _waveformQueue;
    private readonly CueAnalysisQueue           _cueAnalysisQueue;
    private readonly ILogger<PlaylistEngine>    _logger;
    private readonly CancellationTokenSource    _cts = new();
    private readonly SemaphoreSlim              _lock = new(1, 1);

    // Stable across the entire session — used by PlaybackSession upsert (one row per studio)
    private readonly string _sessionId = Guid.NewGuid().ToString();

    // ── Mutable state — always accessed under _lock ───────────────────────────

    private Playlist?                  _currentPlaylist;
    private IReadOnlyList<PlaylistItem> _items = Array.Empty<PlaylistItem>();
    private int                        _currentIndex    = -1;
    private string?                    _currentItemId;
    private string?                    _currentAssetId;
    private Asset?                     _currentAsset;
    private AudioSettings?             _audioSettings;
    private string?                    _startNextTriggeredItemId;
    // Repeat-current-track. A player-level toggle, not a property of the item: it survives Stop
    // and manual Next, and then applies to whatever is loaded. While set, every automatic exit
    // from the current track (StartNext, FadeOut, CueEnd, natural end) is suppressed — the audio
    // engine rewinds the track instead. Deliberately independent of _mode: it must hold in AUTO.
    private bool                       _loopCurrent;
    private PlaylistMode               _mode = PlaylistMode.LiveAssist;
    private SessionState               _state = SessionState.Idle;
    private uint                       _accumulatedPositionMs;
    // Dead-air recovery: only arm after real playback has occurred (avoids firing on a cold,
    // idle start where the queue is legitimately empty). Latch fires the recovery once per
    // silence episode; cleared when audio returns (OnDeadAirTickAsync with silentMs == 0).
    private bool                       _hasEverPlayed;
    private bool                       _deadAirLatched;
    private DateTime?                  _segmentStartedAt;
    private DateTime?                  _trackStartedAt;
    private uint?                      _outroCueMs;
    private uint?                      _cueFadeOutMs;
    private uint?                      _cueStartNextMs;
    private uint?                      _cueEndMs;
    // CueStart of the loaded track: playback is seeked there, so the reported position starts
    // there too. Cue markers are absolute file offsets — a position counted from 0 would trail
    // the audio by this offset (wrong playhead, wrong intro/outro countdowns).
    private uint?                      _cueStartMs;
    // Live-detected duration for the currently loaded IsVariableDuration asset (see
    // LoadAndPlayItemAsync); overrides the stale Asset.DurationMs when publishing TrackStarted.
    private uint?                      _liveDurationMsOverride;

    // Sweeper-overlay transition (uwagi.md §1 LeadInMs follow-up): when the next queue items
    // include a sweeper that floats over the current track's tail, the engine crossfades
    // directly into the following track using this deep overlap and consumes the sweeper items.
    private uint?                      _transitionCrossfadeOverride;
    private List<string>              _overlaySweeperItemIds = new();

    // ── Event handler delegates stored for Unsubscribe ────────────────────────

    private readonly Action<TrackStartNextReachedEvent> _startNextHandler;
    private readonly Action<TrackFadeOutReachedEvent>   _fadeOutHandler;
    private readonly Action<TrackStartedEvent>          _startedHandler;
    private readonly Action<TrackEndedEvent>            _endedHandler;
    private readonly Action<TrackCueEndReachedEvent>    _cueEndHandler;
    private readonly Action<PlayerLoopedEvent>          _loopedHandler;

    // ── Constructor ───────────────────────────────────────────────────────────

    public PlaylistEngine(
        IAudioEngine             audioEngine,
        IEventBus                eventBus,
        IPlaylistRepository      playlistRepo,
        IAssetRepository         assetRepo,
        IAudioSettingsRepository settingsRepo,
        StudioContext            studioContext,
        WaveformQueue            waveformQueue,
        CueAnalysisQueue         cueAnalysisQueue,
        ILogger<PlaylistEngine>  logger)
    {
        _audioEngine      = audioEngine;
        _eventBus         = eventBus;
        _playlistRepo     = playlistRepo;
        _assetRepo        = assetRepo;
        _settingsRepo     = settingsRepo;
        _studioContext    = studioContext;
        _waveformQueue    = waveformQueue;
        _cueAnalysisQueue = cueAnalysisQueue;
        _logger           = logger;

        _startNextHandler = evt => _ = FireAndForget(OnTrackStartNextAsync(evt, _cts.Token));
        _fadeOutHandler   = evt => _ = FireAndForget(OnTrackFadeOutAsync(evt, _cts.Token));
        _startedHandler   = evt => _ = FireAndForget(OnTrackStartedAsync(evt, _cts.Token));
        _endedHandler     = evt => _ = FireAndForget(OnTrackEndedAsync(evt, _cts.Token));
        _cueEndHandler    = evt => _ = FireAndForget(OnTrackCueEndReachedAsync(evt, _cts.Token));
        _loopedHandler    = evt => _ = FireAndForget(OnPlayerLoopedAsync(evt, _cts.Token));

        _eventBus.Subscribe(_loopedHandler);
        _eventBus.Subscribe(_startNextHandler);
        _eventBus.Subscribe(_fadeOutHandler);
        _eventBus.Subscribe(_startedHandler);
        _eventBus.Subscribe(_endedHandler);
        _eventBus.Subscribe(_cueEndHandler);
    }

    // ── Public read-only API ──────────────────────────────────────────────────

    /// <summary>
    /// Used by EventScheduler to determine whether smart_timing should defer execution.
    /// </summary>
    public bool IsPlaying
    {
        get
        {
            _lock.Wait();
            try   { return _state == SessionState.Playing; }
            finally { _lock.Release(); }
        }
    }

    /// <summary>
    /// Used by SweeperEngine to calculate next-track cue points and timing.
    /// Returns null when at end of playlist or no playlist loaded.
    /// </summary>
    public PlaylistItem? PeekNextItem()
    {
        _lock.Wait();
        try
        {
            int next = NextItemIndexLocked();
            return next < _items.Count ? _items[next] : null;
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Index of the item Play will start next. <see cref="_currentIndex"/> is a cursor into the
    /// queue, not proof that something is loaded: when the player is empty (idle after a stop, a
    /// finished track, or a fresh queue) the cursor already rests ON the item that plays next, so
    /// "next" is the cursor itself rather than the slot behind it.
    /// Must be called while holding <c>_lock</c>.
    /// </summary>
    private int NextItemIndexLocked() =>
        _currentItemId is null ? Math.Max(0, _currentIndex) : _currentIndex + 1;

    /// <summary>
    /// Called by PlaybackSessionSnapshotService every 30 seconds.
    /// Atomic snapshot of the current engine state.
    /// </summary>
    public PlaybackSession GetSnapshot()
    {
        _lock.Wait();
        try
        {
            int nextIdx = NextItemIndexLocked();
            uint posMs = _segmentStartedAt.HasValue
                ? _accumulatedPositionMs + (uint)(DateTime.UtcNow - _segmentStartedAt.Value).TotalMilliseconds
                : _accumulatedPositionMs;

            return new PlaybackSession
            {
                SessionId         = _sessionId,
                StudioId          = _studioContext.StudioId,
                CurrentAssetId    = _currentAssetId,
                CurrentPositionMs = posMs,
                NextAssetId       = nextIdx < _items.Count ? _items[nextIdx].AssetId : null,
                PlaylistId        = _currentPlaylist?.PlaylistId,
                PlaylistItemId    = _currentItemId,
                State             = _state,
                Mode              = _mode,
                SnapshotAt        = DateTime.UtcNow
            };
        }
        finally { _lock.Release(); }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads default mode from AudioSettings. Must be called after StudioContext.Initialize().
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var settings = await _settingsRepo.GetByStudioAsync(_studioContext.StudioId, ct);
        if (settings is null) return;

        await _lock.WaitAsync(ct);
        try
        {
            _mode          = settings.DefaultMode;
            _audioSettings = settings;
        }
        finally { _lock.Release(); }

        _logger.LogInformation(
            "PlaylistEngine initialized. Default mode: {Mode}", settings.DefaultMode);
    }

    /// <summary>
    /// Refreshes the cached AudioSettings (crossfade duration, sweeper ducking) after the
    /// user saves Settings, so subsequent transitions use the new values without a restart.
    /// The live playback mode is deliberately left untouched — DefaultMode only seeds it.
    /// </summary>
    public async Task UpdateAudioSettingsAsync(AudioSettings settings, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _audioSettings = settings;
        }
        finally { _lock.Release(); }

        _logger.LogDebug(
            "PlaylistEngine: audio settings hot-updated (crossfade={Xf}ms, sweeperDucking={Duck}dB)",
            settings.CrossfadeDurationMs, settings.SweeperDuckingDb);
    }

    /// <summary>
    /// Called by DeadAirMonitorService's poll loop with how long the master output has been
    /// continuously silent (0 = audio present). Owns no timer itself — the
    /// monitor service drives the cadence (engine SRP: "owns no timers"). When in AUTO mode with
    /// dead-air detection enabled and the silence exceeds DeadAirThresholdS, it publishes a
    /// <see cref="DeadAirWarningEvent"/> and — if an emergency playlist is configured — copies it
    /// into the ON_AIR queue and starts playback. Fires once per silence episode (latch), and only
    /// after real playback has happened this session (never on a cold, idle start).
    /// </summary>
    public async Task OnDeadAirTickAsync(long silentMs, CancellationToken ct = default)
    {
        string? emergencyId;
        long    thresholdMs;
        await _lock.WaitAsync(ct);
        try
        {
            if (silentMs <= 0) { _deadAirLatched = false; return; }

            bool enabled = _audioSettings?.DeadAirEnabled == true;
            if (!enabled || !_hasEverPlayed || _mode != PlaylistMode.Auto || _deadAirLatched)
                return;

            thresholdMs = (_audioSettings?.DeadAirThresholdS ?? 0) * 1000L;
            if (thresholdMs <= 0 || silentMs < thresholdMs)
                return;

            emergencyId    = _audioSettings?.EmergencyPlaylistId;
            _deadAirLatched = true;   // fire once until audio returns
        }
        finally { _lock.Release(); }

        _logger.LogWarning(
            "Dead air detected in AUTO after {SilentMs} ms of silence — emergency playlist={Id}",
            silentMs, string.IsNullOrEmpty(emergencyId) ? "(none)" : emergencyId);

        await _eventBus.PublishAsync(new DeadAirWarningEvent(silentMs, PlaylistMode.Auto), ct);

        if (!string.IsNullOrEmpty(emergencyId))
        {
            await LoadSavedPlaylistIntoQueueAsync(emergencyId, ct);
            await PlayAsync(ct);
        }
    }

    // ── Playlist operations ───────────────────────────────────────────────────

    public async Task LoadPlaylistAsync(string playlistId, CancellationToken ct = default)
    {
        var playlist = await _playlistRepo.GetByIdAsync(playlistId, ct);
        if (playlist is null)
        {
            _logger.LogWarning("Playlist {PlaylistId} not found", playlistId);
            return;
        }
        var items = await _playlistRepo.GetItemsAsync(playlistId, ct);

        await _lock.WaitAsync(ct);
        try
        {
            _currentPlaylist = playlist;
            _items           = items;

            // Pending sweeper-overlay transitions belonged to the previous queue — drop them.
            _transitionCrossfadeOverride = null;
            _overlaySweeperItemIds = new List<string>();

            if (_state == SessionState.Idle)
            {
                // Nothing playing — the first queued item becomes current (original behaviour;
                // a subsequent PlayAsync starts from index 0).
                _currentIndex   = 0;
                _currentItemId  = null;
                _currentAssetId = null;
                _currentAsset   = null;

                _accumulatedPositionMs = 0;
                _segmentStartedAt = null;
                _trackStartedAt = null;
                _outroCueMs = null;
                _cueFadeOutMs = null;
                _cueStartNextMs = null;
                _state = SessionState.Idle;
            }
            else
            {
                // A track is live: the new list is queued *behind* it. Preserve the playing
                // track and its state; _currentIndex = -1 means "the current track is not in
                // _items". When it ends, AUTO advancement enters the new queue at index 0
                // (Manual/Live Assist merely cues the new head). See ClearAsync for the
                // matching queue-swap-while-playing handling.
                _currentIndex = -1;
            }
        }
        finally { _lock.Release(); }

        _logger.LogInformation(
            "Playlist '{Name}' loaded ({Count} items) [id={PlaylistId}, type={Type}]",
            playlist.Name, items.Count, playlist.PlaylistId, playlist.PlaylistType);

        await _eventBus.PublishAsync(new PlaylistQueueReloadedEvent(playlist.PlaylistId), ct);
    }

    /// <summary>
    /// Loads a SAVED playlist into the studio's live queue — used by the LOAD_PLAYLIST
    /// scheduled-event action. Unlike <see cref="LoadPlaylistAsync"/> (which simply points the
    /// session at whatever playlist id it's given — cold start always passes the ON_AIR one),
    /// this copies the saved playlist's tracks into the ON_AIR playlist instead of adopting the
    /// saved playlist's own identity. The ON_AIR playlist is the only one a cold start reloads
    /// (see GetCurrentItemsAsync), so anything loaded without this copy would be lost on restart.
    /// </summary>
    public async Task LoadSavedPlaylistIntoQueueAsync(string savedPlaylistId, CancellationToken ct = default)
    {
        var saved = await _playlistRepo.GetByIdAsync(savedPlaylistId, ct);
        if (saved is null)
        {
            _logger.LogWarning("Playlist {PlaylistId} not found", savedPlaylistId);
            return;
        }

        var onAir = await _playlistRepo.GetOnAirPlaylistAsync(_studioContext.StudioId, ct);
        if (onAir is null)
        {
            _logger.LogWarning(
                "LoadSavedPlaylistIntoQueueAsync: studio {StudioId} has no ON_AIR playlist — cannot load '{Name}'",
                _studioContext.StudioId, saved.Name);
            return;
        }

        var items = await _playlistRepo.GetItemsAsync(savedPlaylistId, ct);

        await _playlistRepo.ClearItemsAsync(onAir.PlaylistId, ct);
        foreach (var src in items)
        {
            await _playlistRepo.AddItemAsync(
                CopyItem(src, itemId: Guid.NewGuid().ToString(), playlistId: onAir.PlaylistId), ct);
            await TryEnqueueVariableDurationRescanAsync(src.AssetId, ct);
        }

        _logger.LogInformation(
            "Copied {Count} item(s) from saved playlist '{SavedName}' ({SavedId}) into ON_AIR playlist '{OnAirName}' ({OnAirId})",
            items.Count, saved.Name, saved.PlaylistId, onAir.Name, onAir.PlaylistId);

        // Reload from ON_AIR's own (now-copied) rows — refreshes in-memory session state,
        // logs the usual "Playlist '...' loaded (N items)" line, and publishes
        // PlaylistQueueReloadedEvent so connected UI clients refresh their queue view.
        await LoadPlaylistAsync(onAir.PlaylistId, ct);
    }

    public async Task PlayAsync(CancellationToken ct = default)
    {
        bool wasPaused;
        await _lock.WaitAsync(ct);
        try
        {
            wasPaused = _state == SessionState.Paused;
        }
        finally { _lock.Release(); }

        if (wasPaused)
        {
            await _audioEngine.PlayAsync(ct);
            return;
        }

        PlaylistItem? item = await PickCurrentItemAsync(ct);
        if (item is null) return;

        await LoadAndPlayItemAsync(item, ct);

        await _eventBus.PublishAsync(
            new PlaylistStartedEvent(_currentPlaylist?.PlaylistId ?? string.Empty), ct);
    }

    /// Manual skip — hard cut to the next track (no crossfade).
    public Task NextTrackAsync(CancellationToken ct = default) => AdvanceAsync(0, 0, ct);

    /// Advances to the next playable item. When <paramref name="crossfadeMs"/> &gt; 0 the
    /// new track overlaps the current one (true crossfade); otherwise it is a hard cut.
    /// <paramref name="fadeDelayMs"/> delays the outgoing fade (used when a track has both
    /// StartNext and FadeOut markers — the incoming starts at StartNext, the outgoing fades later).
    private async Task AdvanceAsync(uint crossfadeMs, uint fadeDelayMs, CancellationToken ct = default)
    {
        PlaylistItem? item;
        List<string>  playedItemIds;

        await _lock.WaitAsync(ct);
        try
        {
            await _audioEngine.CancelScheduledSweepersAsync(ct); // Bug 2 fix

            // RadioDJ-style: the finished track drops off the queue instead of leaving a
            // pointer behind it. The list shrinks in place, so _currentIndex now refers to
            // the next playable item, and a cold start restores only what is still queued.
            playedItemIds = DequeueCurrentItemLocked();

            _accumulatedPositionMs    = 0;
            _segmentStartedAt         = null;
            _trackStartedAt           = null;
            _outroCueMs               = null;
            _cueFadeOutMs             = null;
            _cueStartNextMs           = null;
            _startNextTriggeredItemId = null;

            if (_currentIndex >= _items.Count)
            {
                _state          = SessionState.Idle;
                _currentItemId  = null;
                _currentAssetId = null;
                _currentAsset   = null;
                _cueEndMs       = null;
                item = null;
            }
            else
            {
                item = _items[_currentIndex];
                _currentItemId  = item.ItemId;
                _currentAssetId = item.AssetId;
            }
        }
        finally { _lock.Release(); }

        await DeleteQueueItemsAsync(playedItemIds, ct);

        if (item is null)
        {
            // No next track: fade the final track out (if a crossfade was requested), then stop.
            if (crossfadeMs > 0)
            {
                if (fadeDelayMs > 0) await Task.Delay((int)fadeDelayMs, ct);
                await _audioEngine.FadeOutAsync(crossfadeMs, ct);
            }
            _logger.LogInformation("Playlist reached end — stopping");
            await _eventBus.PublishAsync(
                new PlaylistStoppedEvent(_currentPlaylist?.PlaylistId ?? string.Empty, "NATURAL"), ct);
            return;
        }

        await LoadAndPlayItemAsync(item, ct, crossfadeMs, fadeDelayMs);
    }

    public async Task SetLoopCurrentAsync(bool enabled, CancellationToken ct = default)
    {
        uint loopStartMs;
        uint loopEndMs;

        await _lock.WaitAsync(ct);
        try
        {
            _loopCurrent = enabled;
            loopStartMs  = _cueStartMs ?? 0;
            loopEndMs    = _cueEndMs   ?? 0;   // 0 → engine loops at the end of the file
        }
        finally { _lock.Release(); }

        // With nothing loaded this only records the wish; LoadAndPlayItemAsync re-arms the
        // engine with the new track's region, so pressing Loop before Play still works.
        await _audioEngine.SetPlayerLoopAsync(enabled, loopStartMs, loopEndMs, ct);

        _logger.LogInformation("Loop current track {State} (region {StartMs}..{EndMs}ms)",
            enabled ? "ON" : "OFF", loopStartMs, loopEndMs);

        await _eventBus.PublishAsync(new PlayerLoopChangedEvent(enabled), ct);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        string?      playlistId;
        string?      endedItemId;
        string?      endedAssetId;
        List<string> playedItemIds;

        await _lock.WaitAsync(ct);
        try
        {
            await _audioEngine.CancelScheduledSweepersAsync(ct); // Bug 2 fix
            // User-initiated Stop: apply the configurable stop fade-out. A natural track end
            // uses the track's own FadeOut cue marker instead (handled elsewhere).
            await _audioEngine.StopAsync(_audioSettings?.StopFadeoutMs ?? 0, ct);

            playlistId    = _currentPlaylist?.PlaylistId;
            endedItemId   = _currentItemId;
            endedAssetId  = _currentAssetId;

            // A manually stopped track is done, even if only part of it aired: drop it from the
            // queue, then leave the player EMPTY. The queue cursor stays on the item Play will
            // start next — the same "nothing playing, first queued item is current" shape a
            // queue swap produces (see LoadPlaylistAsync) — but nothing is loaded, so that item
            // stays visible at position 1 until the operator actually starts it.
            //
            // Only when a track is loaded (Playing/Paused): while Idle the player is already
            // empty and there is nothing to retire — dequeuing would drop a track nobody aired.
            playedItemIds = new List<string>();
            if (_state is SessionState.Playing or SessionState.Paused)
            {
                playedItemIds   = DequeueCurrentItemLocked();
                _currentItemId  = null;
                _currentAssetId = null;
                _currentAsset   = null;
                _cueEndMs       = null;
            }

            _accumulatedPositionMs = 0;
            _segmentStartedAt = null;
            _trackStartedAt = null;
            _outroCueMs = null;
            _cueFadeOutMs = null;
            _cueStartNextMs = null;
            _startNextTriggeredItemId = null;
            _transitionCrossfadeOverride = null;
            _overlaySweeperItemIds = new List<string>();
            _state = SessionState.Idle;
        }
        finally { _lock.Release(); }

        await DeleteQueueItemsAsync(playedItemIds, ct);

        // BassAudioEngine.StopAsync does not emit TrackEndedEvent — we do it here.
        // endedAssetId is null for external (non-library) items; the ended event still fires
        // so the playout-log entry gets closed.
        if (endedItemId is not null)
        {
            await _eventBus.PublishAsync(
                new PlaylistItemEndedEvent(endedItemId, endedAssetId, "STOPPED"), ct);
        }

        await _eventBus.PublishAsync(
            new PlaylistStoppedEvent(playlistId ?? string.Empty, "MANUAL"), ct);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        string?      playlistId;
        string?      playlistName;
        PlaylistType? playlistType;
        int          itemCountBeforeClear;

        await _lock.WaitAsync(ct);
        try
        {
            await _audioEngine.CancelScheduledSweepersAsync(ct);
            playlistId           = _currentPlaylist?.PlaylistId;
            playlistName         = _currentPlaylist?.Name;
            playlistType         = _currentPlaylist?.PlaylistType;
            itemCountBeforeClear = _items.Count;
            // Keep _currentPlaylist non-null — nulling it causes GetCurrentItemsAsync
            // to auto-reload from DB (which still has items until ClearItemsAsync runs).
            _items = Array.Empty<PlaylistItem>();

            // Pending sweeper-overlay transitions belonged to the old queue — always drop them.
            _transitionCrossfadeOverride = null;
            _overlaySweeperItemIds = new List<string>();

            if (_state == SessionState.Idle)
            {
                // Nothing is playing — full reset (original behaviour).
                _currentIndex   = -1;
                _currentItemId  = null;
                _currentAssetId = null;
                _currentAsset   = null;

                _accumulatedPositionMs = 0;
                _segmentStartedAt = null;
                _trackStartedAt   = null;
                _outroCueMs       = null;
                _cueFadeOutMs     = null;
                _cueStartNextMs   = null;
            }
            else
            {
                // A track is live: clear only the queue. The playing track and all of its
                // playback state (item id, asset, cue markers, position) are preserved so it
                // finishes naturally instead of being cut off. _currentIndex = -1 marks "the
                // live track is no longer an element of _items"; when it ends, DequeueCurrent
                // ItemLocked positions the cursor at the head of whatever queue is present.
                // In AUTO this yields "let the current track finish, then continue with the
                // new queue"; in Manual/Live Assist the new head is merely cued (no auto-play).
                _currentIndex = -1;
            }
        }
        finally { _lock.Release(); }

        if (playlistId is not null)
            await _playlistRepo.ClearItemsAsync(playlistId, ct);

        _logger.LogInformation(
            "ClearAsync: cleared {Count} item(s) from playlist '{Name}' [id={PlaylistId}, type={Type}]",
            itemCountBeforeClear, playlistName, playlistId, playlistType);

        if (playlistId is not null)
            await _eventBus.PublishAsync(new PlaylistQueueReloadedEvent(playlistId), ct);
    }

    public async Task ChangeModeAsync(PlaylistMode newMode, CancellationToken ct = default)
    {
        PlaylistMode previous;

        await _lock.WaitAsync(ct);
        try
        {
            if (_mode == newMode) return;
            previous = _mode;
            _mode    = newMode;
        }
        finally { _lock.Release(); }

        _logger.LogInformation("Mode changed: {From} → {To}", previous, newMode);
        await _eventBus.PublishAsync(new PlaylistModeChangedEvent(previous, newMode), ct);
    }

    /// <summary>
    /// Plays a specific asset directly — used by EventScheduler PLAY_FILE action.
    /// Does not affect the playlist queue position.
    /// </summary>
    public async Task PlayAssetDirectlyAsync(string assetId, CancellationToken ct = default)
    {
        var asset = await _assetRepo.GetByIdAsync(assetId, ct);

        if (asset is null)
        {
            _logger.LogWarning("Asset {AssetId} not found", assetId);
            return;
        }

        await _audioEngine.CancelScheduledSweepersAsync(ct);

        if (asset.AssetType == AssetType.InternetStream)
        {
            if (asset.StreamUrl is null)
            {
                _logger.LogWarning("InternetStream {AssetId} has no StreamUrl", assetId);
                return;
            }
            await _audioEngine.LoadInternetStreamAsync(assetId, asset.StreamUrl, ct);
            await _audioEngine.PlayAsync(ct);
            return;
        }

        if (asset.RdmFilePath is null)
        {
            _logger.LogWarning("Asset {AssetId} has no file path", assetId);
            return;
        }

        await _audioEngine.LoadTrackAsync(assetId, asset.RdmFilePath, CuePointBuilder.Build(asset, null, PlaylistMode.Manual, 0), null, ct);
        await _audioEngine.PlayAsync(ct);

        // A fresh stream carries no loop sync — re-arm it, so the toggle means the same thing
        // for a directly played asset as for a queued one.
        bool loopCurrent;
        await _lock.WaitAsync(ct);
        try { loopCurrent = _loopCurrent; }
        finally { _lock.Release(); }

        await _audioEngine.SetPlayerLoopAsync(
            loopCurrent,
            asset.CueStart.HasValue ? (uint)(asset.CueStart.Value * 1000) : 0,
            asset.CueEnd.HasValue   ? (uint)(asset.CueEnd.Value   * 1000) : 0,
            ct);
    }

    public async Task PauseAsync(CancellationToken ct = default)
    {
        await _audioEngine.PauseAsync(ct);

        await _lock.WaitAsync(ct);
        try
        {
            if (_segmentStartedAt.HasValue)
            {
                _accumulatedPositionMs += (uint)(DateTime.UtcNow - _segmentStartedAt.Value).TotalMilliseconds;
                _segmentStartedAt = null;
            }
            _state = SessionState.Paused;
        }
        finally { _lock.Release(); }
    }

    public async Task ResetAsync(CancellationToken ct = default)
    {
        await _audioEngine.ResetAsync(ct);

        await _lock.WaitAsync(ct);
        try
        {
            _accumulatedPositionMs = 0;
            if (_segmentStartedAt.HasValue)
            {
                _segmentStartedAt = DateTime.UtcNow;
            }
        }
        finally { _lock.Release(); }
    }

    public NowPlayingInfo GetNowPlayingInfo()
    {
        _lock.Wait();
        try
        {
            uint posMs = _segmentStartedAt.HasValue
                ? _accumulatedPositionMs + (uint)(DateTime.UtcNow - _segmentStartedAt.Value).TotalMilliseconds
                : _accumulatedPositionMs;

            int nextIdx = NextItemIndexLocked();
            var nextItem = nextIdx < _items.Count ? _items[nextIdx] : null;

            return new NowPlayingInfo(
                CurrentAsset: _currentAsset,
                PositionMs: posMs,
                OutroCueMs: _outroCueMs,
                TrackStartedAt: _trackStartedAt,
                NextItem: nextItem,
                Mode: _mode,
                State: _state,
                CurrentItemId: _currentItemId,
                LoopCurrent: _loopCurrent
            );
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── Live playlist editing (UI-002B) ──────────────────────────────────────

    public async Task<IReadOnlyList<PlaylistItem>> GetCurrentItemsAsync(CancellationToken ct = default)
    {
        // Auto-load the most recent playlist on first access (e.g. app startup).
        // Does not create a new playlist — only loads an existing one.
        await _lock.WaitAsync(ct);
        bool hasPlaylist = _currentPlaylist is not null;
        _lock.Release();

        if (!hasPlaylist)
        {
            var onAir = await _playlistRepo.GetOnAirPlaylistAsync(_studioContext.StudioId, ct);
            if (onAir is not null)
                await LoadPlaylistAsync(onAir.PlaylistId, ct);
        }

        await _lock.WaitAsync(ct);
        try { return _items; }
        finally { _lock.Release(); }
    }

    public async Task<string> AddItemAsync(string assetId, int position, CancellationToken ct = default)
    {
        var playlistId = await GetOrCreatePlaylistIdAsync(ct);
        var itemId     = Guid.NewGuid().ToString();

        // Sync in-memory list with DB before position calculation to avoid duplicate-key conflicts
        await RefreshItemsAsync(playlistId, ct);

        var item = new PlaylistItem
        {
            ItemId       = itemId,
            PlaylistId   = playlistId,
            AssetId      = assetId,
            Position     = 0,
            ItemType     = PlaylistItemType.Asset,
            SegueType    = SegueType.Auto,
            AutoLinkNext = false
        };

        var itemsToAdd = new List<PlaylistItem>();
        var itemsToUpdate = new List<PlaylistItem>();

        await _lock.WaitAsync(ct);
        try
        {
            var filtered = _items.OrderBy(x => x.Position).ToList();
            filtered.Insert(Math.Max(0, Math.Min(position, filtered.Count)), item);

            for (int i = 0; i < filtered.Count; i++)
            {
                var expectedPos = (uint)(i + 1);
                if (filtered[i].ItemId == itemId)
                {
                    itemsToAdd.Add(CopyItem(filtered[i], position: expectedPos));
                }
                else if (filtered[i].Position != expectedPos)
                {
                    itemsToUpdate.Add(CopyItem(filtered[i], position: expectedPos));
                }
            }
        }
        finally { _lock.Release(); }

        // Renumbering may shift existing rows up OR down — the queue does not always
        // start at position 1 (e.g. after the first tracks were played/removed), so no
        // single-pass ordering avoids hitting the uq_item_position unique key.
        // Two-phase: 1) park every affected row in a high, collision-free band,
        // 2) write the final positions, then 3) insert the new item into the freed slot.
        const uint tempBase = 1_000_000;
        for (int i = 0; i < itemsToUpdate.Count; i++)
            await _playlistRepo.UpdateItemAsync(CopyItem(itemsToUpdate[i], position: tempBase + (uint)i), ct);
        foreach (var i in itemsToUpdate)
            await _playlistRepo.UpdateItemAsync(i, ct);
        foreach (var i in itemsToAdd)
            await _playlistRepo.AddItemAsync(i, ct);

        await RefreshItemsAsync(playlistId, ct);

        await TryEnqueueVariableDurationRescanAsync(assetId, ct);

        return itemId;
    }

    /// <summary>
    /// For IsVariableDuration assets (content re-recorded in place under the same file path,
    /// e.g. news bulletins), refreshes duration_ms and cue Start/End in the DB as soon as the
    /// asset enters the live queue — otherwise the queue's displayed duration and the computed
    /// start times of subsequent items stay stuck at whatever was measured at import time.
    /// Reuses the same CueAnalysisQueue/AnalyzeCuePointsAsync pipeline as post-import analysis.
    /// </summary>
    private async Task TryEnqueueVariableDurationRescanAsync(string? assetId, CancellationToken ct)
    {
        if (assetId is null || _audioSettings is null)
            return;

        var asset = await _assetRepo.GetByIdAsync(assetId, ct);
        if (asset is null || !asset.IsVariableDuration || asset.RdmFilePath is null)
            return;

        try
        {
            await _cueAnalysisQueue.Writer.WriteAsync(
                new CueAnalysisTask(
                    asset.AssetId, asset.RdmFilePath,
                    (double)_audioSettings.SilenceStartThresholdDb,
                    (double)_audioSettings.SilenceMixThresholdDb,
                    (double)_audioSettings.SilenceEndThresholdDb),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enqueue variable-duration rescan for asset {AssetId}", assetId);
        }
    }

    public async Task<string> AddExternalItemAsync(string filePath, string? title, string? artist, uint? durationMs, int position, CancellationToken ct = default)
    {
        var playlistId = await GetOrCreatePlaylistIdAsync(ct);
        var itemId     = Guid.NewGuid().ToString();

        // External (non-library) file: play from disk, no asset row. Title/artist/duration
        // captured at add time (ID3 for Explorer drops, EXTINF for M3U) live in the
        // dummy_* columns — the item is external iff ExternalFilePath is set.
        var item = new PlaylistItem
        {
            ItemId           = itemId,
            PlaylistId       = playlistId,
            ExternalFilePath = filePath,
            DummyLabel       = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            DummyNote        = string.IsNullOrWhiteSpace(artist) ? null : artist.Trim(),
            DummyDurationMs  = durationMs,
            Position         = 0,
            ItemType         = PlaylistItemType.Asset,
            SegueType        = SegueType.Auto,
            AutoLinkNext     = false
        };

        var itemsToAdd = new List<PlaylistItem>();
        var itemsToUpdate = new List<PlaylistItem>();

        await _lock.WaitAsync(ct);
        try
        {
            var filtered = _items.OrderBy(x => x.Position).ToList();
            filtered.Insert(Math.Max(0, Math.Min(position, filtered.Count)), item);

            for (int i = 0; i < filtered.Count; i++)
            {
                var expectedPos = (uint)(i + 1);
                if (filtered[i].ItemId == itemId)
                {
                    itemsToAdd.Add(CopyItem(filtered[i], position: expectedPos));
                }
                else if (filtered[i].Position != expectedPos)
                {
                    itemsToUpdate.Add(CopyItem(filtered[i], position: expectedPos));
                }
            }
        }
        finally { _lock.Release(); }

        // Two-phase renumber (see AddItemAsync) — park affected rows in a high,
        // collision-free band, write their final positions, then insert the new item.
        const uint tempBase = 1_000_000;
        for (int i = 0; i < itemsToUpdate.Count; i++)
            await _playlistRepo.UpdateItemAsync(CopyItem(itemsToUpdate[i], position: tempBase + (uint)i), ct);
        foreach (var i in itemsToUpdate)
            await _playlistRepo.UpdateItemAsync(i, ct);
        foreach (var i in itemsToAdd)
            await _playlistRepo.AddItemAsync(i, ct);

        await RefreshItemsAsync(playlistId, ct);
        return itemId;
    }

    public async Task RemoveItemAsync(string itemId, CancellationToken ct = default)
    {
        var playlistId = GetCurrentPlaylistId();
        await _playlistRepo.RemoveItemAsync(itemId, ct);
        await RefreshItemsAsync(playlistId, ct);
    }

    public async Task RemoveCurrentItemAsync(CancellationToken ct = default)
    {
        string? itemId;
        string? playlistId;

        await _lock.WaitAsync(ct);
        try
        {
            itemId     = _currentItemId;
            playlistId = _currentPlaylist?.PlaylistId;
            if (itemId is null || playlistId is null) return;
        }
        finally { _lock.Release(); }

        await _playlistRepo.RemoveItemAsync(itemId, ct);

        await _lock.WaitAsync(ct);
        try
        {
            _currentItemId  = null;
            _currentAssetId = null;
            _currentAsset   = null;
            _currentIndex   = -1;
        }
        finally { _lock.Release(); }

        await RefreshItemsAsync(playlistId, ct);
    }

    public async Task ReorderItemAsync(string itemId, int newPosition, CancellationToken ct = default)
    {
        var playlistId = GetCurrentPlaylistId();
        var itemsToUpdate = new List<PlaylistItem>();

        await _lock.WaitAsync(ct);
        try 
        {
            var existing = _items.FirstOrDefault(i => i.ItemId == itemId);
            if (existing is null)
                throw new InvalidOperationException($"PlaylistItem '{itemId}' not found in active playlist.");
                
            var filtered = _items.Where(x => x.ItemId != itemId).OrderBy(x => x.Position).ToList();
            filtered.Insert(Math.Max(0, Math.Min(newPosition, filtered.Count)), existing);
            
            for (int i = 0; i < filtered.Count; i++)
            {
                if (filtered[i].Position != (uint)(i + 1))
                {
                    itemsToUpdate.Add(CopyItem(filtered[i], position: (uint)(i + 1)));
                }
            }
        }
        finally { _lock.Release(); }

        // Reordering rotates positions (e.g. A→3, C→2, B→1), so no single-pass
        // ordering avoids hitting the uq_item_position unique key. Two-phase:
        // 1) park every affected row in a high, collision-free band,
        // 2) write the final positions.
        const uint tempBase = 1_000_000;
        for (int i = 0; i < itemsToUpdate.Count; i++)
            await _playlistRepo.UpdateItemAsync(CopyItem(itemsToUpdate[i], position: tempBase + (uint)i), ct);
        foreach (var item in itemsToUpdate)
            await _playlistRepo.UpdateItemAsync(item, ct);

        await RefreshItemsAsync(playlistId, ct);
    }

    public async Task PatchItemAsync(
        string itemId,
        uint?  crossfadeMs,
        int?   leadInMs,
        uint?  trimStartMs,
        uint?  trimEndMs,
        string? segueType,
        bool?  autoLinkNext,
        string? volumeEnvelope = null,
        CancellationToken ct = default)
    {
        var playlistId = GetCurrentPlaylistId();
        PlaylistItem? existing;

        await _lock.WaitAsync(ct);
        try { existing = _items.FirstOrDefault(i => i.ItemId == itemId); }
        finally { _lock.Release(); }

        if (existing is null)
            throw new InvalidOperationException($"PlaylistItem '{itemId}' not found in active playlist.");

        SegueType parsedSegue = existing.SegueType;
        if (segueType is not null && Enum.TryParse<SegueType>(segueType, ignoreCase: true, out var s))
            parsedSegue = s;

        var updated = CopyItem(
            existing,
            crossfadeMs:    crossfadeMs    ?? existing.CrossfadeMs,
            leadInMs:       leadInMs       ?? existing.LeadInMs,
            trimStartMs:    trimStartMs    ?? existing.TrimStartMs,
            trimEndMs:      trimEndMs      ?? existing.TrimEndMs,
            segueType:      parsedSegue,
            autoLinkNext:   autoLinkNext   ?? existing.AutoLinkNext,
            volumeEnvelope: volumeEnvelope ?? existing.VolumeEnvelope);

        await _playlistRepo.UpdateItemAsync(updated, ct);
        await RefreshItemsAsync(playlistId, ct);
    }

    private string GetCurrentPlaylistId()
    {
        _lock.Wait();
        try
        {
            if (_currentPlaylist is null)
                throw new InvalidOperationException("No playlist is currently loaded.");
            return _currentPlaylist.PlaylistId;
        }
        finally { _lock.Release(); }
    }

    private async Task<string> GetOrCreatePlaylistIdAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        var current = _currentPlaylist;
        _lock.Release();

        if (current is not null)
            return current.PlaylistId;

        var onAir = await _playlistRepo.GetOnAirPlaylistAsync(_studioContext.StudioId, ct);
        if (onAir is not null)
        {
            await LoadPlaylistAsync(onAir.PlaylistId, ct);
            await _lock.WaitAsync(ct);
            var loaded = _currentPlaylist;
            _lock.Release();
            if (loaded is not null) return loaded.PlaylistId;
        }

        var now      = DateTime.UtcNow;
        var playlist = new Playlist
        {
            PlaylistId   = Guid.NewGuid().ToString(),
            StudioId     = _studioContext.StudioId,
            Name         = $"On-Air {now:yyyy-MM-dd HH:mm}",
            PlaylistType = PlaylistType.OnAir,
            CreatedAt    = now
        };
        await _playlistRepo.CreateAsync(playlist, ct);

        await _lock.WaitAsync(ct);
        try
        {
            _currentPlaylist = playlist;
            _items           = Array.Empty<PlaylistItem>();
            _currentIndex    = 0;
            _currentItemId   = null;
            _currentAssetId  = null;
            _currentAsset    = null;
            _state           = SessionState.Idle;
        }
        finally { _lock.Release(); }

        _logger.LogInformation("Auto-created session playlist {PlaylistId} for studio {StudioId}",
            playlist.PlaylistId, _studioContext.StudioId);

        return playlist.PlaylistId;
    }

    private async Task RefreshItemsAsync(string playlistId, CancellationToken ct)
    {
        var items = await _playlistRepo.GetItemsAsync(playlistId, ct);
        await _lock.WaitAsync(ct);
        try
        {
            _items = items;
            if (_currentItemId is not null)
            {
                var idx = items.ToList().FindIndex(i => i.ItemId == _currentItemId);
                if (idx >= 0) _currentIndex = idx;
            }
        }
        finally { _lock.Release(); }
    }

    private static PlaylistItem CopyItem(
        PlaylistItem src,
        uint?        position       = null,
        uint?        crossfadeMs    = null,
        int?         leadInMs       = null,
        uint?        trimStartMs    = null,
        uint?        trimEndMs      = null,
        SegueType?   segueType      = null,
        bool?        autoLinkNext   = null,
        string?      volumeEnvelope = null,
        string?      itemId         = null,
        string?      playlistId     = null) => new()
    {
        ItemId           = itemId     ?? src.ItemId,
        PlaylistId       = playlistId ?? src.PlaylistId,
        AssetId          = src.AssetId,
        Position         = position       ?? src.Position,
        ItemType         = src.ItemType,
        ExternalFilePath = src.ExternalFilePath,
        DummyLabel       = src.DummyLabel,
        DummyNote        = src.DummyNote,
        DummyDurationMs  = src.DummyDurationMs,
        CrossfadeMs      = crossfadeMs    ?? src.CrossfadeMs,
        LeadInMs         = leadInMs       ?? src.LeadInMs,
        TrimStartMs      = trimStartMs    ?? src.TrimStartMs,
        TrimEndMs        = trimEndMs      ?? src.TrimEndMs,
        SegueType        = segueType      ?? src.SegueType,
        ScheduledAt      = src.ScheduledAt,
        AutoLinkNext     = autoLinkNext   ?? src.AutoLinkNext,
        VolumeEnvelope   = volumeEnvelope ?? src.VolumeEnvelope
    };

    // ── IAsyncDisposable ──────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        _eventBus.Unsubscribe(_startNextHandler);
        _eventBus.Unsubscribe(_fadeOutHandler);
        _eventBus.Unsubscribe(_startedHandler);
        _eventBus.Unsubscribe(_endedHandler);
        _eventBus.Unsubscribe(_cueEndHandler);
        _eventBus.Unsubscribe(_loopedHandler);

        await _cts.CancelAsync();
        _cts.Dispose();
        _lock.Dispose();
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private async Task OnTrackStartNextAsync(TrackStartNextReachedEvent evt, CancellationToken ct)
    {
        PlaylistMode mode;
        uint effectiveCrossfadeMs;
        uint? cueFadeOutMs;
        uint? cueEndMs;
        uint  assetDurationMs;

        await _lock.WaitAsync(ct);
        try
        {
            if (evt.AssetId != _currentAssetId) return;
            // Must precede the claim below: recording a StartNext that never happened would
            // block the real advance once the loop is switched off.
            if (_loopCurrent) return;
            if (_startNextTriggeredItemId == _currentItemId) return;
            _startNextTriggeredItemId = _currentItemId;

            mode = _mode;
            effectiveCrossfadeMs = TransitionCrossfadeMs(_currentIndex);
            cueFadeOutMs         = _cueFadeOutMs;
            cueEndMs             = _cueEndMs;
            assetDurationMs      = _currentAsset?.DurationMs ?? 0;
        }
        finally { _lock.Release(); }

        if (mode != PlaylistMode.Auto) return;

        // Effective broadcast end: CueEnd if set, otherwise physical DurationMs
        uint effectiveEndMs = cueEndMs ?? assetDurationMs;

        uint fadeDurationMs = effectiveCrossfadeMs;

        // When CueFadeOut acts as the StartNext trigger (no explicit CueStartNext) and no
        // crossfade is configured, derive the fade duration from remaining broadcast time.
        if (fadeDurationMs == 0 && cueFadeOutMs.HasValue && effectiveEndMs > cueFadeOutMs.Value)
            fadeDurationMs = effectiveEndMs - cueFadeOutMs.Value;

        // CueFadeOut (when both markers set) fires its own TrackFadeOutReachedEvent independently
        // and is handled by OnTrackFadeOutAsync — no delay coordination needed here.
        _logger.LogDebug("AUTO: crossfading to next at StartNext marker for {AssetId}", evt.AssetId);
        await AdvanceAsync(fadeDurationMs, 0, ct);
    }

    private async Task OnTrackFadeOutAsync(TrackFadeOutReachedEvent evt, CancellationToken ct)
    {
        uint effectiveCrossfadeMs;

        await _lock.WaitAsync(ct);
        try
        {
            // If the asset has already advanced (StartNext fired first), assetId won't match
            // the new current asset — this naturally prevents a double-fade.
            if (evt.AssetId != _currentAssetId) return;
            // A looping track must not fade its own tail — every pass would end in silence.
            if (_loopCurrent) return;
            effectiveCrossfadeMs = TransitionCrossfadeMs(_currentIndex);
        }
        finally { _lock.Release(); }

        if (effectiveCrossfadeMs > 0)
            await _audioEngine.FadeOutAsync(effectiveCrossfadeMs, ct);
    }

    private async Task OnTrackCueEndReachedAsync(TrackCueEndReachedEvent evt, CancellationToken ct)
    {
        PlaylistMode mode;

        await _lock.WaitAsync(ct);
        try
        {
            if (evt.AssetId != _currentAssetId) return;
            // CueEnd is the loop point: the engine already rewound there at mixtime, so the
            // track is playing on. Neither advancing nor stopping applies.
            if (_loopCurrent) return;
            mode = _mode;
        }
        finally { _lock.Release(); }

        if (mode == PlaylistMode.Auto)
        {
            // Auto: advance immediately — LoadAndPlayItemAsync → FreePlaylistStream stops the old stream.
            _logger.LogDebug("AUTO: CueEnd reached for {AssetId} — advancing", evt.AssetId);
            await NextTrackAsync(ct);
        }
        else
        {
            // Manual/LiveAssist: the stream is still active past CueEnd — stop it explicitly,
            // then let OnTrackEndedAsync handle the dequeue (same as natural end).
            _logger.LogDebug("{Mode}: CueEnd reached for {AssetId} — stopping", mode, evt.AssetId);
            // Natural end at the CueEnd marker — cut instantly (the FadeOut marker already
            // faded the tail); the user Stop fade-out does not apply here.
            await _audioEngine.StopAsync(0, ct);
            await OnTrackEndedAsync(new TrackEndedEvent(evt.AssetId, "CUE_END"), ct);
        }
    }

    /// A looping track rewound. It did not end and no TrackStarted follows, so the position
    /// clock has to be rewound by hand — otherwise the reported position keeps running past the
    /// end of the track for every pass after the first.
    private async Task OnPlayerLoopedAsync(PlayerLoopedEvent evt, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_currentItemId is null) return;
            _accumulatedPositionMs = evt.PositionMs;
            // Only while actually playing: a paused deck must keep its frozen position, which is
            // held in _accumulatedPositionMs with no running segment.
            if (_segmentStartedAt.HasValue) _segmentStartedAt = DateTime.UtcNow;
        }
        finally { _lock.Release(); }
    }

    private async Task OnTrackStartedAsync(TrackStartedEvent evt, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        string?   itemId;
        string?   publishAssetId;
        string    title;
        string?   artist;
        uint      durationMs;
        AssetType assetType;
        string?   formatId;
        double    vuOffsetDb = 0.0;
        Asset?    assetToCountAsPlayed = null;
        try
        {
            // External (non-library) item: no _currentAsset. The engine echoes the ItemId as
            // the low-level track id, so correlate against _currentItemId instead of _currentAssetId.
            if (_currentAsset is null)
            {
                var ext = _currentItemId is not null
                    ? _items.FirstOrDefault(i => i.ItemId == _currentItemId)
                    : null;

                if (_currentItemId is null || ext?.ExternalFilePath is null || evt.AssetId != _currentItemId)
                    return;

                if (!_trackStartedAt.HasValue)
                {
                    _accumulatedPositionMs = 0;
                    _segmentStartedAt = DateTime.UtcNow;
                    _trackStartedAt = DateTime.UtcNow;
                }
                else
                {
                    _segmentStartedAt = DateTime.UtcNow;
                }
                _state = SessionState.Playing;
                _hasEverPlayed = true;
                itemId         = _currentItemId;
                publishAssetId = null;                 // no asset → history logs TempTitle/TempArtist
                title          = ext.DummyLabel ?? CuePointBuilder.DeriveTitleFromPath(ext.ExternalFilePath);
                artist         = ext.DummyNote;
                durationMs     = ext.DummyDurationMs ?? 0;
                assetType      = AssetType.Track;
                formatId       = null;
            }
            else
            {
                if (evt.AssetId != _currentAssetId || _currentItemId is null)
                    return;

                // Count a play once per genuine track start — not on resume-after-pause
                // (_trackStartedAt already set) and not for continuous internet streams.
                if (!_trackStartedAt.HasValue)
                {
                    // Playback was seeked to CueStart — start counting from there, not from 0.
                    _accumulatedPositionMs = _cueStartMs ?? 0;
                    _segmentStartedAt = DateTime.UtcNow;
                    _trackStartedAt = DateTime.UtcNow;
                    if (_currentAsset.AssetType != AssetType.InternetStream)
                        assetToCountAsPlayed = _currentAsset;
                }
                else
                {
                    _segmentStartedAt = DateTime.UtcNow;
                }
                _state = SessionState.Playing;
                _hasEverPlayed = true;
                itemId         = _currentItemId;
                publishAssetId = evt.AssetId;
                title          = _currentAsset.Title;
                artist         = _currentAsset.Artist;
                // Stale for variable-duration content (e.g. a re-recorded news bulletin) —
                // prefer the length just measured live off the actual file, if we have one.
                durationMs     = _liveDurationMsOverride ?? _currentAsset.DurationMs;
                assetType      = _currentAsset.AssetType;
                formatId       = _currentAsset.FormatId;

                // Display-only VU meter correction — does not touch playback gain/BASS_ATTRIB_VOL.
                if (_audioSettings is { LoudnessNormalization: true } && _currentAsset.LoudnessLufs is decimal lufs)
                    vuOffsetDb = Math.Clamp((double)_audioSettings.LoudnessTargetLufs - (double)lufs, -12.0, 12.0);
            }
        }
        finally { _lock.Release(); }

        if (assetToCountAsPlayed is not null)
        {
            try
            {
                await _assetRepo.UpdatePlayCountAsync(
                    assetToCountAsPlayed.AssetId, assetToCountAsPlayed.PlayCount + 1, DateTime.UtcNow, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update play count for asset {AssetId}", assetToCountAsPlayed.AssetId);
            }
        }

        // Publish playlist-level event with ItemId (Bug 1 fix — avoids duplicate-asset correlation errors)
        await _eventBus.PublishAsync(
            new PlaylistItemStartedEvent(itemId!, publishAssetId, title, artist, durationMs, assetType, formatId, vuOffsetDb), ct);
    }

    private async Task LoadAndPlayInternetStreamAsync(Asset asset, CancellationToken ct)
    {
        if (asset.StreamUrl is null)
        {
            _logger.LogWarning("InternetStream {AssetId} has no StreamUrl — advancing", asset.AssetId);
            await AdvanceAsync(0, 0, ct);
            return;
        }

        await _lock.WaitAsync(ct);
        _currentAsset                     = asset;
        _outroCueMs                       = null;
        _cueFadeOutMs                     = null;
        _cueStartNextMs                   = null;
        _cueEndMs                         = null;
        _cueStartMs                       = null;
        _transitionCrossfadeOverride      = null;
        _overlaySweeperItemIds            = new List<string>();
        _startNextTriggeredItemId         = null;
        _lock.Release();

        try
        {
            await _audioEngine.LoadInternetStreamAsync(asset.AssetId, asset.StreamUrl, ct);
            await _audioEngine.PlayAsync(ct);
            _logger.LogInformation("Internet stream started: {AssetId} {StreamUrl}", asset.AssetId, asset.StreamUrl);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Internet stream {AssetId} failed to connect — advancing", asset.AssetId);
            await AdvanceAsync(0, 0, ct);
        }
    }

    private async Task OnTrackEndedAsync(TrackEndedEvent evt, CancellationToken ct)
    {
        string? itemId;
        string? endedAssetId;
        bool autoAdvance = false;
        List<string> playedItemIds = new();

        await _lock.WaitAsync(ct);
        try
        {
            if (_currentItemId is null)
                return;

            // A looping track does not end. Scoped to the natural end so an explicit stop or
            // skip — which reach this handler with their own reason — still retire the item.
            if (_loopCurrent && evt.EndReason == "NATURAL")
                return;

            // External (non-library) items are correlated by ItemId (the engine echoes it as
            // the low-level track id); library items by AssetId.
            bool isExternal = _currentAsset is null;
            if (isExternal ? evt.AssetId != _currentItemId : evt.AssetId != _currentAssetId)
                return;

            endedAssetId = isExternal ? null : _currentAssetId;

            // In AUTO mode, if StartNext already claimed advancement for this item, skip.
            // This prevents double-increment when SYNC_POS and SYNC_END fire in quick succession.
            if (_mode == PlaylistMode.Auto && _startNextTriggeredItemId == _currentItemId)
                return;

            itemId = _currentItemId;
            _accumulatedPositionMs = 0;
            _segmentStartedAt = null;
            _trackStartedAt = null;
            _outroCueMs = null;
            _state = SessionState.Idle;

            if (_mode == PlaylistMode.Auto)
            {
                // Claim advancement so OnTrackStartNextAsync won't double-advance.
                // The actual dequeue happens in AdvanceAsync (via NextTrackAsync below).
                _startNextTriggeredItemId = _currentItemId;
                autoAdvance = true;
            }
            else
            {
                // Live Assist / Manual: the track finished on its own. Drop it from the queue
                // (RadioDJ-style) and stay Idle (no auto-play) with the player EMPTY — the
                // cursor rests on the item Play will start, but loading it here would hide it
                // from the queue view while never actually airing it. Same shape as StopAsync.
                playedItemIds   = DequeueCurrentItemLocked();
                _currentItemId  = null;
                _currentAssetId = null;
                _currentAsset   = null;
            }
        }
        finally { _lock.Release(); }

        await DeleteQueueItemsAsync(playedItemIds, ct);

        await _eventBus.PublishAsync(
            new PlaylistItemEndedEvent(itemId!, endedAssetId, evt.EndReason), ct);

        if (autoAdvance)
            await NextTrackAsync(ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Sets _currentItemId/_currentAssetId for the item at _currentIndex,
    /// skipping leading DUMMY items. Returns the item or null (empty/all-dummy playlist).
    /// Must be called from PlayAsync; uses full lock protocol.
    /// </summary>
    private async Task<PlaylistItem?> PickCurrentItemAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_items.Count == 0)
            {
                _logger.LogWarning("PlayAsync called with no playlist loaded");
                return null;
            }

            if (_currentIndex < 0) _currentIndex = 0;
            SkipDummies();

            if (_currentIndex >= _items.Count)
            {
                _logger.LogWarning("Playlist contains only DUMMY items");
                return null;
            }

            var item = _items[_currentIndex];
            _currentItemId  = item.ItemId;
            _currentAssetId = item.AssetId;
            return item;
        }
        finally { _lock.Release(); }
    }

    private async Task LoadAndPlayItemAsync(
        PlaylistItem item, CancellationToken ct, uint crossfadeMs = 0, uint fadeDelayMs = 0)
    {
        if (item.AssetId is null)
        {
            if (item.ExternalFilePath is not null)
            {
                await _lock.WaitAsync(ct);
                _currentAsset = null;
                _outroCueMs   = null;
                _cueStartMs   = null;   // external files have no cue markers — play from 0
                _transitionCrossfadeOverride = null;
                _overlaySweeperItemIds = new List<string>();
                _lock.Release();

                try
                {
                    await _audioEngine.LoadTrackAsync(item.ItemId, item.ExternalFilePath, Array.Empty<AssetCuePoint>(), CuePointBuilder.DeserializeEnvelope(item.VolumeEnvelope), ct);
                    await _audioEngine.PlayAsync(ct);
                }
                catch (FileNotFoundException ex)
                {
                    _logger.LogWarning(ex, "External file missing on disk ({Path}) for PlaylistItem {ItemId} — advancing", item.ExternalFilePath, item.ItemId);
                    await AdvanceAsync(0, 0, ct);
                }
            }
            else
            {
                _logger.LogWarning("PlaylistItem {ItemId} has no AssetId and no ExternalFilePath — skipping", item.ItemId);
            }
            return;
        }

        var asset = await _assetRepo.GetByIdAsync(item.AssetId, ct);

        if (asset is null)
        {
            _logger.LogWarning("Asset {AssetId} not found for PlaylistItem {ItemId} — advancing", item.AssetId, item.ItemId);
            await AdvanceAsync(0, 0, ct);
            return;
        }

        if (asset.IsDamaged)
        {
            _logger.LogWarning("Asset {AssetId} is marked damaged — advancing", asset.AssetId);
            await AdvanceAsync(0, 0, ct);
            return;
        }

        if (asset.AssetType == AssetType.InternetStream)
        {
            await LoadAndPlayInternetStreamAsync(asset, ct);
            return;
        }

        if (asset.RdmFilePath is null)
        {
            _logger.LogWarning("Asset {AssetId} has no file path — advancing", asset.AssetId);
            await AdvanceAsync(0, 0, ct);
            return;
        }

        // Plan any floating sweeper overlays for this track's outgoing transition (needs async
        // asset look-ups, so compute it before taking the lock).
        var transition = await PlanSweeperOverlaysAsync(asset, ct);

        // Variable-duration content (e.g. a news bulletin re-recorded in place under the same
        // file path): the stored CueStart/CueEnd were analyzed against a previous recording and
        // can be stale — trusting CueEnd as a hard stop would cut a longer replacement short.
        // Re-detect Start/End live from the current file instead, same silence-threshold
        // detector used for AUX deck loads. Falls back to the stored markers on failure.
        double? liveCueStart   = null;
        double? liveCueEnd     = null;
        uint?   liveDurationMs = null;
        if (asset.IsVariableDuration && _audioSettings is not null)
        {
            try
            {
                var detected = await _audioEngine.AnalyzeCuePointsAsync(
                    asset.RdmFilePath,
                    (double)_audioSettings.SilenceStartThresholdDb,
                    (double)_audioSettings.SilenceMixThresholdDb,
                    (double)_audioSettings.SilenceEndThresholdDb,
                    ct);
                liveCueStart   = detected?.Start;
                liveCueEnd     = detected?.End;
                liveDurationMs = detected?.DurationSec is double d ? (uint)(d * 1000) : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Live cue re-analysis failed for variable-duration asset {AssetId} — falling back to stored cue markers",
                    asset.AssetId);
            }

            // The waveform cached at import reflects whatever content the file had back then —
            // regenerate it so the player/editor show the shape of what's actually about to play.
            try
            {
                await _waveformQueue.Writer.WriteAsync(new WaveformTask(asset.AssetId, asset.RdmFilePath), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue waveform regen for variable-duration asset {AssetId}", asset.AssetId);
            }
        }

        PlaylistMode mode;
        uint? cueStartMs;
        uint  transitionCrossfadeMs;
        double? effectiveCueEnd;
        bool loopCurrent;
        await _lock.WaitAsync(ct);
        _currentAsset = asset;
        _outroCueMs     = asset.CueOutro.HasValue    ? (uint)(asset.CueOutro.Value    * 1000) : null;
        _cueFadeOutMs   = asset.CueFadeOut.HasValue  ? (uint)(asset.CueFadeOut.Value  * 1000) : null;
        _cueStartNextMs = asset.CueStartNext.HasValue ? (uint)(asset.CueStartNext.Value * 1000) : null;
        var effectiveCueStart = liveCueStart ?? asset.CueStart;
        effectiveCueEnd = liveCueEnd ?? asset.CueEnd;
        _cueEndMs       = effectiveCueEnd.HasValue    ? (uint)(effectiveCueEnd.Value    * 1000) : null;
        cueStartMs      = effectiveCueStart.HasValue  ? (uint)(effectiveCueStart.Value  * 1000) : null;
        _cueStartMs     = cueStartMs;
        _liveDurationMsOverride       = liveDurationMs;
        _transitionCrossfadeOverride = transition?.CrossfadeMs;
        _overlaySweeperItemIds       = transition is not null ? transition.OverlayItemIds.ToList() : new List<string>();
        _startNextTriggeredItemId = null;
        mode = _mode;
        loopCurrent = _loopCurrent;
        // Crossfade out of this item is owned by the next (incoming) item — capture under lock.
        transitionCrossfadeMs = TransitionCrossfadeMs(_currentIndex);
        _lock.Release();

        var cuePoints = CuePointBuilder.Build(asset, item, mode, transitionCrossfadeMs, effectiveCueEnd);

        bool loaded = false;
        try
        {
            if (crossfadeMs > 0)
            {
                // Overlapping crossfade: incoming starts immediately at its cue-in and plays
                // alongside the fading outgoing track.
                await _audioEngine.CrossfadeToAsync(
                    asset.AssetId, asset.RdmFilePath, cuePoints,
                    cueStartMs ?? 0, crossfadeMs, fadeDelayMs,
                    CuePointBuilder.DeserializeEnvelope(item.VolumeEnvelope), ct);
            }
            else
            {
                await _audioEngine.LoadTrackAsync(asset.AssetId, asset.RdmFilePath, cuePoints, CuePointBuilder.DeserializeEnvelope(item.VolumeEnvelope), ct);

                if (cueStartMs is > 0)
                {
                    await _audioEngine.SeekPlaylistStreamAsync(cueStartMs.Value, ct);
                    _logger.LogDebug("Seeked to CueStart {CueStartMs}ms for {AssetId}", cueStartMs.Value, asset.AssetId);
                }

                await _audioEngine.PlayAsync(ct);
            }
            loaded = true;
        }
        catch (FileNotFoundException ex)
        {
            // Row still in the DB but the physical file was deleted — skip instead of
            // stalling auto-play. The queue view already hides these (IsDamaged via File.Exists).
            _logger.LogWarning(ex, "Asset {AssetId} file missing on disk ({Path}) — advancing", asset.AssetId, asset.RdmFilePath);
            await AdvanceAsync(0, 0, ct);
        }

        // Every load creates a new BASS stream, so the loop must be re-armed on it with THIS
        // track's region — both when the toggle was pressed before anything was playing and
        // when the operator skipped to another track while looping.
        if (loaded)
        {
            uint loopEndMs = effectiveCueEnd.HasValue ? (uint)(effectiveCueEnd.Value * 1000) : 0;
            await _audioEngine.SetPlayerLoopAsync(loopCurrent, cueStartMs ?? 0, loopEndMs, ct);
        }

        // Schedule the floating sweeper overlays on the now-current stream. Best-effort:
        // a missing sweeper file must not derail the main track that is already playing.
        if (loaded && transition is not null)
        {
            foreach (var (filePath, triggerAtMs) in transition.Overlays)
            {
                try
                {
                    await _audioEngine.ScheduleSweeperAsync(triggerAtMs, filePath, _audioSettings?.SweeperDuckingDb ?? 6f, ct);
                    _logger.LogDebug("Scheduled overlay sweeper at {TriggerMs}ms ({Path})", triggerAtMs, filePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to schedule overlay sweeper {Path}", filePath);
                }
            }
        }
    }

    /// <summary>
    /// Resolves a <see cref="TransitionPlanner"/> plan for the current track's outgoing
    /// transition: looks up the upcoming items' assets to find sweepers that overlap the
    /// current track's tail. Returns null when no sweeper floats over this transition (the
    /// normal crossfade path applies). Must be called WITHOUT holding <c>_lock</c>.
    /// </summary>
    private async Task<ResolvedTransition?> PlanSweeperOverlaysAsync(Asset currentAsset, CancellationToken ct)
    {
        List<PlaylistItem> upcoming;
        uint defaultCrossfade;
        await _lock.WaitAsync(ct);
        try
        {
            if (_currentIndex < 0 || _currentIndex + 1 >= _items.Count) return null;
            upcoming         = _items.Skip(_currentIndex + 1).ToList();
            defaultCrossfade = _audioSettings?.CrossfadeDurationMs ?? 0;
        }
        finally { _lock.Release(); }

        uint effEndMs = currentAsset.CueEnd.HasValue
            ? (uint)(currentAsset.CueEnd.Value * 1000)
            : currentAsset.DurationMs;

        // Resolve upcoming items into PlannerItems, stopping after the first sequential track.
        var planItems = new List<PlannerItem>(upcoming.Count);
        var assets    = new List<Asset?>(upcoming.Count);
        foreach (var it in upcoming)
        {
            if (it.ItemType == PlaylistItemType.Dummy || it.AssetId is null)
            {
                planItems.Add(new PlannerItem(true, false, 0, it.LeadInMs, it.CrossfadeMs));
                assets.Add(null);
                continue;
            }

            var a = await _assetRepo.GetByIdAsync(it.AssetId, ct);
            bool isSweeper = a?.AssetType == AssetType.Sweeper;
            planItems.Add(new PlannerItem(false, isSweeper, a?.DurationMs ?? 0, it.LeadInMs, it.CrossfadeMs));
            assets.Add(a);
            if (!isSweeper) break; // first sequential track — planner needs nothing beyond it
        }

        var plan = TransitionPlanner.Plan(effEndMs, planItems, defaultCrossfade);
        if (plan.Overlays.Count == 0) return null;

        var overlayItemIds = new List<string>();
        var overlays       = new List<(string FilePath, long TriggerAtMs)>();
        foreach (var ov in plan.Overlays)
        {
            var it = upcoming[ov.Offset - 1];
            var a  = assets[ov.Offset - 1];
            if (a?.RdmFilePath is null) continue; // can't float an overlay without a file
            overlayItemIds.Add(it.ItemId);
            overlays.Add((a.RdmFilePath, ov.TriggerAtMs));
        }

        if (overlays.Count == 0) return null;
        return new ResolvedTransition(plan.CrossfadeMs, overlayItemIds, overlays);
    }

    private sealed record ResolvedTransition(
        uint CrossfadeMs,
        IReadOnlyList<string> OverlayItemIds,
        IReadOnlyList<(string FilePath, long TriggerAtMs)> Overlays);

    /// <summary>Must be called while holding _lock.</summary>
    private void SkipDummies()
    {
        while (_currentIndex < _items.Count
               && _items[_currentIndex].ItemType == PlaylistItemType.Dummy)
            _currentIndex++;
    }

    /// <summary>
    /// Removes the just-finished item at <see cref="_currentIndex"/> from the in-memory queue,
    /// together with any DUMMY items now exposed at that slot (they were "passed", never played).
    /// Leaves <see cref="_currentIndex"/> pointing at the next playable item — the list shrank in
    /// place, so the index does not move. Returns the removed ItemIds so the caller can delete
    /// them from the ON_AIR playlist in storage (a cold start then restores only what remains).
    /// Must be called while holding <c>_lock</c>.
    /// </summary>
    private List<string> DequeueCurrentItemLocked()
    {
        var removed = new List<string>();

        // A queue swap during playback (CLEAR_PLAYLIST / LOAD_PLAYLIST) leaves _currentIndex at
        // -1: the just-finished track was never an element of the current queue, so there is
        // nothing to remove — but the cursor must enter the new queue at its head so callers
        // advance into it rather than mistaking -1 for end-of-list. (In every non-swap flow the
        // index is already >= 0 by the time a track ends, so this only fires after a live swap.)
        if (_currentIndex < 0)
        {
            _currentIndex = 0;
            return removed;
        }

        if (_currentIndex >= _items.Count)
            return removed;

        var list = _items.ToList();

        removed.Add(list[_currentIndex].ItemId);
        list.RemoveAt(_currentIndex);

        // Drop any DUMMY items now exposed, plus sweepers already scheduled as floating
        // overlays for this transition — both were "passed", so the queue lands on the
        // next real track (and a cold start restores only what truly remains).
        while (_currentIndex < list.Count
               && (list[_currentIndex].ItemType == PlaylistItemType.Dummy
                   || _overlaySweeperItemIds.Contains(list[_currentIndex].ItemId)))
        {
            removed.Add(list[_currentIndex].ItemId);
            list.RemoveAt(_currentIndex);
        }

        _items = list;
        _overlaySweeperItemIds = new List<string>(); // consumed by this transition
        return removed;
    }

    /// <summary>
    /// Deletes played items from the ON_AIR playlist in storage so a restart restores only the
    /// remaining queue. Best-effort: a storage failure is logged but never breaks playback.
    /// Must be called WITHOUT holding <c>_lock</c> (mirrors the repo-call convention elsewhere).
    /// </summary>
    private async Task DeleteQueueItemsAsync(IReadOnlyList<string> itemIds, CancellationToken ct)
    {
        foreach (var id in itemIds)
        {
            try
            {
                await _playlistRepo.RemoveItemAsync(id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove played item {ItemId} from ON_AIR queue", id);
            }
        }
    }

    /// <summary>
    /// Crossfade overlap (ms) for the transition OUT of the item at <paramref name="outgoingIndex"/>.
    /// The value is owned by the <b>next playable (incoming) item</b>, matching the segue editor /
    /// builder convention where a clip's CrossfadeMs is its overlap with the previous clip.
    /// Falls back to the studio's default crossfade. Caller must hold <c>_lock</c>.
    /// </summary>
    private uint TransitionCrossfadeMs(int outgoingIndex)
    {
        // Only Music tracks get crossfade; everything else (News, Jingle, Commercial, etc.)
        // transitions with a hard cut so the next item starts cleanly without overlap.
        if (!string.Equals(_currentAsset?.FormatName, "Music", StringComparison.OrdinalIgnoreCase))
            return 0;

        // A sweeper-overlay transition computes a deep overlap into the track AFTER the
        // sweeper(s); honour it so both the StartNext position and the fade duration match.
        if (_transitionCrossfadeOverride.HasValue)
            return _transitionCrossfadeOverride.Value;

        int j = outgoingIndex + 1;
        while (j < _items.Count && _items[j].ItemType == PlaylistItemType.Dummy)
            j++;

        uint? incoming = j < _items.Count ? _items[j].CrossfadeMs : null;
        return incoming ?? _audioSettings?.CrossfadeDurationMs ?? 0;
    }

    private async Task FireAndForget(Task task)
    {
        try   { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in PlaylistEngine event handler");
        }
    }
}
