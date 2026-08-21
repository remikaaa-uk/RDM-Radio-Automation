using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Shared.DTOs;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RDM.API.WebSocket;

public sealed class RdmWebSocketHub : IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly IPlaylistController _playlistController;
    private readonly ILogger<RdmWebSocketHub> _logger;
    private readonly ConcurrentDictionary<string, ConnectedClient> _clients = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Action<PlaylistItemStartedEvent>    _onTrackStarted;
    private readonly Action<PlaylistItemEndedEvent>      _onTrackEnded;
    private readonly Action<PlaylistModeChangedEvent>    _onModeChanged;
    private readonly Action<PlaylistStoppedEvent>        _onPlaylistStopped;
    private readonly Action<PlaylistQueueReloadedEvent>  _onQueueReloaded;
    private readonly Action<DeadAirWarningEvent>         _onDeadAir;
    private readonly Action<WaveformReadyEvent>          _onWaveformReady;
    private readonly Action<AssetCreatedEvent>           _onAssetCreated;
    private readonly Action<LoudnessAnalyzedEvent>       _onLoudnessAnalyzed;
    private readonly Action<ScheduledEventFiredEvent>    _onScheduleFired;
    private readonly Action<ScheduledEventSkippedEvent>  _onScheduleSkipped;
    private readonly Action<PflEndedEvent>               _onPflEnded;
    private readonly Action<CartTriggeredEvent>          _onCartTriggered;
    private readonly Action<CartEndedEvent>              _onCartEnded;
    private readonly Action<StreamMetaChangedEvent>      _onStreamMeta;

    public RdmWebSocketHub(
        IEventBus eventBus,
        IPlaylistController playlistController,
        ILogger<RdmWebSocketHub> logger)
    {
        _eventBus = eventBus;
        _playlistController = playlistController;
        _logger = logger;

        _onTrackStarted    = OnTrackStarted;
        _onTrackEnded      = OnTrackEnded;
        _onModeChanged     = OnModeChanged;
        _onPlaylistStopped = OnPlaylistStopped;
        _onQueueReloaded   = OnQueueReloaded;
        _onDeadAir         = OnDeadAir;
        _onWaveformReady   = OnWaveformReady;
        _onAssetCreated    = OnAssetCreated;
        _onLoudnessAnalyzed = OnLoudnessAnalyzed;
        _onScheduleFired   = OnScheduleFired;
        _onScheduleSkipped = OnScheduleSkipped;
        _onPflEnded        = OnPflEnded;
        _onCartTriggered   = OnCartTriggered;
        _onCartEnded       = OnCartEnded;
        _onStreamMeta      = OnStreamMeta;

        _eventBus.Subscribe(_onTrackStarted);
        _eventBus.Subscribe(_onTrackEnded);
        _eventBus.Subscribe(_onModeChanged);
        _eventBus.Subscribe(_onPlaylistStopped);
        _eventBus.Subscribe(_onQueueReloaded);
        _eventBus.Subscribe(_onDeadAir);
        _eventBus.Subscribe(_onWaveformReady);
        _eventBus.Subscribe(_onAssetCreated);
        _eventBus.Subscribe(_onLoudnessAnalyzed);
        _eventBus.Subscribe(_onScheduleFired);
        _eventBus.Subscribe(_onScheduleSkipped);
        _eventBus.Subscribe(_onPflEnded);
        _eventBus.Subscribe(_onCartTriggered);
        _eventBus.Subscribe(_onCartEnded);
        _eventBus.Subscribe(_onStreamMeta);
    }

    public async Task HandleConnectionAsync(System.Net.WebSockets.WebSocket ws, CancellationToken ct)
    {
        var clientId = Guid.NewGuid().ToString();
        var client = new ConnectedClient(ws);
        _clients[clientId] = client;
        _logger.LogInformation("WebSocket client connected: {ClientId}", clientId);

        var writeTask = client.RunWriteLoopAsync(ct);
        var buffer = new byte[1024];

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket receive error for {ClientId}", clientId);
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            await client.DisposeAsync();

            try { await writeTask; }
            catch (OperationCanceledException) { }

            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None); }
                catch { }
            }

            _logger.LogInformation("WebSocket client disconnected: {ClientId}", clientId);
        }
    }

    private void Broadcast(string eventType, object payload)
    {
        var frame = new WebSocketFrameDto(Guid.NewGuid().ToString(), eventType, DateTime.UtcNow, payload);
        var json = JsonSerializer.Serialize(frame, JsonOptions);

        var dead = new List<string>();
        foreach (var (id, client) in _clients)
        {
            if (!client.TryEnqueue(json))
                dead.Add(id);
        }

        foreach (var id in dead)
        {
            if (_clients.TryRemove(id, out var c))
                _ = c.DisposeAsync();
        }
    }

    private void OnTrackStarted(PlaylistItemStartedEvent evt)
    {
        var info = _playlistController.GetNowPlayingInfo();
        DateTime? scheduledAt = null;
        if (info.TrackStartedAt.HasValue)
        {
            var durationMs = info.OutroCueMs ?? evt.DurationMs;
            scheduledAt = info.TrackStartedAt.Value.AddMilliseconds(durationMs);
        }

        Broadcast("TRACK_STARTED", new
        {
            asset_id = evt.AssetId,
            title = evt.Title,
            artist = evt.Artist,
            duration_ms = evt.DurationMs,
            scheduled_at = scheduledAt,
            vu_offset_db = evt.VuOffsetDb
        });
    }

    private void OnTrackEnded(PlaylistItemEndedEvent evt)
    {
        Broadcast("TRACK_ENDED", new
        {
            asset_id = evt.AssetId,
            reason   = evt.EndReason,
            ended_at = DateTime.UtcNow
        });
    }

    private void OnAssetCreated(AssetCreatedEvent evt)
        => Broadcast("ASSET_IMPORTED", new
        {
            asset_id = evt.AssetId,
            title    = evt.Title,
            artist   = evt.Artist
        });

    private void OnLoudnessAnalyzed(LoudnessAnalyzedEvent evt)
        => Broadcast("LOUDNESS_ANALYZED", new
        {
            asset_id  = evt.AssetId,
            lufs      = evt.LufsIntegrated,
            true_peak = evt.TruePeak
        });

    private void OnScheduleFired(ScheduledEventFiredEvent evt)
        => Broadcast("SCHEDULE_CHANGED", new
        {
            event_id    = evt.EventId,
            name        = evt.Name,
            change_type = "FIRED",
            result      = evt.Result
        });

    private void OnScheduleSkipped(ScheduledEventSkippedEvent evt)
        => Broadcast("SCHEDULE_CHANGED", new
        {
            event_id    = evt.EventId,
            name        = evt.Name,
            change_type = "SKIPPED"
        });

    private void OnModeChanged(PlaylistModeChangedEvent evt)
    {
        Broadcast("PLAYLIST_MODE_CHANGED", new
        {
            previous_mode = evt.PreviousMode.ToString(),
            mode          = evt.NewMode.ToString()
        });
    }

    private void OnQueueReloaded(PlaylistQueueReloadedEvent evt)
    {
        Broadcast("PLAYLIST_UPDATED", new { playlist_id = evt.PlaylistId });
    }

    private void OnPlaylistStopped(PlaylistStoppedEvent evt)
    {
        Broadcast("PLAYLIST_STOPPED", new
        {
            playlist_id = evt.PlaylistId,
            reason = evt.Reason
        });
    }

    private void OnDeadAir(DeadAirWarningEvent evt)
    {
        Broadcast("DEAD_AIR_WARNING", new
        {
            silence_ms = evt.SilenceMs,
            mode = evt.Mode.ToString()
        });
    }

    private void OnWaveformReady(WaveformReadyEvent evt)
        => Broadcast("WAVEFORM_READY", new { asset_id = evt.AssetId });

    private void OnPflEnded(PflEndedEvent evt)
        => Broadcast("PFL_ENDED", new { asset_id = evt.AssetId });

    private void OnCartTriggered(CartTriggeredEvent evt)
        => Broadcast("CART_TRIGGERED", new
        {
            slot_id     = evt.SlotId,
            duration_ms = evt.DurationMs,
            label       = (string?)null
        });

    private void OnCartEnded(CartEndedEvent evt)
        => Broadcast("CART_STOPPED", new { slot_id = evt.SlotId });

    private void OnStreamMeta(StreamMetaChangedEvent evt)
        => Broadcast("STREAM_META_CHANGED", new
        {
            asset_id     = evt.AssetId,
            stream_title = evt.StreamTitle
        });

    public void Dispose()
    {
        _eventBus.Unsubscribe(_onTrackStarted);
        _eventBus.Unsubscribe(_onTrackEnded);
        _eventBus.Unsubscribe(_onModeChanged);
        _eventBus.Unsubscribe(_onPlaylistStopped);
        _eventBus.Unsubscribe(_onQueueReloaded);
        _eventBus.Unsubscribe(_onDeadAir);
        _eventBus.Unsubscribe(_onWaveformReady);
        _eventBus.Unsubscribe(_onAssetCreated);
        _eventBus.Unsubscribe(_onLoudnessAnalyzed);
        _eventBus.Unsubscribe(_onScheduleFired);
        _eventBus.Unsubscribe(_onScheduleSkipped);
        _eventBus.Unsubscribe(_onPflEnded);
        _eventBus.Unsubscribe(_onCartTriggered);
        _eventBus.Unsubscribe(_onCartEnded);
        _eventBus.Unsubscribe(_onStreamMeta);
    }

    private sealed class ConnectedClient : IAsyncDisposable
    {
        public System.Net.WebSockets.WebSocket Socket { get; }
        private readonly Channel<string> _outbox = Channel.CreateBounded<string>(
            new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropOldest });

        public ConnectedClient(System.Net.WebSockets.WebSocket socket)
        {
            Socket = socket;
        }

        public bool TryEnqueue(string json) => _outbox.Writer.TryWrite(json);

        public async Task RunWriteLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var msg in _outbox.Reader.ReadAllAsync(ct))
                {
                    if (Socket.State != WebSocketState.Open) break;
                    await Socket.SendAsync(
                        Encoding.UTF8.GetBytes(msg),
                        WebSocketMessageType.Text,
                        endOfMessage: true,
                        cancellationToken: CancellationToken.None);
                }
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) { }
        }

        public ValueTask DisposeAsync()
        {
            _outbox.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
