using System.Threading.Channels;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Shared.Enums;

namespace RDM.Audio.Engine;

// ── Internal message types ────────────────────────────────────────────────────

internal enum SyncMessageType
{
    TrackEnded,
    TrackOutroReached,
    TrackIntroReached,
    TrackStartNextReached,
    TrackFadeOutReached,
    TrackCueEndReached,
    CuePointReached,
    CartEnded,
    BufferUnderrun,
    SweeperTrigger, // position reached on playlist stream; EventBridge creates sweeper stream
    SweeperEnded,   // sweeper stream finished; EventBridge frees BASS resources
    PflEnded,       // PFL stream played to end naturally
    AuxEnded,       // AUX deck played to its natural end
    PlayerLooped,   // looping player track rewound to its loop start; re-arms the cue syncs
    StreamMeta      // ICY/Shoutcast metadata changed on internet stream
}

internal record SyncMessage(
    SyncMessageType Type,
    string?         AssetId     = null,
    string?         SlotId      = null,
    long            RemainingMs = 0,
    MarkerType?     Marker      = null,
    uint            PositionMs  = 0,
    string?         FilePath    = null, // sweeper file path carried from SYNCPROC to drain task
    int             AuxIndex    = -1,   // AUX deck index for AuxEnded messages
    Action?         PostProcess = null, // cart/sweeper cleanup, called on drain task thread
    string?         StreamTitle = null); // ICY metadata title for StreamMeta messages

// ── EventBridge ───────────────────────────────────────────────────────────────

/// <summary>
/// Decouples BASS audio-thread callbacks from .NET event publishing.
/// SYNCPROC callbacks call Post() (lock-free TryWrite) from the BASS thread.
/// A dedicated background Task drains the channel and calls IEventBus.PublishAsync
/// on the .NET thread pool.
/// </summary>
internal sealed class EventBridge : IAsyncDisposable
{
    private readonly IEventBus _eventBus;
    private readonly Channel<SyncMessage> _channel;
    private CancellationTokenSource? _cts;
    private Task? _drainTask;
    private Action<string>? _sweeperHandler; // set by BassAudioEngine after init

    public EventBridge(IEventBus eventBus)
    {
        _eventBus = eventBus;
        _channel  = Channel.CreateBounded<SyncMessage>(new BoundedChannelOptions(128)
        {
            FullMode     = BoundedChannelFullMode.DropOldest, // never blocks the BASS thread
            SingleWriter = false,
            SingleReader = true
        });
    }

    /// <summary>Starts the background drain loop. Call from InitializeAsync.</summary>
    public void Start()
    {
        _cts       = new CancellationTokenSource();
        _drainTask = Task.Run(() => DrainAsync(_cts.Token));
    }

    /// <summary>
    /// Completes the channel and awaits the drain task. Call from ShutdownAsync.
    /// Remaining queued messages are processed before the task exits.
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts is null || _drainTask is null) return;

        _channel.Writer.TryComplete();
        await _drainTask.ConfigureAwait(false);

        _cts.Cancel();
        _cts.Dispose();
        _cts       = null;
        _drainTask = null;
    }

    /// <summary>
    /// Posts a message from a BASS SYNCPROC callback.
    /// MUST be non-blocking — called on the BASS audio thread.
    /// </summary>
    public void Post(SyncMessage message) => _channel.Writer.TryWrite(message);

    /// <summary>
    /// Registers the callback that creates a sweeper stream when SweeperTrigger fires.
    /// Called by BassAudioEngine after initialization.
    /// </summary>
    public void SetSweeperHandler(Action<string> handler) => _sweeperHandler = handler;

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    // ── Private ───────────────────────────────────────────────────────────────

    private async Task DrainAsync(CancellationToken ct)
    {
        await foreach (var msg in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try   { await DispatchAsync(msg).ConfigureAwait(false); }
            catch { /* swallow — never crash the drain loop */ }
        }
    }

    private Task DispatchAsync(SyncMessage msg) => msg.Type switch
    {
        SyncMessageType.TrackEnded =>
            _eventBus.PublishAsync(new TrackEndedEvent(msg.AssetId!, "NATURAL")),

        SyncMessageType.TrackOutroReached =>
            _eventBus.PublishAsync(new TrackOutroReachedEvent(msg.AssetId!, msg.RemainingMs)),

        SyncMessageType.TrackIntroReached =>
            _eventBus.PublishAsync(new TrackIntroReachedEvent(msg.AssetId!)),

        SyncMessageType.TrackStartNextReached =>
            _eventBus.PublishAsync(new TrackStartNextReachedEvent(msg.AssetId!)),

        SyncMessageType.TrackFadeOutReached =>
            _eventBus.PublishAsync(new TrackFadeOutReachedEvent(msg.AssetId!)),

        SyncMessageType.TrackCueEndReached =>
            _eventBus.PublishAsync(new TrackCueEndReachedEvent(msg.AssetId!)),

        SyncMessageType.CuePointReached =>
            _eventBus.PublishAsync(new CuePointReachedEvent(
                msg.AssetId!, msg.Marker!.Value, msg.PositionMs)),

        SyncMessageType.CartEnded => DispatchCartEndedAsync(msg),

        SyncMessageType.BufferUnderrun =>
            _eventBus.PublishAsync(new BufferUnderrunEvent("player", DateTimeOffset.UtcNow)),

        SyncMessageType.SweeperTrigger => DispatchSweeperTriggerAsync(msg),

        SyncMessageType.SweeperEnded => DispatchSweeperEndedAsync(msg),

        SyncMessageType.PflEnded =>
            _eventBus.PublishAsync(new PflEndedEvent(msg.AssetId ?? "")),

        SyncMessageType.AuxEnded =>
            _eventBus.PublishAsync(new AuxDeactivatedEvent(msg.AuxIndex)),

        SyncMessageType.PlayerLooped => DispatchPlayerLoopedAsync(msg),

        SyncMessageType.StreamMeta =>
            _eventBus.PublishAsync(new StreamMetaChangedEvent(msg.AssetId!, msg.StreamTitle ?? "")),

        _ => Task.CompletedTask
    };

    private Task DispatchPlayerLoopedAsync(SyncMessage msg)
    {
        // Re-arms the track's one-shot cue syncs for the new pass. Runs on the drain task
        // thread — the mixtime SYNCPROC that posted this must not call back into BASS setup.
        msg.PostProcess?.Invoke();
        return _eventBus.PublishAsync(new PlayerLoopedEvent(msg.AssetId ?? "", msg.PositionMs));
    }

    private Task DispatchCartEndedAsync(SyncMessage msg)
    {
        // Free BASS stream resources before notifying subscribers
        msg.PostProcess?.Invoke();
        return _eventBus.PublishAsync(new CartEndedEvent(msg.SlotId!, msg.AssetId!));
    }

    private Task DispatchSweeperTriggerAsync(SyncMessage msg)
    {
        // Called on drain task thread — safe to call BASS functions via the handler
        if (msg.FilePath is not null)
            _sweeperHandler?.Invoke(msg.FilePath);
        return Task.CompletedTask;
    }

    private static Task DispatchSweeperEndedAsync(SyncMessage msg)
    {
        // Frees sweeper BASS stream and unpins GCHandle; no domain event published
        msg.PostProcess?.Invoke();
        return Task.CompletedTask;
    }
}
