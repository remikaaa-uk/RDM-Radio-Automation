using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RDM.Core.Entities;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Shared.Enums;

namespace RDM.Core.Services;

/// <summary>
/// Persists playlist playback history to playout_log (used for ZAIKS/STOART reporting
/// and the History view in the UI).
///
/// PlaylistItemStartedEvent → INSERT playout_log (records started_at)
/// PlaylistItemEndedEvent   → UPDATE playout_log.ended_at
///
/// Bug 1 fix: keyed by PlaylistItem.ItemId (not AssetId) — handles the case where
/// the same asset appears multiple times in a playlist without correlation errors.
/// </summary>
public sealed class PlayoutLogService : IAsyncDisposable
{
    private readonly IEventBus                  _eventBus;
    private readonly IPlayoutLogRepository      _logRepo;
    private readonly StudioContext              _studioContext;
    private readonly ILogger<PlayoutLogService> _logger;
    private readonly CancellationTokenSource    _cts = new();

    // itemId → logId  (Bug 1 fix: ItemId is unique per playlist position)
    private readonly ConcurrentDictionary<string, string> _activeEntries = new();

    // streamAssetId → logId  (ICY metadata — one entry per track played on the stream)
    private readonly ConcurrentDictionary<string, string> _activeStreamEntries = new();

    private readonly Action<PlaylistItemStartedEvent> _startedHandler;
    private readonly Action<PlaylistItemEndedEvent>   _endedHandler;
    private readonly Action<StreamMetaChangedEvent>   _streamMetaHandler;

    public PlayoutLogService(
        IEventBus                   eventBus,
        IPlayoutLogRepository       logRepo,
        StudioContext               studioContext,
        ILogger<PlayoutLogService>  logger)
    {
        _eventBus      = eventBus;
        _logRepo       = logRepo;
        _studioContext = studioContext;
        _logger        = logger;

        _startedHandler    = evt => _ = FireAndForget(OnStartedAsync(evt, _cts.Token));
        _endedHandler      = evt => _ = FireAndForget(OnEndedAsync(evt, _cts.Token));
        _streamMetaHandler = evt => _ = FireAndForget(OnStreamMetaAsync(evt, _cts.Token));

        _eventBus.Subscribe(_startedHandler);
        _eventBus.Subscribe(_endedHandler);
        _eventBus.Subscribe(_streamMetaHandler);
    }

    public async ValueTask DisposeAsync()
    {
        _eventBus.Unsubscribe(_startedHandler);
        _eventBus.Unsubscribe(_endedHandler);
        _eventBus.Unsubscribe(_streamMetaHandler);
        await _cts.CancelAsync();
        _cts.Dispose();
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private async Task OnStartedAsync(PlaylistItemStartedEvent evt, CancellationToken ct)
    {
        var logId = Guid.NewGuid().ToString();
        _activeEntries[evt.ItemId] = logId;

        var entry = new PlayoutLog
        {
            LogId      = logId,
            StudioId   = _studioContext.StudioId,
            AssetId    = evt.AssetId,
            TempTitle  = evt.Title,
            TempArtist = evt.Artist,
            StartedAt  = DateTime.UtcNow,
            SourceType = SourceType.Playlist
        };

        try
        {
            await _logRepo.CreateAsync(entry, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "PlayoutLogService: INSERT failed for item {ItemId}", evt.ItemId);
            _activeEntries.TryRemove(evt.ItemId, out _);
        }
    }

    private async Task OnEndedAsync(PlaylistItemEndedEvent evt, CancellationToken ct)
    {
        if (!_activeEntries.TryRemove(evt.ItemId, out var logId))
        {
            _logger.LogDebug(
                "PlayoutLogService: no active log for item {ItemId} — end ignored", evt.ItemId);
        }
        else
        {
            try
            {
                await _logRepo.UpdateEndedAtAsync(logId, DateTime.UtcNow, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "PlayoutLogService: UPDATE ended_at failed for log {LogId}", logId);
            }
        }

        // Close any open ICY track entry for this stream asset (external items have no AssetId)
        if (evt.AssetId is not null && _activeStreamEntries.TryRemove(evt.AssetId, out var streamLogId))
        {
            try { await _logRepo.UpdateEndedAtAsync(streamLogId, DateTime.UtcNow, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "PlayoutLogService: UPDATE ended_at failed for ICY log {LogId}", streamLogId);
            }
        }
    }

    private async Task OnStreamMetaAsync(StreamMetaChangedEvent evt, CancellationToken ct)
    {
        _logger.LogDebug("PlayoutLogService: OnStreamMetaAsync assetId={AssetId} title='{Title}'",
            evt.AssetId, evt.StreamTitle);

        // Close previous ICY entry for this stream
        if (_activeStreamEntries.TryRemove(evt.AssetId, out var oldLogId))
        {
            try { await _logRepo.UpdateEndedAtAsync(oldLogId, DateTime.UtcNow, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "PlayoutLogService: UPDATE ended_at failed for ICY log {LogId}", oldLogId);
            }
        }

        if (string.IsNullOrWhiteSpace(evt.StreamTitle)) return;

        var (artist, title) = ParseStreamTitle(evt.StreamTitle);
        var logId = Guid.NewGuid().ToString();
        _activeStreamEntries[evt.AssetId] = logId;

        var entry = new PlayoutLog
        {
            LogId      = logId,
            StudioId   = _studioContext.StudioId,
            AssetId    = null,
            TempTitle  = title,
            TempArtist = artist,
            StartedAt  = DateTime.UtcNow,
            SourceType = SourceType.Playlist
        };

        try
        {
            await _logRepo.CreateAsync(entry, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "PlayoutLogService: INSERT failed for ICY track '{Title}'", evt.StreamTitle);
            _activeStreamEntries.TryRemove(evt.AssetId, out _);
        }
    }

    private static (string? artist, string title) ParseStreamTitle(string streamTitle)
    {
        var sep = streamTitle.IndexOf(" - ", StringComparison.Ordinal);
        if (sep < 0) return (null, streamTitle.Trim());
        return (streamTitle[..sep].Trim(), streamTitle[(sep + 3)..].Trim());
    }

    private async Task FireAndForget(Task task)
    {
        try   { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception in PlayoutLogService");
        }
    }
}
