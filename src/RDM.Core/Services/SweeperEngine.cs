using Microsoft.Extensions.Logging;
using RDM.Core.Entities;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Shared.Enums;

namespace RDM.Core.Services;

/// <summary>
/// Schedules a sweeper overlay when a music track starts after another music track (TRACK→TRACK).
///
/// Timing: the sweeper ends exactly at the new track's Intro cue (i.e. just before the vocals),
/// so it plays over the instrumental intro and clears out before the vocal comes in.
///
/// Conditions: sweeper_enabled, format match, intro >= sweeper_min_intro_ms, TRACK→TRACK pair,
/// and the sweeper's duration must fit within the intro.
/// </summary>
public sealed class SweeperEngine : IAsyncDisposable
{
    private readonly IAudioEngine                _audioEngine;
    private readonly IEventBus                  _eventBus;
    private readonly IAssetRepository           _assetRepo;
    private readonly IAudioSettingsRepository   _settingsRepo;
    private readonly StudioContext              _studioContext;
    private readonly ILogger<SweeperEngine>     _logger;
    private readonly CancellationTokenSource    _cts = new();

    private readonly Action<TrackStartedEvent> _handler;

    // Tracks the previous asset's type and format to enforce TRACK(music)→TRACK(music) rule.
    private AssetType? _previousAssetType;
    private string?    _previousFormatId;

    public SweeperEngine(
        IAudioEngine             audioEngine,
        IEventBus                eventBus,
        IAssetRepository         assetRepo,
        IAudioSettingsRepository settingsRepo,
        StudioContext            studioContext,
        ILogger<SweeperEngine>   logger)
    {
        _audioEngine   = audioEngine;
        _eventBus      = eventBus;
        _assetRepo     = assetRepo;
        _settingsRepo  = settingsRepo;
        _studioContext = studioContext;
        _logger        = logger;

        _handler = evt => _ = FireAndForget(HandleAsync(evt, _cts.Token));
        _eventBus.Subscribe(_handler);
    }

    public async ValueTask DisposeAsync()
    {
        _eventBus.Unsubscribe(_handler);
        await _cts.CancelAsync();
        _cts.Dispose();
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private async Task HandleAsync(TrackStartedEvent evt, CancellationToken ct)
    {
        // 1. Resolve current asset and update previous-tracking unconditionally.
        var currentAsset   = await _assetRepo.GetByIdAsync(evt.AssetId, ct);
        var previousType   = _previousAssetType;
        var previousFormat = _previousFormatId;
        _previousAssetType = currentAsset?.AssetType;
        _previousFormatId  = currentAsset?.FormatId;

        // 2. Check global sweeper settings.
        var settings = await _settingsRepo.GetByStudioAsync(_studioContext.StudioId, ct);
        if (settings is null || !settings.SweeperEnabled || settings.SweeperFormatId is null)
            return;

        // 3. Both current and previous must be music tracks (TRACK→TRACK, same music format).
        //    MusicFormatId gates the sweeper: if not configured, any TRACK→TRACK qualifies.
        if (currentAsset is null || currentAsset.AssetType != AssetType.Track)
            return;
        if (previousType != AssetType.Track)
        {
            _logger.LogDebug("Previous asset was not a TRACK — sweeper skipped");
            return;
        }
        if (settings.MusicFormatId is not null)
        {
            if (currentAsset.FormatId != settings.MusicFormatId)
            {
                _logger.LogDebug(
                    "Current track format {FormatId} != music format — sweeper skipped",
                    currentAsset.FormatId);
                return;
            }
            if (previousFormat != settings.MusicFormatId)
            {
                _logger.LogDebug(
                    "Previous track format {FormatId} != music format — sweeper skipped",
                    previousFormat);
                return;
            }
        }

        // 4. The just-started track's intro cue determines the sweeper window.
        //    When TrackStartedEvent fires, _playlistStream is already the NEW track's stream.
        uint introMs = currentAsset.CueIntro.HasValue
            ? (uint)(currentAsset.CueIntro.Value * 1000)
            : 0u;

        if (introMs < settings.SweeperMinIntroMs)
        {
            _logger.LogDebug(
                "Track {AssetId} intro {IntroMs}ms < min {MinMs}ms — sweeper skipped",
                evt.AssetId, introMs, settings.SweeperMinIntroMs);
            return;
        }

        // 5. Select a random eligible sweeper that fits within the intro
        //    (duration <= intro so it can start at or after 0 and end at the Intro cue).
        //    When an active subcategory is set, the pool is narrowed to it (e.g. "day"/"weekend");
        //    null = randomize across the whole sweeper category.
        var candidates = (await _assetRepo.GetByFormatAsync(settings.SweeperFormatId, AssetStatus.Active, ct))
            .Where(s => s.AssetType == AssetType.Sweeper
                     && s.DurationMs <= introMs
                     && s.RdmFilePath is not null
                     && (settings.SweeperSubcategoryId is null
                         || s.SubcategoryId == settings.SweeperSubcategoryId))
            .ToList();

        if (candidates.Count == 0)
        {
            _logger.LogDebug(
                "No eligible sweeper in format {FormatId} subcategory {SubId} fitting intro {IntroMs}ms — skipped",
                settings.SweeperFormatId, settings.SweeperSubcategoryId ?? "(all)", introMs);
            return;
        }

        var sweeper = candidates[Random.Shared.Next(candidates.Count)];

        // 6. Schedule so the sweeper ends exactly at the Intro cue: start = intro - duration.
        long startMs = introMs - sweeper.DurationMs;

        _logger.LogDebug(
            "Scheduling sweeper {SweeperId} at {StartMs}ms "
            + "(intro={Intro}ms duration={Dur}ms — ends at intro cue)",
            sweeper.AssetId, startMs, introMs, sweeper.DurationMs);

        await _audioEngine.ScheduleSweeperAsync(startMs, sweeper.RdmFilePath!, settings.SweeperDuckingDb, ct);
    }

    private async Task FireAndForget(Task task)
    {
        try   { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in SweeperEngine event handler");
        }
    }
}
