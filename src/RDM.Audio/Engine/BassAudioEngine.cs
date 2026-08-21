using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RDM.Audio.Engine.Output;
using RDM.Audio.Processing;
using RDM.Audio.Sweeper;
using RDM.Core.Entities;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Shared.Enums;
using Un4seen.Bass;
using Un4seen.Bass.AddOn.Fx;
using Un4seen.Bass.AddOn.Mix;
using Un4seen.Bass.AddOn.Vst;

namespace RDM.Audio.Engine;

public sealed class BassAudioEngine : IAudioEngine, IAsyncDisposable
{
    // ── Dependencies ─────────────────────────────────────────────────────────

    private readonly IEventBus              _eventBus;
    private readonly IAudioDeviceRepository _deviceRepository;
    private readonly EventBridge            _eventBridge;
    private readonly CuePointTimer          _cuePointTimer;
    private readonly ILogger<BassAudioEngine> _log;
    private readonly object                 _lock = new();
    private SweeperTrigger?                 _sweeperTrigger;
    // Track-ducking gain applied while a sweeper plays (1.0 = no duck). Set by
    // ScheduleSweeperAsync, consumed when the sweeper stream starts.
    private float                           _pendingSweeperDuckGain = 1f;

    // ── BASS state ────────────────────────────────────────────────────────────

    private RoutingGraph?   _routingGraph;
    private ResolvedRouting? _routing;
    private IOutputBackend? _outputBackend;
    private int             _mixerHandle;
    private int             _playlistStream;
    private int             _pflStream;
    private string?         _pflAssetId;

    // Multi-lane PFL preview streams (segue editor mix audition). Lanes 0/1.
    private readonly int[]  _previewStreams = new int[2];

    // Streaming encoders, keyed by profile id. Each session is a non-consuming DSP tap on the
    // mixer and owns its own reconnect policy; the engine only starts, stops and reports them.
    private readonly ConcurrentDictionary<string, EncoderSession> _encoderSessions = new();

    // File recorder — the same non-consuming tap, one at a time. A plain field rather than a
    // dictionary: recording is a single operator action, not a collection like the cast profiles.
    private readonly object _recordingLock = new();
    private RecordingSession? _recordingSession;

    // Outgoing streams mid-crossfade — kept alive (still audible) until their fade
    // completes, then removed from the mixer and freed by FreeTimer.
    private sealed class FadingStream { public int Stream; public Timer? FadeTimer; public Timer? FreeTimer; }
    private readonly List<FadingStream> _fading = new();
    // Active equal-power fade ramps (crossfade/fade-out). Tracked so they survive GC and are
    // disposed on shutdown; each self-removes when its curve completes. Guarded by _fadeLock.
    private readonly HashSet<Timer> _rampTimers = new();
    private readonly object _fadeLock = new();

    // ── Microphone ────────────────────────────────────────────────────────────
    // Config: persists across mic on/off cycles
    private readonly List<MicFxSlot>        _micFxConfig   = new();
    private readonly List<MicVstSlot>       _micVstConfig  = new();
    // Runtime handles: populated in StartMicAsync, cleared in StopMicAsync
    private readonly Dictionary<int, int>   _micFxHandles  = new();  // slotId → BASS FX handle
    private readonly Dictionary<int, int>   _micVstHandles = new();  // slotId → BASS VST handle

    // slotId → editor callback. The delegates MUST be kept alive here for as long as bass_vst
    // holds them: letting one be collected crashes the process when the plugin next calls back.
    private readonly Dictionary<int, VSTPROC> _micVstEditorProcs = new();
    private int _micFxNextSlotId;
    private int _micVstNextSlotId;
    private int           _micPushStream;
    private int           _micRecordHandle;
    private RECORDPROC?   _micRecordProc;
    private GCHandle      _micRecordProcGch;
    private volatile bool _isMicActive;
    private int           _micSampleRate;

    // Mic level DSP (priority 20000 — runs before any FX on the push stream)
    private DSPPROC?      _micLevelDsp;
    private GCHandle      _micLevelDspGch;
    private int           _micLevelDspHandle;

    // Raw (unfiltered) peak level — written by DSPPROC, read by GetMicLevelDb(); use Interlocked.
    private double        _rawMicLevelDb  = -60.0;
    private volatile int  _micRecordCallbackCount; // diagnostic
    private volatile int  _micDspCallbackCount;    // diagnostic

    // ── Mic distortion diagnostics (throttled logging from the RT callbacks) ──
    // _micLastBufferLogTick / _micLastClipLogTick: Environment.TickCount64 of the last log,
    // used to rate-limit the RT-thread logging to ~once per 2 s.
    private long          _micLastBufferLogTick;   // push-stream fill logger (record thread)
    private long          _micLastClipLogTick;     // clip logger (DSP thread)
    private int           _micClipCount;           // # samples with |v| >= full-scale since last clip log
    private float         _micWindowPeak;          // max |v| since last clip log

    // Pre-allocated buffer for peak measurement (avoids heap allocation in RT callback)
    private float[]       _micLevelBuffer = new float[4096];

    // ── Voice track recording ─────────────────────────────────────────────────
    private int           _recordHandle;
    private RECORDPROC?   _recordProc;
    private GCHandle      _recordProcGch;
    private FileStream?   _recordStream;
    private BinaryWriter? _recordWriter;
    private int           _recordSampleRate;
    private int           _recordChannels;
    private long          _recordDataStartPos;
    private volatile bool _isRecording;
    private SYNCPROC?       _pflEndProc;
    private GCHandle        _pflEndGcHandle;
    private SYNCPROC?       _streamMetaProc;
    private GCHandle        _streamMetaGcHandle;
    private string?         _currentAssetId;
    private bool            _isInitialized;

    // Mixer stall SYNCPROC — engine lifetime, must survive GC
    private SYNCPROC? _stallProc;
    private GCHandle  _stallGcHandle;

    // ── Volume envelope ───────────────────────────────────────────────────────
    // DSP proc registered on _playlistStream when the track has envelope points.
    // Multiplies each decoded sample by the interpolated gain at that file position.
    private IReadOnlyList<RDM.Core.Models.EnvelopePoint>? _playlistEnvelope;
    private DSPPROC? _envelopeDsp;
    private GCHandle _envelopeDspGch;
    private int      _envelopeDspHandle;

    // ── Player loop (repeat the current track) ────────────────────────────────
    // Armed by SetPlayerLoopAsync with the loop region of the track that is loaded. A mixtime
    // BASS_SYNC_POS at the loop end seeks back to the loop start, so the repeat is seamless and
    // honours CueStart/CueEnd — BASS_SAMPLE_LOOP could only repeat the whole file, ignoring both.
    // Same mechanism the AUX decks use through the mixer (see RegisterAuxEndSyncLocked).
    private bool      _playerLoop;
    private uint      _playerLoopStartMs;
    private uint      _playerLoopEndMs;   // 0 = loop at the end of the file
    private SYNCPROC? _playerLoopProc;
    private GCHandle  _playerLoopGch;
    private int       _playerLoopSync;

    // Cartwall — up to 8 simultaneous streams, keyed by slotId
    private readonly ConcurrentDictionary<string, CartState> _cartwallStreams = new();
    private volatile string _cartwallMode = "ON";

    // AUX ducking
    private AudioSettings? _activeSettings;
    private float          _duckingGain = 1.0f; // mic-ducking ramp position [0..1]
    private Timer?         _duckTimer;
    private readonly object _duckLock   = new();

    // Sweeper ducking — independent of the mic ducker so a sweeper ending does not
    // restore full volume while the presenter is still talking. The applied playlist
    // volume is always _duckingGain * _sweeperDuckGain (see ApplyCombinedDuckGain).
    private float          _sweeperDuckGain = 1.0f; // sweeper-ducking ramp position [0..1]
    private Timer?         _sweeperDuckTimer;
    private const uint     SweeperDuckAttackMs  = 150;
    private const uint     SweeperDuckReleaseMs = 400;

    // AUX players — independent file decks keyed by index (0..3)
    private readonly ConcurrentDictionary<int, AuxState> _auxStreams = new();
    private Timer? _auxMonitorTimer;

    // AUX routing mode. DirectSound reaches the card through a BASS output device, so decks play
    // via BASS_ChannelPlay on _routing.PlayerDeviceIndex (and PFL can target a second card). WASAPI/
    // ASIO own the card exclusively — the only path to it is the master decode mixer — so there the
    // decks are added to _mixerHandle as decode sources instead. Set once at InitializeAsync.
    private bool _auxThroughMixer;

    // InMixer: cart is a decode source of the master mixer (ON under WASAPI/ASIO). Otherwise it plays
    // direct to a device (DirectSound, or any PFL cart on the PFL card).
    private record CartState(int StreamHandle, string AssetId, bool Loop, GCHandle SyncHandle, bool InMixer);

    private sealed class AuxState
    {
        public int      StreamHandle;
        public string   FilePath = "";
        public bool     Loop;
        public bool     On = true;   // routed to program bus (on air)
        public bool     Pfl;         // monitored on the PFL device (headphones)
        public float    Volume = 1.0f;
        public uint     DurationMs;
        public uint     StartMs;
        public uint     EndMs;
        public bool     InMixer;        // true = decode source of the master mixer (ON under WASAPI/ASIO);
                                        // false = direct device playback (DirectSound, PFL, or a deck with
                                        //         its own assigned output card)
        public int?     DeviceIndex;    // dedicated output card for this deck when ON (null = program bus)
        public GCHandle EndSyncHandle;  // pinned SYNCPROC: mixtime loop/END sync (mixer) or BASS_SYNC_END (device)
        public Timer?   StopFadeTimer;  // pending fade-out-then-pause from StopAuxAsync(fadeoutMs > 0)
    }

    public bool IsInitialized => _isInitialized;
    public bool IsMicActive   => _isMicActive;
    public bool IsRecording   => _isRecording;

    // ── Constructor ───────────────────────────────────────────────────────────

    public BassAudioEngine(
        IEventBus eventBus,
        IAudioDeviceRepository deviceRepository,
        ILogger<BassAudioEngine> log)
    {
        _eventBus         = eventBus;
        _deviceRepository = deviceRepository;
        _log              = log;
        _eventBridge      = new EventBridge(eventBus);
        _cuePointTimer    = new CuePointTimer(_eventBridge);
    }

    // ── IAudioEngine ─────────────────────────────────────────────────────────

    public async Task InitializeAsync(AudioSettings settings, CancellationToken ct = default)
    {
        if (_isInitialized) return;

        // Registration slot — populate before production deployment.
        // Trial mode: BASS shows a popup every ~30 seconds.
        // BassNet.Registration("email@example.com", "XXXX-YYYY-ZZZZ-WWWW");

        var devices = await _deviceRepository.GetByStudioAsync(settings.StudioId, ct);

        lock (_lock)
        {
            if (_isInitialized) return;

            // Latency stack for mic monitoring (default BASS config):
            //   REC_BUFFER 100ms + UPDATE 100ms + OUTPUT_BUFFER 500ms ≈ 700ms — unusable.
            // Target: REC 20ms + UPDATE 20ms + OUTPUT 100ms ≈ 140ms — acceptable for radio.
            // UPDATEPERIOD=10ms causes BASS recording to stall after first callback on this system
            // (Windows WASAPI shared mode minimum period is ~10ms — too tight for reliable REC callbacks).
            // UPDATEPERIOD=20ms is the confirmed-working floor; BUFFER=100ms (down from default 500ms)
            // gives ~140ms total mic monitoring latency vs ~620ms before.
            Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_UPDATEPERIOD, 20);  // proven stable on this system
            Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_BUFFER, 100);       // output pre-buffer: 500→100ms
            Bass.BASS_SetConfig(BASSConfig.BASS_CONFIG_REC_BUFFER, 20);    // record callback: follows UPDATEPERIOD

            // 1. Resolve device UUIDs → BASS indices and call BASS_Init per device
            _routingGraph   = new RoutingGraph(_log);
            _routing        = _routingGraph.Initialize(settings, devices);
            _activeSettings = settings;

            // Log player output device so we can compare with mic recording device (loopback diagnosis)
            var playerInfo = Bass.BASS_GetDeviceInfo(_routing.PlayerDeviceIndex);
            _log.LogInformation("AudioEngine: player device index={Idx}, name='{Name}', id='{Id}'",
                _routing.PlayerDeviceIndex, playerInfo?.name, playerInfo?.id);

            // Log all recording devices to identify which one recIndex=5 actually is
            var recDevices = Bass.BASS_RecordGetDeviceInfos();
            for (int ri = 0; ri < recDevices.Length; ri++)
            {
                var rd = recDevices[ri];
                _log.LogInformation("RecordDev[{Idx}]: name='{Name}', id='{Id}', flags={Flags}",
                    ri, rd?.name, rd?.id, rd?.flags);
            }

            // Load BASSloud after BASS_Init so the add-on can register with the engine
            Bass.BASS_PluginLoad("bassloud.dll");

            // 2. Select the output backend for the configured driver mode and create the
            //    BASSmix master mixer through it. Only DirectSound is implemented today;
            //    other modes fall back to DirectSound with a warning (see OutputBackendFactory).
            _outputBackend = OutputBackendFactory.Create(settings.OutputMode, _log);
            _mixerHandle   = _outputBackend.CreateMixer(_routing);

            if (_mixerHandle == 0)
                throw new AudioEngineException(
                    $"BASS_Mixer_StreamCreate failed: {Bass.BASS_ErrorGetCode()}");

            // 3. Register buffer-underrun callback on the mixer (order preserved: before Start).
            //    Only meaningful for a played mixer (DirectSound). Under WASAPI the mixer is a
            //    DECODE stream pulled by the backend, so BASS_SYNC_STALL never fires — WASAPI
            //    signals underruns itself. Skip registration there to avoid a dead sync.
            if (_outputBackend.EffectiveMode == DriverType.DirectSound)
                RegisterStallCallback();

            // 4. Start the backend — the mixer now feeds continuously to the output device
            _outputBackend.Start();

            _log.LogInformation(
                "AudioEngine: output backend={Effective} (requested={Requested})",
                _outputBackend.EffectiveMode, _outputBackend.RequestedMode);

            // DirectSound plays decks straight to a BASS device; WASAPI/ASIO own the card, so decks
            // must go through the master mixer to be audible (see _auxThroughMixer docs).
            _auxThroughMixer = _outputBackend.EffectiveMode != DriverType.DirectSound;

            _isInitialized = true;
        }

        // 5. Start EventBridge drain loop (outside lock — creates a Task)
        _eventBridge.Start();

        // 6. Wire sweeper — must be after Start() so the handler runs on the drain task
        _sweeperTrigger = new SweeperTrigger(_eventBridge);
        _eventBridge.SetSweeperHandler(CreateSweeperStream);

        // 7. AUX position/level monitor — publishes ~10 Hz while any deck is playing
        _auxMonitorTimer = new Timer(AuxMonitorTick, null, 100, 100);

        await _eventBus.PublishAsync(new EngineReadyEvent(
            SampleRate:    (int)settings.SampleRate,
            BufferSize:    (int)settings.BufferSize,
            ActiveDevices: [$"player:{_routing.PlayerDeviceIndex}"]), ct);
    }

    public Task UpdateSettingsAsync(AudioSettings settings, CancellationToken ct = default)
    {
        lock (_lock)
        {
            // Nothing to refresh until the engine is running; InitializeAsync will pick up
            // the latest settings from the DB when it is eventually called.
            if (!_isInitialized) return Task.CompletedTask;

            // Swap the reference only. The ducking DSP and mic paths read _activeSettings
            // live on every callback, so the new AUX/mic ducking values take effect on the
            // next ramp. Engine-level fields (sample rate, buffers, output mode, devices)
            // are baked into the routing graph and require a restart — intentionally ignored.
            _activeSettings = settings;
        }

        _log.LogInformation(
            "AudioEngine: active settings hot-updated (AUX/mic ducking refreshed; " +
            "sample-rate/buffer/device changes still require restart)");
        return Task.CompletedTask;
    }

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        // Drain and stop EventBridge before freeing BASS resources
        await _eventBridge.StopAsync();

        // Stop every streaming encoder FIRST. Each one is a DSP callback attached to the mixer;
        // if the mixer were freed while a session still held it, the next callback would fire on a
        // dead handle and take the process down. Same reason carts and AUX are torn down below.
        foreach (var session in _encoderSessions.Values.ToList())
            session.Dispose();
        _encoderSessions.Clear();

        // Same reason, plus one of its own: a recording left running past this point would be a
        // file with no final block written. Stop() finalises it; Dispose alone would not.
        lock (_recordingLock)
        {
            _recordingSession?.Stop();
            _recordingSession?.Dispose();
            _recordingSession = null;
        }

        // Stop all active carts (before mixer shutdown)
        foreach (var slotId in _cartwallStreams.Keys.ToList())
            StopCartInternal(slotId);

        // Stop the AUX monitor and free all AUX decks
        _auxMonitorTimer?.Dispose();
        _auxMonitorTimer = null;
        foreach (var index in _auxStreams.Keys.ToList())
            EjectAuxInternal(index);

        // Stop active microphone before mixer teardown
        if (_isMicActive)
            await StopMicAsync(ct).ConfigureAwait(false);

        // Cancel any in-progress ducking ramp before BASS teardown
        lock (_duckLock)
        {
            _duckTimer?.Dispose();
            _duckTimer = null;
        }

        // Tear down any in-flight crossfade tails before freeing the mixer
        lock (_fadeLock)
        {
            foreach (var fs in _fading)
            {
                fs.FadeTimer?.Dispose();
                fs.FreeTimer?.Dispose();
                BassMix.BASS_Mixer_ChannelRemove(fs.Stream);
                Bass.BASS_StreamFree(fs.Stream);
            }
            _fading.Clear();

            foreach (var t in _rampTimers) t.Dispose();
            _rampTimers.Clear();
        }

        lock (_lock)
        {
            FreePlaylistStream();
            for (int i = 0; i < _previewStreams.Length; i++)
                FreePreviewLane(i);

            if (_stallGcHandle.IsAllocated) _stallGcHandle.Free();

            // Stop the backend (frees the mixer) before the routing graph frees the devices.
            _outputBackend?.Stop();
            _outputBackend = null;
            _mixerHandle   = 0;

            _routingGraph?.Shutdown();
            _routingGraph    = null;
            _routing         = null;
            _activeSettings  = null;
            _sweeperTrigger  = null;
            _isInitialized   = false;
        }
    }

    public Task LoadTrackAsync(
        string assetId,
        string filePath,
        IReadOnlyList<AssetCuePoint> cuePoints, // registered as BASS_SYNC_POS callbacks
        IReadOnlyList<RDM.Core.Models.EnvelopePoint>? envelope = null,
        CancellationToken ct = default)
    {
        EnsureInitialized();

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Audio file not found: {filePath}", filePath);

        lock (_lock)
        {
            FreePlaylistStream();

            // BASS_STREAM_DECODE: stream decodes to memory, plays through mixer (not directly to device)
            // BASS_SAMPLE_FLOAT: DSP callback (EnvelopeDspCallback) uses float* arithmetic — must match
            int stream = Bass.BASS_StreamCreateFile(
                filePath, 0, 0, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT);

            if (stream == 0)
            {
                BASSError err = Bass.BASS_ErrorGetCode();
                throw err switch
                {
                    BASSError.BASS_ERROR_FILEOPEN => new AudioEngineException(
                        $"Cannot open audio file: '{filePath}'."),
                    BASSError.BASS_ERROR_FILEFORM => new AudioEngineException(
                        $"Unrecognised file format: '{filePath}'."),
                    BASSError.BASS_ERROR_CODEC    => new AudioEngineException(
                        $"Required codec unavailable for: '{filePath}'."),
                    BASSError.BASS_ERROR_FORMAT   => new AudioEngineException(
                        $"Sample format unsupported by current device: '{filePath}'."),
                    _                             => new AudioEngineException(
                        $"BASS_StreamCreateFile failed ({err}): '{filePath}'.")
                };
            }

            // Add to mixer in paused state; PlayAsync will start it
            BassMix.BASS_Mixer_StreamAddChannel(
                _mixerHandle, stream, BASSFlag.BASS_MIXER_CHAN_PAUSE);

            // Apply current ducking level immediately — avoids a brief full-volume window
            // between AddChannel and the next timer tick (~16 ms)
            float duck = _duckingGain * _sweeperDuckGain;
            if (duck < 1.0f)
                Bass.BASS_ChannelSetAttribute(stream, BASSAttribute.BASS_ATTRIB_VOL, duck);

            _playlistStream = stream;
            _currentAssetId = assetId;

            InstallEnvelopeDsp(stream, envelope);

            // Register SYNCPROC callbacks for cue points (must be after AddChannel)
            _cuePointTimer.Register(stream, assetId, cuePoints);
        }

        return Task.CompletedTask;
    }

    public Task LoadInternetStreamAsync(string assetId, string streamUrl, CancellationToken ct = default)
    {
        EnsureInitialized();

        lock (_lock)
        {
            FreePlaylistStream();

            // BASS_StreamCreateURL connects and buffers in the background.
            // Without BASS_STREAM_BLOCK it returns as soon as the connection is
            // established; playback starts automatically once enough data is buffered.
            int stream = Bass.BASS_StreamCreateURL(
                streamUrl, 0,
                BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT,
                null, IntPtr.Zero);

            if (stream == 0)
            {
                var err = Bass.BASS_ErrorGetCode();
                throw new AudioEngineException(
                    $"Cannot connect to internet stream ({err}): '{streamUrl}'");
            }

            BassMix.BASS_Mixer_StreamAddChannel(
                _mixerHandle, stream, BASSFlag.BASS_MIXER_CHAN_PAUSE);

            _playlistStream = stream;
            _currentAssetId = assetId;

            // ICY/Shoutcast metadata — fires whenever the station sends a new StreamTitle
            string capturedAssetId = assetId;
            int    capturedStream  = stream;
            var    capturedLog     = _log;
            _streamMetaProc = (handle, channel, data, user) =>
            {
                nint ptr = Bass.BASS_ChannelGetTags(capturedStream, BASSTag.BASS_TAG_META);
                string? raw = ptr == 0 ? null : Marshal.PtrToStringAnsi(ptr);
                capturedLog.LogDebug("BASS_SYNC_META fired for {AssetId}: raw='{Raw}'", capturedAssetId, raw ?? "(null)");
                if (raw is null) return;

                // Format: "StreamTitle=Artist - Title;StreamUrl=...;"
                string title = ParseIcyTitle(raw);
                _eventBridge.Post(new SyncMessage(
                    Type:        SyncMessageType.StreamMeta,
                    AssetId:     capturedAssetId,
                    StreamTitle: title));
            };
            _streamMetaGcHandle = GCHandle.Alloc(_streamMetaProc, GCHandleType.Normal);
            Bass.BASS_ChannelSetSync(stream, BASSSync.BASS_SYNC_META, 0, _streamMetaProc, IntPtr.Zero);

            // Read initial ICY metadata — may already be available before first SYNC_META fires
            nint initialPtr = Bass.BASS_ChannelGetTags(stream, BASSTag.BASS_TAG_META);
            string? initialRaw = initialPtr == 0 ? null : Marshal.PtrToStringAnsi(initialPtr);
            _log.LogDebug("LoadInternetStreamAsync: initial ICY meta for {AssetId}: '{Raw}'", assetId, initialRaw ?? "(null)");
            if (!string.IsNullOrWhiteSpace(initialRaw))
            {
                string initialTitle = ParseIcyTitle(initialRaw);
                _eventBridge.Post(new SyncMessage(
                    Type:        SyncMessageType.StreamMeta,
                    AssetId:     capturedAssetId,
                    StreamTitle: initialTitle));
            }
        }

        return Task.CompletedTask;
    }

    private static string ParseIcyTitle(string raw)
    {
        // ICY metadata: "StreamTitle=Artist - Title;StreamUrl=http://...;"
        const string prefix = "StreamTitle=";
        int start = raw.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return raw;
        start += prefix.Length;
        int end = raw.IndexOf(';', start);
        return end < 0
            ? raw[start..].Trim('\'', '"', ' ')
            : raw[start..end].Trim('\'', '"', ' ');
    }

    public Task PlayAsync(CancellationToken ct = default)
    {
        EnsureInitialized();

        string? assetId;
        lock (_lock)
        {
            if (_playlistStream == 0)
                throw new AudioEngineException(
                    "No track loaded. Call LoadTrackAsync before PlayAsync.");

            if (!BassMix.BASS_Mixer_ChannelPlay(_playlistStream))
                throw new AudioEngineException(
                    $"BASS_Mixer_ChannelPlay failed: {Bass.BASS_ErrorGetCode()}");

            assetId = _currentAssetId;
        }

        // TrackStarted is application-initiated — publish directly, not through EventBridge
        return _eventBus.PublishAsync(
            new TrackStartedEvent(assetId!, null, null, 0), ct);
    }

    public Task PauseAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        lock (_lock)
        {
            if (_playlistStream == 0)
                throw new AudioEngineException("No track loaded.");

            if (!BassMix.BASS_Mixer_ChannelPause(_playlistStream))
                throw new AudioEngineException(
                    $"BASS_Mixer_ChannelPause failed: {Bass.BASS_ErrorGetCode()}");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(uint fadeoutMs = 0, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (fadeoutMs == 0 || _playlistStream == 0)
            {
                FreePlaylistStream();
            }
            else
            {
                // Fade the current track to silence over fadeoutMs, then free it — instead of
                // cutting instantly. No incoming track inherits the per-stream resources, so
                // release them now, then detach the stream so a subsequent Play can load a
                // fresh one immediately while this tail fades out in the background.
                int outgoing = _playlistStream;
                _sweeperTrigger?.Release();
                _cuePointTimer.Release();
                RemoveEnvelopeDsp();   // reads _playlistStream — must run before we detach it
                FreeStreamMetaSync();
                _playlistStream = 0;
                _currentAssetId = null;

                ScheduleCrossfadeOut(outgoing, fadeoutMs, 0);
            }
        }
        return Task.CompletedTask;
        // TrackEnded with reason "STOPPED" is the caller's responsibility (e.g. PlaylistEngine)
    }

    public Task SeekPlaylistStreamAsync(uint positionMs, CancellationToken ct = default)
    {
        EnsureInitialized();
        lock (_lock)
        {
            if (_playlistStream == 0) return Task.CompletedTask;
            long bytes = Bass.BASS_ChannelSeconds2Bytes(_playlistStream, positionMs / 1000.0);
            if (!Bass.BASS_ChannelSetPosition(_playlistStream, bytes, BASSMode.BASS_POS_BYTE))
                throw new AudioEngineException(
                    $"SeekPlaylistStream failed at {positionMs}ms: {Bass.BASS_ErrorGetCode()}");
        }
        return Task.CompletedTask;
    }

    public Task ResetAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        lock (_lock)
        {
            if (_playlistStream != 0)
            {
                if (!Bass.BASS_ChannelSetPosition(_playlistStream, 0, BASSMode.BASS_POS_BYTE))
                {
                    throw new AudioEngineException(
                        $"BASS_ChannelSetPosition failed: {Bass.BASS_ErrorGetCode()}");
                }
            }
        }
        return Task.CompletedTask;
    }

    public Task SetPlayerLoopAsync(
        bool loop, uint loopStartMs = 0, uint loopEndMs = 0, CancellationToken ct = default)
    {
        EnsureInitialized();
        lock (_lock)
        {
            _playerLoop        = loop;
            _playerLoopStartMs = loopStartMs;
            _playerLoopEndMs   = loopEndMs;
            ApplyPlayerLoopLocked();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// (Re)arms the loop sync for the current state. Arming with no stream loaded is not an
    /// error — the wish is remembered and the caller re-arms with the new track's region on
    /// the next load, which is what makes "loop pressed before Play" work.
    /// Caller holds _lock.
    /// </summary>
    private void ApplyPlayerLoopLocked()
    {
        ReleasePlayerLoopSyncLocked();
        if (!_playerLoop || _playlistStream == 0) return;

        int  stream = _playlistStream;
        long len    = Bass.BASS_ChannelGetLength(stream, BASSMode.BASS_POS_BYTE);

        long endBytes = _playerLoopEndMs > 0
            ? Bass.BASS_ChannelSeconds2Bytes(stream, _playerLoopEndMs / 1000.0)
            : len;

        // A loop point sitting on the very last sample races EOF: BASS may retire the source
        // before the sync is reached. Back off ~30 ms, as the AUX decks do.
        if (len > 0 && endBytes >= len)
        {
            double lenSec = Bass.BASS_ChannelBytes2Seconds(stream, len);
            endBytes = Bass.BASS_ChannelSeconds2Bytes(stream, Math.Max(0, lenSec - 0.03));
        }

        long startBytes = Bass.BASS_ChannelSeconds2Bytes(stream, _playerLoopStartMs / 1000.0);
        if (endBytes <= startBytes)
        {
            _log.LogWarning(
                "Player loop not armed: loop region is empty (start={StartMs}ms, end={EndMs}ms)",
                _playerLoopStartMs, _playerLoopEndMs);
            return;
        }

        // Captured for the callback: a mixtime SYNCPROC must not read engine fields under _lock.
        string loopAssetId = _currentAssetId ?? string.Empty;
        uint   loopStartMs = _playerLoopStartMs;

        SYNCPROC proc = (h, c, d, u) =>
        {
            // Mixtime: the seek lands exactly on the loop point, so the repeat is gapless.
            BassMix.BASS_Mixer_ChannelSetPosition(stream, startBytes, BASSMode.BASS_POS_BYTE);
            // Everything that displays a position counts wall-clock time from the track start,
            // so the rewind has to be announced or the playhead runs off the end and freezes.
            _eventBridge.Post(new SyncMessage(
                Type:        SyncMessageType.PlayerLooped,
                AssetId:     loopAssetId,
                PositionMs:  loopStartMs,
                PostProcess: ReArmCuePointsAfterLoop));
        };

        _playerLoopProc = proc;
        _playerLoopGch  = GCHandle.Alloc(proc, GCHandleType.Normal);
        _playerLoopSync = BassMix.BASS_Mixer_ChannelSetSync(
            stream, BASSSync.BASS_SYNC_POS | BASSSync.BASS_SYNC_MIXTIME, endBytes, proc, IntPtr.Zero);

        _log.LogDebug("Player loop armed: {StartMs}ms → {EndMs}ms",
            _playerLoopStartMs, _playerLoopEndMs);
    }

    // Runs on the EventBridge drain thread (never inside the mixtime callback).
    private void ReArmCuePointsAfterLoop()
    {
        lock (_lock)
        {
            if (!_playerLoop || _playlistStream == 0) return;
            _cuePointTimer.ReArm();
            // ReArm re-registers through CuePointTimer only; the loop sync itself is a separate
            // registration and stays armed, so the next pass loops again without re-arming here.
        }
    }

    // Caller holds _lock. Must run while _playlistStream still refers to the looping stream.
    private void ReleasePlayerLoopSyncLocked()
    {
        if (_playerLoopSync != 0 && _playlistStream != 0)
            BassMix.BASS_Mixer_ChannelRemoveSync(_playlistStream, _playerLoopSync);
        if (_playerLoopGch.IsAllocated) _playerLoopGch.Free();
        _playerLoopSync = 0;
        _playerLoopProc = null;
    }

    public Task SetVolumeAsync(string sourceId, float gainLinear, CancellationToken ct = default)
    {
        EnsureInitialized();
        float clamped = Math.Clamp(gainLinear, 0.0f, 1.0f);

        lock (_lock)
        {
            // "player" → master output on mixer handle
            // anything else → playlist source channel (for per-source ducking in AUDIO-002D)
            int target = string.Equals(sourceId, "player", StringComparison.OrdinalIgnoreCase)
                ? _mixerHandle
                : (_playlistStream != 0 ? _playlistStream : _mixerHandle);

            if (target != 0)
                Bass.BASS_ChannelSetAttribute(target, BASSAttribute.BASS_ATTRIB_VOL, clamped);
        }
        return Task.CompletedTask;
    }

    public Task FadeOutAsync(uint durationMs, CancellationToken ct = default)
    {
        EnsureInitialized();
        int stream;
        lock (_lock) stream = _playlistStream;
        StartEqualPowerFadeOut(stream, durationMs);
        return Task.CompletedTask;
    }

    /// Ramps a stream's BASS_ATTRIB_VOL to 0 along a quarter-cosine (equal-power) curve over
    /// <paramref name="durationMs"/>. This is the single, fixed crossfade/fade-out shape: with an
    /// incoming track playing at full, a linear amplitude fade dips in perceived loudness through
    /// the middle of the overlap, whereas cos() keeps it roughly constant. Stepped on a timer
    /// because BASS_ChannelSlideAttribute only does linear amplitude slides; the ~20 ms step is
    /// inaudible over second-scale crossfades. <paramref name="durationMs"/> == 0 cuts to silence.
    private void StartEqualPowerFadeOut(int stream, uint durationMs)
    {
        if (stream == 0) return;
        if (durationMs == 0)
        {
            lock (_lock) Bass.BASS_ChannelSetAttribute(stream, BASSAttribute.BASS_ATTRIB_VOL, 0f);
            return;
        }

        float startGain = 1f;
        lock (_lock) Bass.BASS_ChannelGetAttribute(stream, BASSAttribute.BASS_ATTRIB_VOL, ref startGain);

        const int stepMs = 20;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Timer timer = null!;
        timer = new Timer(_ =>
        {
            double t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / durationMs);
            float  g = (float)(startGain * Math.Cos(t * Math.PI / 2.0));   // equal-power taper
            lock (_lock)
                Bass.BASS_ChannelSetAttribute(stream, BASSAttribute.BASS_ATTRIB_VOL, Math.Clamp(g, 0f, 1f));

            if (t >= 1.0)
            {
                lock (_fadeLock) _rampTimers.Remove(timer);
                timer.Dispose();
            }
        }, null, stepMs, stepMs);

        lock (_fadeLock) _rampTimers.Add(timer);
    }

    public Task CrossfadeToAsync(
        string assetId,
        string filePath,
        IReadOnlyList<AssetCuePoint> cuePoints,
        uint cueStartMs,
        uint fadeOutMs,
        uint fadeDelayMs,
        IReadOnlyList<RDM.Core.Models.EnvelopePoint>? envelope = null,
        CancellationToken ct = default)
    {
        EnsureInitialized();

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Audio file not found: {filePath}", filePath);

        string? assetIdForEvent;

        lock (_lock)
        {
            // The outgoing stream keeps playing through the crossfade. Detach its cue
            // syncs (incl. END) so they don't fire against the new current track.
            int outgoing = _playlistStream;
            _sweeperTrigger?.Release();
            _cuePointTimer.Release();
            // The outgoing stream keeps playing to the end of the fade — drop its loop sync too,
            // or it would rewind the track that is on its way out.
            ReleasePlayerLoopSyncLocked();

            int incoming = Bass.BASS_StreamCreateFile(filePath, 0, 0, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT);
            if (incoming == 0)
                throw new AudioEngineException(
                    $"Crossfade BASS_StreamCreateFile failed ({Bass.BASS_ErrorGetCode()}): '{filePath}'.");

            // Add paused so we can position before the first sample is mixed
            BassMix.BASS_Mixer_StreamAddChannel(
                _mixerHandle, incoming, BASSFlag.BASS_MIXER_CHAN_PAUSE);

            float duckGain = _duckingGain * _sweeperDuckGain;
            if (duckGain < 1.0f)
                Bass.BASS_ChannelSetAttribute(incoming, BASSAttribute.BASS_ATTRIB_VOL, duckGain);

            if (cueStartMs > 0)
            {
                long bytes = Bass.BASS_ChannelSeconds2Bytes(incoming, cueStartMs / 1000.0);
                if (Bass.BASS_ChannelSetPosition(incoming, bytes, BASSMode.BASS_POS_BYTE))
                    _log.LogDebug("Crossfade: seeked to CueStart {CueStartMs}ms for {AssetId}", cueStartMs, assetId);
                else
                    _log.LogWarning("Crossfade: seek to CueStart {CueStartMs}ms failed for {AssetId} ({Err}) — will play from 0",
                        cueStartMs, assetId, Bass.BASS_ErrorGetCode());
            }

            _playlistStream = incoming;
            _currentAssetId = assetId;
            assetIdForEvent = assetId;

            InstallEnvelopeDsp(incoming, envelope);

            // Register the incoming track's own cue points (so its StartNext chains the
            // next crossfade) and start it at full level alongside the fading outgoing.
            _cuePointTimer.Register(incoming, assetId, cuePoints);
            BassMix.BASS_Mixer_ChannelPlay(incoming);

            if (outgoing != 0)
                ScheduleCrossfadeOut(outgoing, fadeOutMs, fadeDelayMs);
        }

        // Application-initiated — publish directly (mirrors PlayAsync)
        return _eventBus.PublishAsync(
            new TrackStartedEvent(assetIdForEvent!, null, null, 0), ct);
    }

    /// Fades the outgoing stream to silence (after an optional delay) and frees it once
    /// the fade has finished. Timers are tracked so they survive GC and shutdown cleanup.
    private void ScheduleCrossfadeOut(int stream, uint fadeMs, uint delayMs)
    {
        var fs = new FadingStream { Stream = stream };
        lock (_fadeLock) _fading.Add(fs);

        // Equal-power (quarter-cosine) fade of the outgoing track under the incoming (which
        // plays at full). fadeMs == 0 → immediate cut. See StartEqualPowerFadeOut.
        fs.FadeTimer = new Timer(_ => StartEqualPowerFadeOut(stream, fadeMs), null, delayMs, Timeout.Infinite);

        // Free shortly after the fade completes (+tail for the slide to settle).
        fs.FreeTimer = new Timer(_ => FreeFadingStream(fs), null, delayMs + fadeMs + 60, Timeout.Infinite);
    }

    private void FreeFadingStream(FadingStream fs)
    {
        lock (_fadeLock)
        {
            if (!_fading.Remove(fs)) return; // already freed (e.g. by shutdown)
        }
        lock (_lock)
        {
            BassMix.BASS_Mixer_ChannelRemove(fs.Stream);
            Bass.BASS_StreamFree(fs.Stream);
        }
        fs.FadeTimer?.Dispose();
        fs.FreeTimer?.Dispose();
    }

    // ── Cartwall ─────────────────────────────────────────────────────────────

    public Task TriggerCartAsync(
        string slotId, string assetId, string filePath, bool loop,
        CancellationToken ct = default)
    {
        EnsureInitialized();

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Cart audio file not found: {filePath}", filePath);

        string currentMode = _cartwallMode;
        if (currentMode == "OFF")
            return Task.CompletedTask;

        // Stop whatever is already on this slot
        StopCartInternal(slotId);

        if (_cartwallStreams.Count >= 8)
            throw new AudioEngineException(
                "Maximum of 8 simultaneous cartwall streams reached.");

        // ON goes through the master mixer unless the cartwall has a card of its own: under WASAPI/ASIO
        // the backend owns the player card, so an unassigned cartwall played direct would land on No
        // Sound. A cartwall assigned to a DIFFERENT card plays direct to it, exactly like AUX does — the
        // `is null` test is what makes the assignment count instead of being silently discarded.
        // An assignment naming the card the backend already owns is dropped in RoutingGraph and arrives
        // here as null, so it correctly falls back to the mixer. PFL always plays direct to the PFL card.
        bool useMixer = _auxThroughMixer && currentMode != "PFL" && _routing!.CartwallDeviceIndex is null;
        int cartwallDevice = currentMode == "PFL"
            ? (_routing!.PflDeviceIndex ?? _routing.PlayerDeviceIndex)
            : (_routing!.CartwallDeviceIndex ?? _routing.PlayerDeviceIndex);
        int cartHandle;

        lock (_lock)
        {
            // BASS_SAMPLE_LOOP: BASS handles looping natively (no SYNCPROC needed for loop)
            BASSFlag loopFlag = loop ? BASSFlag.BASS_SAMPLE_LOOP : BASSFlag.BASS_DEFAULT;
            if (useMixer)
            {
                cartHandle = Bass.BASS_StreamCreateFile(
                    filePath, 0, 0, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT | loopFlag);
                if (cartHandle != 0)
                    BassMix.BASS_Mixer_StreamAddChannel(
                        _mixerHandle, cartHandle, BASSFlag.BASS_MIXER_CHAN_PAUSE);
            }
            else
            {
                // Device selection + stream creation must be atomic
                Bass.BASS_SetDevice(cartwallDevice);
                cartHandle = Bass.BASS_StreamCreateFile(filePath, 0, 0, loopFlag);
            }
        }

        if (cartHandle == 0)
            throw new AudioEngineException(
                $"Cart BASS_StreamCreateFile failed ({Bass.BASS_ErrorGetCode()}): '{filePath}'.");

        GCHandle gcHandle = default;

        if (!loop)
        {
            // One-shot: fire SYNCPROC at natural end → post CartEnded + cleanup
            string capturedSlotId  = slotId;
            string capturedAssetId = assetId;

            SYNCPROC endProc = (handle, channel, data, user) =>
                _eventBridge.Post(new SyncMessage(
                    Type:        SyncMessageType.CartEnded,
                    SlotId:      capturedSlotId,
                    AssetId:     capturedAssetId,
                    PostProcess: () => CleanupCart(capturedSlotId)));

            gcHandle = GCHandle.Alloc(endProc, GCHandleType.Normal);
            // Mixer sources need the BASSmix sync API; a plain BASS_ChannelSetSync would never fire.
            if (useMixer)
                BassMix.BASS_Mixer_ChannelSetSync(cartHandle,
                    BASSSync.BASS_SYNC_END | BASSSync.BASS_SYNC_ONETIME, 0, endProc, IntPtr.Zero);
            else
                Bass.BASS_ChannelSetSync(cartHandle,
                    BASSSync.BASS_SYNC_END | BASSSync.BASS_SYNC_ONETIME, 0, endProc, IntPtr.Zero);
        }
        // Loop carts: BASS_SAMPLE_LOOP handles repetition; no SYNCPROC required

        _cartwallStreams[slotId] = new CartState(cartHandle, assetId, loop, gcHandle, useMixer);
        if (useMixer)
            BassMix.BASS_Mixer_ChannelPlay(cartHandle);
        else
            Bass.BASS_ChannelPlay(cartHandle, false);

        long  lenBytes   = Bass.BASS_ChannelGetLength(cartHandle, BASSMode.BASS_POS_BYTE);
        uint  durationMs = lenBytes > 0 ? (uint)(Bass.BASS_ChannelBytes2Seconds(cartHandle, lenBytes) * 1000) : 0;

        return _eventBus.PublishAsync(new CartTriggeredEvent(slotId, assetId, loop, durationMs), ct);
    }

    public Task StopCartAsync(string slotId, CancellationToken ct = default)
    {
        if (!_cartwallStreams.TryRemove(slotId, out var state))
            return Task.CompletedTask;

        FreeCartStream(state);

        return _eventBus.PublishAsync(new CartEndedEvent(slotId, state.AssetId), ct);
    }

    public async Task SetCartwallModeAsync(string mode, CancellationToken ct = default)
    {
        _cartwallMode = mode.ToUpperInvariant();

        if (_cartwallMode == "OFF")
        {
            var slotIds = _cartwallStreams.Keys.ToList();
            foreach (var slotId in slotIds)
            {
                if (_cartwallStreams.TryRemove(slotId, out var state))
                {
                    FreeCartStream(state);
                    await _eventBus.PublishAsync(new CartEndedEvent(slotId, state.AssetId), ct);
                }
            }
        }
    }

    public Task StartPflAsync(string assetId, string filePathOrUrl, CancellationToken ct = default)
    {
        EnsureInitialized();

        bool isUrl = filePathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                  || filePathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        if (!isUrl && !File.Exists(filePathOrUrl))
            throw new FileNotFoundException($"PFL audio file not found: {filePathOrUrl}", filePathOrUrl);

        lock (_lock)
        {
            if (_pflStream != 0)
            {
                Bass.BASS_ChannelStop(_pflStream);
                Bass.BASS_StreamFree(_pflStream);
                _pflStream = 0;
                if (_pflEndGcHandle.IsAllocated) _pflEndGcHandle.Free();
                _pflEndProc = null;
            }

            int pflDevice = _routing!.PflDeviceIndex ?? _routing.PlayerDeviceIndex;
            Bass.BASS_SetDevice(pflDevice);

            _pflStream = isUrl
                ? Bass.BASS_StreamCreateURL(filePathOrUrl, 0, BASSFlag.BASS_DEFAULT, null, IntPtr.Zero)
                : Bass.BASS_StreamCreateFile(filePathOrUrl, 0, 0, BASSFlag.BASS_DEFAULT);
            _pflAssetId = assetId;

            if (_pflStream != 0)
            {
                string capturedAssetId = assetId;
                _pflEndProc = (handle, channel, data, user) =>
                    _eventBridge.Post(new SyncMessage(
                        Type:    SyncMessageType.PflEnded,
                        AssetId: capturedAssetId));

                _pflEndGcHandle = GCHandle.Alloc(_pflEndProc, GCHandleType.Normal);
                Bass.BASS_ChannelSetSync(
                    _pflStream,
                    BASSSync.BASS_SYNC_END | BASSSync.BASS_SYNC_ONETIME,
                    0, _pflEndProc, IntPtr.Zero);

                Bass.BASS_ChannelPlay(_pflStream, false);
            }
            else
            {
                throw new AudioEngineException($"PFL stream create failed: {Bass.BASS_ErrorGetCode()}");
            }
        }
        return Task.CompletedTask;
    }

    public Task StopPflAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_pflStream != 0)
            {
                Bass.BASS_ChannelStop(_pflStream);
                Bass.BASS_StreamFree(_pflStream);
                _pflStream = 0;
                if (_pflEndGcHandle.IsAllocated) _pflEndGcHandle.Free();
                _pflEndProc = null;
                _pflAssetId = null;
            }
        }
        return Task.CompletedTask;
    }

    public Task SeekPflAsync(int offsetMs, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_pflStream == 0) return Task.CompletedTask;
            long curBytes = Bass.BASS_ChannelGetPosition(_pflStream, BASSMode.BASS_POS_BYTE);
            double curSec  = Bass.BASS_ChannelBytes2Seconds(_pflStream, curBytes);
            double newSec  = Math.Max(0, curSec + offsetMs / 1000.0);
            long newBytes  = Bass.BASS_ChannelSeconds2Bytes(_pflStream, newSec);
            Bass.BASS_ChannelSetPosition(_pflStream, newBytes, BASSMode.BASS_POS_BYTE);
        }
        return Task.CompletedTask;
    }

    // ── Multi-lane PFL preview (segue editor) ──────────────────────────────────

    public Task PreviewPlayAsync(int lane, string filePath, int positionMs, float gain, CancellationToken ct = default)
    {
        EnsureInitialized();
        if (lane < 0 || lane >= _previewStreams.Length) return Task.CompletedTask;
        if (!File.Exists(filePath)) return Task.CompletedTask;

        lock (_lock)
        {
            FreePreviewLane(lane);

            int pflDevice = _routing!.PflDeviceIndex ?? _routing.PlayerDeviceIndex;
            Bass.BASS_SetDevice(pflDevice);

            int stream = Bass.BASS_StreamCreateFile(filePath, 0, 0, BASSFlag.BASS_DEFAULT);
            if (stream == 0) return Task.CompletedTask;
            _previewStreams[lane] = stream;

            if (positionMs > 0)
            {
                double sec   = positionMs / 1000.0;
                long   bytes = Bass.BASS_ChannelSeconds2Bytes(stream, sec);
                Bass.BASS_ChannelSetPosition(stream, bytes, BASSMode.BASS_POS_BYTE);
            }

            Bass.BASS_ChannelSetAttribute(stream, BASSAttribute.BASS_ATTRIB_VOL, Math.Clamp(gain, 0f, 1f));
            Bass.BASS_ChannelPlay(stream, false);
        }
        return Task.CompletedTask;
    }

    public Task PreviewSetGainAsync(int lane, float gain, CancellationToken ct = default)
    {
        if (lane < 0 || lane >= _previewStreams.Length) return Task.CompletedTask;
        lock (_lock)
        {
            int stream = _previewStreams[lane];
            if (stream != 0)
                Bass.BASS_ChannelSetAttribute(stream, BASSAttribute.BASS_ATTRIB_VOL, Math.Clamp(gain, 0f, 1f));
        }
        return Task.CompletedTask;
    }

    public Task PreviewStopAsync(int lane, CancellationToken ct = default)
    {
        if (lane < 0 || lane >= _previewStreams.Length) return Task.CompletedTask;
        lock (_lock) FreePreviewLane(lane);
        return Task.CompletedTask;
    }

    public Task PreviewStopAllAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            for (int i = 0; i < _previewStreams.Length; i++)
                FreePreviewLane(i);
        }
        return Task.CompletedTask;
    }

    public int GetPreviewPositionMs(int lane)
    {
        if (lane < 0 || lane >= _previewStreams.Length) return -1;
        lock (_lock)
        {
            int stream = _previewStreams[lane];
            if (stream == 0) return -1;
            if (Bass.BASS_ChannelIsActive(stream) != BASSActive.BASS_ACTIVE_PLAYING) return -1;

            long bytes = Bass.BASS_ChannelGetPosition(stream, BASSMode.BASS_POS_BYTE);
            if (bytes < 0) return -1;
            return (int)(Bass.BASS_ChannelBytes2Seconds(stream, bytes) * 1000);
        }
    }

    public Task PreviewPauseAsync(int lane, CancellationToken ct = default)
    {
        if (lane < 0 || lane >= _previewStreams.Length) return Task.CompletedTask;
        lock (_lock)
        {
            int stream = _previewStreams[lane];
            if (stream != 0) Bass.BASS_ChannelPause(stream);
        }
        return Task.CompletedTask;
    }

    public Task PreviewResumeAsync(int lane, CancellationToken ct = default)
    {
        if (lane < 0 || lane >= _previewStreams.Length) return Task.CompletedTask;
        lock (_lock)
        {
            int stream = _previewStreams[lane];
            if (stream != 0) Bass.BASS_ChannelPlay(stream, false);
        }
        return Task.CompletedTask;
    }

    public Task PreviewSeekAsync(int lane, int positionMs, CancellationToken ct = default)
    {
        if (lane < 0 || lane >= _previewStreams.Length) return Task.CompletedTask;
        lock (_lock)
        {
            int stream = _previewStreams[lane];
            if (stream == 0) return Task.CompletedTask;
            double sec   = Math.Max(0, positionMs / 1000.0);
            long   bytes = Bass.BASS_ChannelSeconds2Bytes(stream, sec);
            Bass.BASS_ChannelSetPosition(stream, bytes, BASSMode.BASS_POS_BYTE);
        }
        return Task.CompletedTask;
    }

    private void FreePreviewLane(int lane)
    {
        int stream = _previewStreams[lane];
        if (stream == 0) return;
        Bass.BASS_ChannelStop(stream);
        Bass.BASS_StreamFree(stream);
        _previewStreams[lane] = 0;
    }

    // ── On-demand sample-accurate waveform (segue editor zoom) ──────────────────

    public Task<WaveformWindow?> ReadWaveformWindowAsync(
        string filePath, double startMs, double endMs, int columns, CancellationToken ct = default)
        => Task.Run<WaveformWindow?>(() =>
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)
                || columns < 1 || endMs <= startMs)
                return null;

            // Independent decode stream — does not touch playback channels, so no lock
            // and no output device required (decode channels render on demand).
            int stream = Bass.BASS_StreamCreateFile(
                filePath, 0, 0, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT);
            if (stream == 0) return null;

            try
            {
                var info  = Bass.BASS_ChannelGetInfo(stream);
                int chans = info is { chans: > 0 } ? info.chans : 2;

                long total     = Bass.BASS_ChannelGetLength(stream, BASSMode.BASS_POS_BYTE);
                long startByte = Bass.BASS_ChannelSeconds2Bytes(stream, startMs / 1000.0);
                long endByte   = Bass.BASS_ChannelSeconds2Bytes(stream, endMs   / 1000.0);
                if (total > 0) endByte = Math.Min(endByte, total);
                if (endByte <= startByte) return null;

                ct.ThrowIfCancellationRequested();
                Bass.BASS_ChannelSetPosition(stream, startByte, BASSMode.BASS_POS_BYTE);

                // Read the whole (small) window in one shot. Cap to guard against an
                // accidentally wide request (~24 MB of float data ≈ 60 s stereo @ 44.1 kHz).
                const int maxFloats = 6_000_000;
                int floatCount = (int)Math.Min((endByte - startByte) / sizeof(float), maxFloats);
                if (floatCount < chans) return null;

                float[] buf       = new float[floatCount];
                int     readBytes = Bass.BASS_ChannelGetData(stream, buf, floatCount * sizeof(float));
                if (readBytes <= 0) return null;

                int frames = (readBytes / sizeof(float)) / chans; // mono samples in the window
                if (frames < 1) return null;

                // At ≤ 1 sample per column we expose the raw signal so the renderer can
                // connect it into a continuous line (Audacity sample view).
                bool sampleLevel = frames <= columns;
                int  outCols     = sampleLevel ? frames : columns;
                var  minmax      = new float[outCols * 2];

                if (sampleLevel)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        float v = MonoAt(buf, f, chans);
                        minmax[f * 2]     = v;
                        minmax[f * 2 + 1] = v;
                    }
                }
                else
                {
                    for (int c = 0; c < outCols; c++)
                    {
                        int f0 = (int)((long)c       * frames / outCols);
                        int f1 = (int)((long)(c + 1) * frames / outCols);
                        if (f1 <= f0) f1 = f0 + 1;

                        float mn = 0f, mx = 0f;
                        for (int f = f0; f < f1 && f < frames; f++)
                        {
                            float v = MonoAt(buf, f, chans);
                            if (v < mn) mn = v;
                            if (v > mx) mx = v;
                        }
                        minmax[c * 2]     = mn;
                        minmax[c * 2 + 1] = mx;
                    }
                }

                return new WaveformWindow(startMs, endMs, minmax, sampleLevel);
            }
            catch (OperationCanceledException) { return null; }
            catch { return null; }
            finally { Bass.BASS_StreamFree(stream); }
        }, ct);

    // ── Cue-point detection ──────────────────────────────────────────────────

    public Task<(double? Start, double? NextStart, double? End, double? DurationSec)?> AnalyzeCuePointsAsync(
        string filePath, double startDb, double nextStartDb, double endDb,
        CancellationToken ct = default)
        => Task.Run<(double?, double?, double?, double?)?>(() =>
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return null;

            float startAmp     = (float)Math.Pow(10.0, startDb     / 20.0);
            float nextStartAmp = (float)Math.Pow(10.0, nextStartDb / 20.0);
            float endAmp       = (float)Math.Pow(10.0, endDb       / 20.0);

            int stream = Bass.BASS_StreamCreateFile(
                filePath, 0, 0, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT);
            if (stream == 0) return null;

            try
            {
                var info   = Bass.BASS_ChannelGetInfo(stream);
                int chans  = info is { chans: > 0 } ? info.chans : 2;
                int rate   = info?.freq ?? 44100;

                // 0.1 s analysis frame
                int framesPerBlock = rate / 10;
                int floatsPerBlock = framesPerBlock * chans;
                var buf = new float[floatsPerBlock];

                double? startSec     = null;
                double? endSec       = null;
                double? nextStartSec = null;
                double  posSec       = 0.0;

                while (true)
                {
                    ct.ThrowIfCancellationRequested();

                    int readBytes = Bass.BASS_ChannelGetData(
                        stream, buf, floatsPerBlock * sizeof(float));
                    if (readBytes <= 0) break;

                    int frames  = (readBytes / sizeof(float)) / chans;
                    double dur  = frames / (double)rate;
                    float  peak = 0f;
                    for (int f = 0; f < frames; f++)
                    {
                        float v = Math.Abs(MonoAt(buf, f, chans));
                        if (v > peak) peak = v;
                    }

                    if (startSec is null && peak >= startAmp)
                        startSec = posSec;

                    if (peak >= endAmp)
                        endSec = posSec + dur;

                    if (peak >= nextStartAmp)
                        nextStartSec = posSec + dur;

                    posSec += dur;
                }

                if (startSec is null && endSec is null)
                    return null;

                // NEXT_START should not exceed END
                if (nextStartSec.HasValue && endSec.HasValue && nextStartSec > endSec)
                    nextStartSec = endSec;

                return (startSec, nextStartSec, endSec, (double?)posSec);
            }
            catch (OperationCanceledException) { return null; }
            catch { return null; }
            finally { Bass.BASS_StreamFree(stream); }
        }, ct);

    /// Averages all interleaved channels of one frame down to a single mono sample.
    private static float MonoAt(float[] interleaved, int frame, int chans)
    {
        int b = frame * chans;
        if (chans == 1) return interleaved[b];
        float sum = 0f;
        for (int c = 0; c < chans; c++) sum += interleaved[b + c];
        return sum / chans;
    }

    // ── AUX players ────────────────────────────────────────────────────────────

    public Task<AuxLoadResult> LoadAuxAsync(int index, string filePath, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            EnsureInitialized();

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"AUX audio file not found: {filePath}", filePath);

            // Replace whatever is already loaded on this deck
            EjectAuxInternal(index);

            // Fresh deck: ON by default, so under WASAPI/ASIO it is a decode source of the master
            // mixer; under DirectSound (or once switched to PFL) it plays direct to a device.
            // A deck with its own assigned output card always plays direct to that card (see
            // AuxUsesMixer), so it works on-air on a separate card in every output mode.
            var st = new AuxState
            {
                FilePath    = filePath,
                DeviceIndex = index >= 0 && index < 4 ? _routing?.AuxDeviceIndices[index] : null
            };
            lock (_lock)
            {
                if (!CreateAuxHandleLocked(st))
                    throw new AudioEngineException(
                        $"AUX BASS_StreamCreateFile failed ({Bass.BASS_ErrorGetCode()}): '{filePath}'.");
            }
            int handle = st.StreamHandle;

            long lenBytes   = Bass.BASS_ChannelGetLength(handle, BASSMode.BASS_POS_BYTE);
            uint durationMs = lenBytes > 0
                ? (uint)(Bass.BASS_ChannelBytes2Seconds(handle, lenBytes) * 1000)
                : 0;
            st.DurationMs = durationMs;

            // Waveform cache (one .wvf per deck, overwritten on each load)
            string wvfDir  = Path.Combine(Path.GetTempPath(), "rdm_aux");
            string wvfPath = Path.Combine(wvfDir, $"aux{index}.wvf");
            Directory.CreateDirectory(wvfDir);
            byte[] auxWaveData = await new WaveformGenerator().GenerateAsync(filePath, ct)
                .ConfigureAwait(false);
            await File.WriteAllBytesAsync(wvfPath, auxWaveData, ct).ConfigureAwait(false);

            // Auto-cue from the Silence Remover thresholds — only when it is enabled.
            // Thresholds come exclusively from settings; no hardcoded fallbacks.
            uint startMs = 0;
            uint endMs   = durationMs;
            if (_activeSettings?.SilenceRemoverEnabled == true)
            {
                var cues = await AnalyzeCuePointsAsync(
                    filePath,
                    (double)_activeSettings.SilenceStartThresholdDb,
                    (double)_activeSettings.SilenceMixThresholdDb,
                    (double)_activeSettings.SilenceEndThresholdDb,
                    ct).ConfigureAwait(false);

                if (cues?.Start is double s) startMs = (uint)(s * 1000);
                if (cues?.End   is double e) endMs   = (uint)(e * 1000);
            }

            st.StartMs = startMs;
            st.EndMs   = endMs > 0 ? endMs : durationMs;

            lock (_lock)
            {
                // END cue + looping: mixer decks loop the region seamlessly via a mixtime sync;
                // device decks are enforced by AuxMonitorTick. Registered now that cues are known.
                RegisterAuxEndSyncLocked(st, index);
                ApplyAuxRouting(st);

                // Pre-position at the START cue so the playhead and first PLAY begin there.
                long startPosBytes = Bass.BASS_ChannelSeconds2Bytes(handle, st.StartMs / 1000.0);
                AuxSetPositionBytes(st, startPosBytes);
            }
            _auxStreams[index] = st;

            // Notify the UI so the tile fills in (name, cues, waveform) regardless of trigger source
            // — in-app command, API, or MIDI/script via ScriptingFacade.
            await _eventBus.PublishAsync(
                new AuxLoadedEvent(index, filePath, durationMs, wvfPath, st.StartMs, st.EndMs), ct);

            return new AuxLoadResult(durationMs, wvfPath, startMs, endMs);
        }, ct);

    public Task PlayAuxAsync(int index, CancellationToken ct = default)
    {
        EnsureInitialized();
        if (!_auxStreams.TryGetValue(index, out var st) || st.StreamHandle == 0)
            return Task.CompletedTask;

        if (st.StopFadeTimer is not null)
        {
            // A previous StopAuxAsync(fadeoutMs) is still ramping down — cancel it and restore
            // the deck's configured volume so playback resumes at full level, not mid-fade.
            st.StopFadeTimer.Dispose();
            st.StopFadeTimer = null;
            ApplyAuxRouting(st);
        }

        // Begin from the START cue unless we are resuming inside the cue region.
        long posBytes = AuxGetPositionBytes(st);
        uint posMs    = posBytes > 0
            ? (uint)(Bass.BASS_ChannelBytes2Seconds(st.StreamHandle, posBytes) * 1000)
            : 0;
        if (posMs < st.StartMs || posMs >= st.EndMs)
            AuxSetPositionBytes(st, Bass.BASS_ChannelSeconds2Bytes(st.StreamHandle, st.StartMs / 1000.0));

        PlayAuxDeck(st);
        return _eventBus.PublishAsync(new AuxActivatedEvent(index), ct);
    }

    public Task PauseAuxAsync(int index, CancellationToken ct = default)
    {
        EnsureInitialized();
        if (_auxStreams.TryGetValue(index, out var st) && st.StreamHandle != 0)
            PauseAuxDeck(st);
        return Task.CompletedTask;
    }

    public Task StopAuxAsync(int index, uint fadeoutMs = 0, CancellationToken ct = default)
    {
        EnsureInitialized();
        if (!_auxStreams.TryGetValue(index, out var st) || st.StreamHandle == 0)
            return Task.CompletedTask;

        st.StopFadeTimer?.Dispose();
        st.StopFadeTimer = null;

        if (fadeoutMs == 0)
        {
            PauseAuxDeck(st);
            AuxSetPositionBytes(st, Bass.BASS_ChannelSeconds2Bytes(st.StreamHandle, st.StartMs / 1000.0));
            return _eventBus.PublishAsync(new AuxDeactivatedEvent(index), ct);
        }

        // Ramp to silence, then pause+rewind+restore volume once the fade settles. Mirrors the
        // main player's StopAsync(fadeoutMs) — see StartEqualPowerFadeOut.
        StartEqualPowerFadeOut(st.StreamHandle, fadeoutMs);

        Timer timer = null!;
        timer = new Timer(_ =>
        {
            lock (_lock)
            {
                if (!ReferenceEquals(st.StopFadeTimer, timer) || st.StreamHandle == 0) return;
                PauseAuxDeck(st);
                AuxSetPositionBytes(st, Bass.BASS_ChannelSeconds2Bytes(st.StreamHandle, st.StartMs / 1000.0));
                ApplyAuxRouting(st);
                st.StopFadeTimer = null;
            }
            _ = _eventBus.PublishAsync(new AuxDeactivatedEvent(index));
        }, null, fadeoutMs + 60, Timeout.Infinite);

        st.StopFadeTimer = timer;
        return Task.CompletedTask;
    }

    public Task EjectAuxAsync(int index, CancellationToken ct = default)
    {
        EjectAuxInternal(index);
        return _eventBus.PublishAsync(new AuxDeactivatedEvent(index), ct);
    }

    public Task SetAuxLoopAsync(int index, bool loop, CancellationToken ct = default)
    {
        if (!_auxStreams.TryGetValue(index, out var st))
            return Task.CompletedTask;

        st.Loop = loop;
        // Mixer decks: BASS_SAMPLE_LOOP keeps the source from being dropped at EOF so the
        // mixtime END sync can rewind the START..END region. Device decks loop via the monitor.
        if (st.InMixer && st.StreamHandle != 0)
            Bass.BASS_ChannelFlags(st.StreamHandle,
                loop ? BASSFlag.BASS_SAMPLE_LOOP : BASSFlag.BASS_DEFAULT, BASSFlag.BASS_SAMPLE_LOOP);

        return _eventBus.PublishAsync(new AuxLoopChangedEvent(index, loop), ct);
    }

    public Task SetAuxVolumeAsync(int index, float gainLinear, CancellationToken ct = default)
    {
        if (!_auxStreams.TryGetValue(index, out var st) || st.StreamHandle == 0)
            return Task.CompletedTask;

        st.Volume = Math.Clamp(gainLinear, 0f, 1f);
        ApplyAuxRouting(st);
        return _eventBus.PublishAsync(new AuxVolumeChangedEvent(index, st.Volume), ct);
    }

    public Task SetAuxRouteAsync(int index, bool on, bool pfl, CancellationToken ct = default)
    {
        if (!_auxStreams.TryGetValue(index, out var st) || st.StreamHandle == 0)
            return Task.CompletedTask;

        lock (_lock)
        {
            // ON ⇄ PFL can change which output path is valid: under WASAPI/ASIO an ON deck lives in
            // the master mixer while a PFL deck plays direct to the PFL card. When that flips, the
            // stream type (decode vs playback) must change, so rebuild it in place preserving state.
            bool wasMixer = AuxUsesMixer(st);
            st.On  = on;
            st.Pfl = pfl;
            if (AuxUsesMixer(st) != wasMixer)
                RebuildAuxDeck(st, index);
            ApplyAuxRouting(st);
        }
        return _eventBus.PublishAsync(new AuxRouteChangedEvent(index, on, pfl), ct);
    }

    public int GetAuxPositionMs(int index)
    {
        if (!_auxStreams.TryGetValue(index, out var st) || st.StreamHandle == 0)
            return 0;
        long bytes = AuxGetPositionBytes(st);
        return bytes > 0 ? (int)(Bass.BASS_ChannelBytes2Seconds(st.StreamHandle, bytes) * 1000) : 0;
    }

    // ── AUX routing helpers ────────────────────────────────────────────────────

    // A deck renders through the master mixer only when it is ON under WASAPI/ASIO. A PFL deck always
    // plays direct to the PFL device (which stays a real BASS device even in those modes), so cueing
    // never leaks on-air. Under DirectSound nothing goes through the mixer (device switching suffices).
    // A deck renders through the master mixer only when it is ON, under WASAPI/ASIO, AND has no
    // dedicated output card. A deck with its own card plays direct to that card instead (device path),
    // which is why an assigned AUX card works on-air in every output mode.
    private bool AuxUsesMixer(AuxState st) => _auxThroughMixer && !st.Pfl && st.DeviceIndex is null;

    // The output device for a device-path deck: PFL card when cueing, else its own assigned card, else
    // the program (player) device.
    private int AuxDeviceFor(AuxState st) => st.Pfl
        ? (_routing!.PflDeviceIndex ?? _routing.PlayerDeviceIndex)
        : (st.DeviceIndex ?? _routing!.PlayerDeviceIndex);

    // Creates st.StreamHandle for the deck's current routing and marks st.InMixer. Caller holds _lock.
    private bool CreateAuxHandleLocked(AuxState st)
    {
        bool inMixer = AuxUsesMixer(st);
        int handle;
        if (inMixer)
        {
            handle = Bass.BASS_StreamCreateFile(
                st.FilePath, 0, 0, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT);
            if (handle != 0)
            {
                // CHAN_PAUSE: added silent until Play. CHAN_BUFFER: the mixer keeps this source's
                // rendered data so BASS_Mixer_ChannelGetLevel (per-deck VU) works without stealing audio.
                BassMix.BASS_Mixer_StreamAddChannel(_mixerHandle, handle,
                    BASSFlag.BASS_MIXER_CHAN_PAUSE | BASSFlag.BASS_MIXER_CHAN_BUFFER);
                if (st.Loop)
                    Bass.BASS_ChannelFlags(handle, BASSFlag.BASS_SAMPLE_LOOP, BASSFlag.BASS_SAMPLE_LOOP);
            }
        }
        else
        {
            int device = AuxDeviceFor(st);
            Bass.BASS_SetDevice(device);
            handle = Bass.BASS_StreamCreateFile(st.FilePath, 0, 0, BASSFlag.BASS_DEFAULT);
            if (handle != 0) Bass.BASS_ChannelSetDevice(handle, device);
        }

        st.StreamHandle = handle;
        st.InMixer      = inMixer && handle != 0;
        return handle != 0;
    }

    // Registers the deck's loop/END sync. Mixer decks loop the START..END region seamlessly with a
    // mixtime position sync (seek back on loop, pause on end); device decks get a natural-EOF backup
    // and rely on AuxMonitorTick to enforce the cue. Frees any previously pinned SYNCPROC first.
    private void RegisterAuxEndSyncLocked(AuxState st, int index)
    {
        if (st.EndSyncHandle.IsAllocated) st.EndSyncHandle.Free();
        int capturedIndex = index;
        AuxState stC = st;

        if (st.InMixer)
        {
            long len      = Bass.BASS_ChannelGetLength(st.StreamHandle, BASSMode.BASS_POS_BYTE);
            long endBytes = Bass.BASS_ChannelSeconds2Bytes(st.StreamHandle, st.EndMs / 1000.0);
            // When the END cue sits at the very end of the file, back off ~30 ms (frame-aligned) so the
            // sync is reliably reached before EOF instead of racing the end of the stream.
            if (len > 0 && endBytes >= len)
                endBytes = Bass.BASS_ChannelSeconds2Bytes(
                    st.StreamHandle, Math.Max(0, st.EndMs / 1000.0 - 0.03));

            SYNCPROC posProc = (h, c, d, u) =>
            {
                long startBytes = Bass.BASS_ChannelSeconds2Bytes(stC.StreamHandle, stC.StartMs / 1000.0);
                BassMix.BASS_Mixer_ChannelSetPosition(stC.StreamHandle, startBytes, BASSMode.BASS_POS_BYTE);
                if (!stC.Loop)
                {
                    BassMix.BASS_Mixer_ChannelPause(stC.StreamHandle);
                    _eventBridge.Post(new SyncMessage(SyncMessageType.AuxEnded, AuxIndex: capturedIndex));
                }
            };
            st.EndSyncHandle = GCHandle.Alloc(posProc, GCHandleType.Normal);
            BassMix.BASS_Mixer_ChannelSetSync(st.StreamHandle,
                BASSSync.BASS_SYNC_POS | BASSSync.BASS_SYNC_MIXTIME, endBytes, posProc, IntPtr.Zero);
        }
        else
        {
            SYNCPROC endProc = (h, c, d, u) =>
                _eventBridge.Post(new SyncMessage(SyncMessageType.AuxEnded, AuxIndex: capturedIndex));
            st.EndSyncHandle = GCHandle.Alloc(endProc, GCHandleType.Normal);
            Bass.BASS_ChannelSetSync(st.StreamHandle, BASSSync.BASS_SYNC_END, 0, endProc, IntPtr.Zero);
        }
    }

    // Rebuilds the deck's stream for a changed routing (ON⇄PFL under WASAPI/ASIO), preserving the
    // playhead and play/pause state. Caller holds _lock.
    private void RebuildAuxDeck(AuxState st, int index)
    {
        bool   wasPlaying = st.StreamHandle != 0 && AuxIsActive(st) == BASSActive.BASS_ACTIVE_PLAYING;
        double posSec     = st.StreamHandle != 0
            ? Bass.BASS_ChannelBytes2Seconds(st.StreamHandle, AuxGetPositionBytes(st))
            : 0;

        FreeAuxStream(st);

        if (!CreateAuxHandleLocked(st))
        {
            _log.LogWarning("AUX {Index}: stream rebuild after route change failed ({Err})",
                index, Bass.BASS_ErrorGetCode());
            return;
        }

        AuxSetPositionBytes(st, Bass.BASS_ChannelSeconds2Bytes(st.StreamHandle, posSec));
        RegisterAuxEndSyncLocked(st, index);
        if (wasPlaying) PlayAuxDeck(st);
    }

    // Routes a deck's effective volume; a device (non-mixer) deck also targets its PFL/program card.
    private void ApplyAuxRouting(AuxState st)
    {
        if (st.StreamHandle == 0) return;

        if (!st.InMixer)
            Bass.BASS_ChannelSetDevice(st.StreamHandle, AuxDeviceFor(st));

        float vol = (st.On || st.Pfl) ? st.Volume : 0f;
        Bass.BASS_ChannelSetAttribute(st.StreamHandle, BASSAttribute.BASS_ATTRIB_VOL, vol);
    }

    // ── Mixer-aware deck accessors (a deck is either a mixer source or a device stream) ─────────
    private void       PlayAuxDeck(AuxState st)  { if (st.InMixer) BassMix.BASS_Mixer_ChannelPlay(st.StreamHandle);  else Bass.BASS_ChannelPlay(st.StreamHandle, false); }
    private void       PauseAuxDeck(AuxState st) { if (st.InMixer) BassMix.BASS_Mixer_ChannelPause(st.StreamHandle); else Bass.BASS_ChannelPause(st.StreamHandle); }
    private BASSActive AuxIsActive(AuxState st)  => st.InMixer ? BassMix.BASS_Mixer_ChannelIsActive(st.StreamHandle) : Bass.BASS_ChannelIsActive(st.StreamHandle);
    private long       AuxGetPositionBytes(AuxState st) => st.InMixer
        ? BassMix.BASS_Mixer_ChannelGetPosition(st.StreamHandle, BASSMode.BASS_POS_BYTE)
        : Bass.BASS_ChannelGetPosition(st.StreamHandle, BASSMode.BASS_POS_BYTE);
    private void AuxSetPositionBytes(AuxState st, long bytes)
    {
        if (st.InMixer) BassMix.BASS_Mixer_ChannelSetPosition(st.StreamHandle, bytes, BASSMode.BASS_POS_BYTE);
        else            Bass.BASS_ChannelSetPosition(st.StreamHandle, bytes, BASSMode.BASS_POS_BYTE);
    }
    // Non-consuming: BASS_Mixer_ChannelGetLevel reads the mixer's rendered data, so (unlike
    // BASS_ChannelGetLevel on a decode source) it never steals audio from the output.
    private int AuxGetLevel(AuxState st) =>
        st.InMixer ? BassMix.BASS_Mixer_ChannelGetLevel(st.StreamHandle) : Bass.BASS_ChannelGetLevel(st.StreamHandle);

    // Tears down the deck's stream (detaches from the mixer or stops device playback) and frees the sync.
    private void FreeAuxStream(AuxState st)
    {
        st.StopFadeTimer?.Dispose();
        st.StopFadeTimer = null;
        if (st.StreamHandle != 0)
        {
            if (st.InMixer) BassMix.BASS_Mixer_ChannelRemove(st.StreamHandle);
            else            Bass.BASS_ChannelStop(st.StreamHandle);
            Bass.BASS_StreamFree(st.StreamHandle);
            st.StreamHandle = 0;
        }
        if (st.EndSyncHandle.IsAllocated) st.EndSyncHandle.Free();
        st.InMixer = false;
    }

    private void EjectAuxInternal(int index)
    {
        if (!_auxStreams.TryRemove(index, out var st)) return;
        lock (_lock) FreeAuxStream(st);
    }

    // Publishes position + level for every actively playing AUX deck (~10 Hz).
    private void AuxMonitorTick(object? state)
    {
        foreach (var (index, st) in _auxStreams)
        {
            int handle = st.StreamHandle;
            if (handle == 0) continue;
            if (AuxIsActive(st) != BASSActive.BASS_ACTIVE_PLAYING) continue;

            long posBytes = AuxGetPositionBytes(st);
            uint posMs    = posBytes > 0
                ? (uint)(Bass.BASS_ChannelBytes2Seconds(handle, posBytes) * 1000)
                : 0;

            // Enforce the END cue by polling — device decks only. Mixer decks do this precisely in a
            // mixtime sync (RegisterAuxEndSyncLocked); polling their decode position would fight it.
            if (!st.InMixer && st.EndMs > st.StartMs && posMs >= st.EndMs)
            {
                AuxSetPositionBytes(st, Bass.BASS_ChannelSeconds2Bytes(handle, st.StartMs / 1000.0));

                if (!st.Loop)
                {
                    PauseAuxDeck(st);
                    _ = _eventBus.PublishAsync(new AuxDeactivatedEvent(index));
                    continue;
                }
                posMs = st.StartMs;
            }

            _ = _eventBus.PublishAsync(new AuxPositionChangedEvent(index, posMs, st.DurationMs));

            int level = AuxGetLevel(st);
            if (level >= 0)
            {
                int peak = Math.Max(Un4seen.Bass.Utils.LowWord32(level),
                                    Un4seen.Bass.Utils.HighWord32(level));
                float linear = peak / 32768f;
                float db     = linear > 0.0001f ? 20f * (float)Math.Log10(linear) : -60f;
                _ = _eventBus.PublishAsync(new AuxLevelChangedEvent(index, db));
            }
        }
    }

    // ── Sweeper ──────────────────────────────────────────────────────────────

    public Task CancelScheduledSweepersAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            _sweeperTrigger?.Cancel();
        }
        return Task.CompletedTask;
    }

    public Task ScheduleSweeperAsync(
        long triggerAtMs, string sweeperFilePath, float duckDb = 6f, CancellationToken ct = default)
    {
        EnsureInitialized();

        if (!File.Exists(sweeperFilePath))
            throw new FileNotFoundException(
                $"Sweeper file not found: {sweeperFilePath}", sweeperFilePath);

        lock (_lock)
        {
            if (_playlistStream != 0)
            {
                // duckDb is an attenuation amount 0..12 → gain = 10^(-duckDb/20) (0 dB = no duck).
                float d = Math.Clamp(duckDb, 0f, 12f);
                _pendingSweeperDuckGain = (float)Math.Pow(10.0, -d / 20.0);
                long bytes = Bass.BASS_ChannelSeconds2Bytes(_playlistStream, triggerAtMs / 1000.0);
                _sweeperTrigger!.Schedule(_playlistStream, bytes, sweeperFilePath);
            }
        }
        return Task.CompletedTask;
    }

    // ── IAsyncDisposable ─────────────────────────────────────────────────────

    public async ValueTask DisposeAsync() => await ShutdownAsync();

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Called from EventBridge drain task after a one-shot cart reaches natural end.
    /// ConcurrentDictionary TryRemove is atomic — safe if StopCartAsync races.
    /// </summary>
    private void CleanupCart(string slotId)
    {
        if (!_cartwallStreams.TryRemove(slotId, out var state)) return;
        // Stream already stopped (SYNC_END fired) — just free resources
        FreeCartStream(state);
    }

    // Detaches a cart from the mixer (or stops device playback) and frees its stream + pinned sync.
    private void FreeCartStream(CartState state)
    {
        if (state.InMixer) BassMix.BASS_Mixer_ChannelRemove(state.StreamHandle);
        else               Bass.BASS_ChannelStop(state.StreamHandle);
        Bass.BASS_StreamFree(state.StreamHandle);
        if (state.SyncHandle.IsAllocated) state.SyncHandle.Free();
    }

    /// <summary>
    /// Removes the playlist stream from BASSmix and frees BASS resources.
    /// CuePointTimer is released FIRST to prevent callbacks firing after removal.
    /// Must be called under _lock.
    /// </summary>
    private void FreePlaylistStream()
    {
        if (_playlistStream == 0) return;

        _sweeperTrigger?.Release();                          // unpin sweeper GCHandles first
        _cuePointTimer.Release();                            // then cue point GCHandles
        ReleasePlayerLoopSyncLocked();                       // and the loop sync, while the handle is still valid
        RemoveEnvelopeDsp();
        FreeStreamMetaSync();
        BassMix.BASS_Mixer_ChannelRemove(_playlistStream);
        Bass.BASS_StreamFree(_playlistStream);
        _playlistStream = 0;
        _currentAssetId = null;
    }

    private void FreeStreamMetaSync()
    {
        if (_streamMetaGcHandle.IsAllocated) _streamMetaGcHandle.Free();
        _streamMetaProc = null;
    }

    /// <summary>
    /// Stops and frees a cart stream without publishing CartEndedEvent.
    /// Used by TriggerCartAsync (replace slot) and ShutdownAsync.
    /// </summary>
    private void StopCartInternal(string slotId)
    {
        if (!_cartwallStreams.TryRemove(slotId, out var state)) return;
        FreeCartStream(state);
    }

    private void RegisterStallCallback()
    {
        _stallProc = (handle, channel, data, user) =>
            _eventBridge.Post(new SyncMessage(SyncMessageType.BufferUnderrun));

        _stallGcHandle = GCHandle.Alloc(_stallProc, GCHandleType.Normal);

        Bass.BASS_ChannelSetSync(
            _mixerHandle,
            BASSSync.BASS_SYNC_STALL,
            0,
            _stallProc,
            IntPtr.Zero);
    }

    private void EnsureInitialized()
    {
        if (!_isInitialized)
            throw new AudioEngineException(
                "BassAudioEngine is not initialized. Call InitializeAsync first.");
    }

    /// <summary>
    /// Called by EventBridge when SweeperTrigger fires (drain task thread — BASS calls safe).
    /// Creates a BASS_STREAM_DECODE sweeper stream, adds it to the mixer, and registers
    /// a self-cleaning SYNC_END that frees the stream via a SweeperEnded EventBridge message.
    /// </summary>
    private void CreateSweeperStream(string filePath)
    {
        if (!File.Exists(filePath)) return;

        lock (_lock)
        {
            if (_mixerHandle == 0 || _routing is null) return;

            // With a dedicated sweeper card assigned, the sweeper plays direct to that DirectSound
            // device (like PFL — works in every output mode); otherwise it decodes into the master
            // mixer as before. Either way the playlist (in the mixer) is ducked underneath it.
            int? sweeperDev = _routing.SweeperDeviceIndex;
            bool onOwnCard  = sweeperDev is not null;
            int  stream;

            if (onOwnCard)
            {
                Bass.BASS_SetDevice(sweeperDev!.Value);
                stream = Bass.BASS_StreamCreateFile(filePath, 0, 0, BASSFlag.BASS_DEFAULT);
                if (stream == 0) return;
                Bass.BASS_ChannelSetDevice(stream, sweeperDev.Value);
                Bass.BASS_ChannelSetAttribute(stream, BASSAttribute.BASS_ATTRIB_VOL, 1.0f);
                Bass.BASS_ChannelPlay(stream, false);
            }
            else
            {
                Bass.BASS_SetDevice(_routing.PlayerDeviceIndex);
                stream = Bass.BASS_StreamCreateFile(filePath, 0, 0, BASSFlag.BASS_STREAM_DECODE);
                if (stream == 0) return;
                BassMix.BASS_Mixer_StreamAddChannel(_mixerHandle, stream, BASSFlag.BASS_DEFAULT);
                // Sweeper plays at unity; the track is ducked underneath it instead.
                Bass.BASS_ChannelSetAttribute(stream, BASSAttribute.BASS_ATTRIB_VOL, 1.0f);
                BassMix.BASS_Mixer_ChannelPlay(stream);
            }

            // Duck the playlist track for the duration of the sweeper.
            BeginSweeperDuckRamp(_pendingSweeperDuckGain, SweeperDuckAttackMs);

            // Capture handles for the cleanup closure
            int        capturedStream = stream;
            GCHandle[] gcRef          = new GCHandle[1];

            SYNCPROC endProc = (h, c, d, u) =>
                _eventBridge.Post(new SyncMessage(
                    Type:        SyncMessageType.SweeperEnded,
                    PostProcess: () =>
                    {
                        Bass.BASS_StreamFree(capturedStream);
                        if (gcRef[0].IsAllocated) gcRef[0].Free();
                        // Restore the track volume now that the sweeper has ended.
                        BeginSweeperDuckRamp(1.0f, SweeperDuckReleaseMs);
                    }));

            gcRef[0] = GCHandle.Alloc(endProc, GCHandleType.Normal);

            if (onOwnCard)
                Bass.BASS_ChannelSetSync(
                    stream, BASSSync.BASS_SYNC_END | BASSSync.BASS_SYNC_ONETIME, 0, endProc, IntPtr.Zero);
            else
                BassMix.BASS_Mixer_ChannelSetSync(
                    stream, BASSSync.BASS_SYNC_END | BASSSync.BASS_SYNC_ONETIME, 0, endProc, IntPtr.Zero);
        }
    }

    private float DuckingTargetGain()
    {
        decimal levelDb = _activeSettings?.DuckingLevelDb ?? -6m;
        return (float)Math.Pow(10.0, (double)levelDb / 20.0);
    }

    /// <summary>
    /// Starts a smooth volume ramp on the playlist channel from the current
    /// _duckingGain to targetGain over durationMs milliseconds (~16 ms steps).
    /// A concurrent ramp is cancelled and replaced from the current position.
    /// </summary>
    private void BeginDuckingRamp(float targetGain, uint durationMs)
    {
        lock (_duckLock)
        {
            _duckTimer?.Dispose();
            _duckTimer = null;

            if (durationMs == 0)
            {
                _duckingGain = targetGain;
                ApplyCombinedDuckGain();
                return;
            }

            float startGain = _duckingGain;
            const int stepMs = 16;
            int   steps = Math.Max(1, (int)(durationMs / stepMs));
            float step  = (targetGain - startGain) / steps;
            int   tick  = 0;

            _duckTimer = new Timer(_ =>
            {
                lock (_duckLock)
                {
                    tick++;
                    bool done    = tick >= steps;
                    _duckingGain = done ? targetGain : Math.Clamp(startGain + step * tick, 0f, 1f);
                    ApplyCombinedDuckGain();
                    if (done)
                    {
                        _duckTimer?.Dispose();
                        _duckTimer = null;
                    }
                }
            }, null, stepMs, stepMs);
        }
    }

    /// <summary>
    /// Ramps the sweeper-ducking gain (_sweeperDuckGain) to targetGain over durationMs,
    /// independently of the mic ducker. Shares _duckLock so writes to the channel volume
    /// stay serialised with the mic ramp.
    /// </summary>
    private void BeginSweeperDuckRamp(float targetGain, uint durationMs)
    {
        lock (_duckLock)
        {
            _sweeperDuckTimer?.Dispose();
            _sweeperDuckTimer = null;

            if (durationMs == 0)
            {
                _sweeperDuckGain = targetGain;
                ApplyCombinedDuckGain();
                return;
            }

            float startGain = _sweeperDuckGain;
            const int stepMs = 16;
            int   steps = Math.Max(1, (int)(durationMs / stepMs));
            float step  = (targetGain - startGain) / steps;
            int   tick  = 0;

            _sweeperDuckTimer = new Timer(_ =>
            {
                lock (_duckLock)
                {
                    tick++;
                    bool done        = tick >= steps;
                    _sweeperDuckGain = done ? targetGain : Math.Clamp(startGain + step * tick, 0f, 1f);
                    ApplyCombinedDuckGain();
                    if (done)
                    {
                        _sweeperDuckTimer?.Dispose();
                        _sweeperDuckTimer = null;
                    }
                }
            }, null, stepMs, stepMs);
        }
    }

    /// <summary>
    /// Applies the combined ducking (_duckingGain * _sweeperDuckGain) to the active
    /// playlist stream. Safe to call from the timer callbacks — reads _playlistStream
    /// without lock (int reads are atomic; BASS handles a stale handle gracefully).
    /// </summary>
    private void ApplyCombinedDuckGain()
    {
        int stream = _playlistStream;
        if (stream != 0)
            Bass.BASS_ChannelSetAttribute(
                stream, BASSAttribute.BASS_ATTRIB_VOL, _duckingGain * _sweeperDuckGain);
    }

    // ── Streaming encoders ────────────────────────────────────────────────────

    public Task StartEncoderAsync(EncoderProfile profile, string? password, CancellationToken ct = default)
    {
        if (!_isInitialized || _mixerHandle == 0)
            throw new AudioEngineException("Audio engine is not initialised — cannot start an encoder.");

        // Restarting an existing profile: tear the old session down first so its retry timer and
        // DSP tap cannot outlive it and keep casting under the previous settings.
        if (_encoderSessions.TryRemove(profile.ProfileId, out var previous))
            previous.Dispose();

        var session = new EncoderSession(
            profile, password, _mixerHandle, _log,
            status => _ = PublishEncoderStatusAsync(status));

        _encoderSessions[profile.ProfileId] = session;
        session.Start();
        return Task.CompletedTask;
    }

    public Task StopEncoderAsync(string profileId, CancellationToken ct = default)
    {
        if (_encoderSessions.TryGetValue(profileId, out var session))
            session.Stop();
        return Task.CompletedTask;
    }

    public EncoderStatus? GetEncoderStatus(string profileId) =>
        _encoderSessions.TryGetValue(profileId, out var session) ? session.Status : null;

    public IReadOnlyList<EncoderStatus> GetAllEncoderStatuses() =>
        _encoderSessions.Values.Select(s => s.Status).ToList();

    public Task SetEncoderTitleAsync(string nowPlayingTitle, CancellationToken ct = default)
    {
        foreach (var session in _encoderSessions.Values)
            session.ApplyNowPlaying(nowPlayingTitle);
        return Task.CompletedTask;
    }

    public bool IsEncoderFormatAvailable(EncoderFormat format) =>
        BassEncoderFormats.RequiredLibrary(format) is { } lib
        && BassLibInitializer.HasLibrary("bassenc.dll")
        && BassLibInitializer.HasLibrary(lib);

    /// <summary>
    /// Fire-and-forget bridge from the session's synchronous state machine (which can be running on
    /// a native callback thread) to the async event bus. Failures are logged rather than swallowed —
    /// a status update that never reaches the UI must not disappear without trace.
    /// </summary>
    private async Task PublishEncoderStatusAsync(EncoderStatus status)
    {
        try
        {
            await _eventBus.PublishAsync(new EncoderStatusChangedEvent(status)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Encoder '{Name}': status publish failed — {Err}", status.ProfileName, ex.Message);
        }
    }

    // ── Recording ─────────────────────────────────────────────────────────────

    public Task<RecordingStatus> StartRecordingAsync(RecordingRequest request, CancellationToken ct = default)
    {
        if (!_isInitialized || _mixerHandle == 0)
            throw new AudioEngineException("Audio engine is not initialised — cannot start recording.");

        lock (_recordingLock)
        {
            // A running recording is never replaced implicitly: silently starting a second file
            // would leave the operator believing the show is in one place when it is in two.
            if (_recordingSession is { IsActive: true } active)
            {
                _log.LogInformation("Recording already in progress — start ignored.");
                return Task.FromResult(active.Status);
            }

            _recordingSession?.Dispose();
            _recordingSession = new RecordingSession(
                request, _mixerHandle, _log,
                status => _ = PublishRecordingStatusAsync(status));

            return Task.FromResult(_recordingSession.Start());
        }
    }

    public Task<RecordingStatus> StopRecordingAsync(CancellationToken ct = default)
    {
        lock (_recordingLock)
        {
            return Task.FromResult(
                _recordingSession?.Stop() ?? new RecordingStatus(RecordingState.Stopped));
        }
    }

    public RecordingStatus GetRecordingStatus()
    {
        lock (_recordingLock)
        {
            return _recordingSession?.Status ?? new RecordingStatus(RecordingState.Stopped);
        }
    }

    private async Task PublishRecordingStatusAsync(RecordingStatus status)
    {
        try
        {
            await _eventBus.PublishAsync(new RecordingStatusChangedEvent(status)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Recording: status publish failed — {Err}", ex.Message);
        }
    }

    // ── Microphone ────────────────────────────────────────────────────────────

    public async Task StartMicAsync(CancellationToken ct = default)
    {
        _log.LogInformation("StartMicAsync: entry — isMicActive={Active}, isRecording={Rec}, DeviceInputId={DevId}",
            _isMicActive, _isRecording, _activeSettings?.DeviceInputId);

        EnsureInitialized();

        if (_isMicActive)
            throw new InvalidOperationException("Microphone is already active.");

        if (_isRecording)
            throw new InvalidOperationException(
                "Cannot activate microphone while voice track recording is in progress.");

        int recIndex = 0;
        if (_activeSettings?.DeviceInputId is not null)
        {
            var devices = await _deviceRepository.GetByStudioAsync(_activeSettings.StudioId, ct);
            var inputDev = devices.FirstOrDefault(d => d.DeviceId == _activeSettings.DeviceInputId);
            if (inputDev is not null)
            {
                var recInfos = Bass.BASS_RecordGetDeviceInfos();
                if (recInfos is not null)
                {
                    for (int i = 0; i < recInfos.Length; i++)
                    {
                        var info = recInfos[i];
                        if (info is null) continue;
                        var sysId = string.IsNullOrEmpty(info.id) ? $"bass-rec-{i}" : info.id;
                        if (string.Equals(sysId, inputDev.SystemDeviceId,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            recIndex = i;
                            break;
                        }
                    }
                }
                _log.LogInformation("StartMicAsync: input device resolved — SystemDeviceId='{SysId}', recIndex={Idx}",
                    inputDev.SystemDeviceId, recIndex);
            }
            else
            {
                _log.LogWarning("StartMicAsync: DeviceInputId={DevId} not found in DB — falling back to recIndex=0",
                    _activeSettings.DeviceInputId);
            }
        }
        else
        {
            _log.LogInformation("StartMicAsync: no DeviceInputId configured — using default recIndex=0");
        }

        int sampleRate = _activeSettings is not null ? (int)_activeSettings.SampleRate : 44100;
        const int channels = 1;

        Bass.BASS_RecordInit(recIndex);

        // Cross-check: if the recording device shares the same USB hardware as the player output,
        // the USB chip may route the playback signal back into the ADC capture (hardware loopback),
        // causing the mic to always capture the music — ducking triggers immediately, echo on air.
        var recInfo    = Bass.BASS_RecordGetDeviceInfo(recIndex);
        var playerInfo = _routing is not null ? Bass.BASS_GetDeviceInfo(_routing.PlayerDeviceIndex) : null;
        _log.LogInformation("StartMicAsync: recDev[{Idx}] name='{Name}', id='{Id}'",
            recIndex, recInfo?.name, recInfo?.id);
        if (recInfo?.id is not null && playerInfo?.id is not null
            && ExtractUsbHardwareId(recInfo.id) == ExtractUsbHardwareId(playerInfo.id))
        {
            _log.LogWarning(
                "StartMicAsync: SAME USB HARDWARE for playback ('{PName}') and recording ('{RName}'). " +
                "The USB chip may route its DAC output into the ADC capture (hardware monitor loopback), " +
                "causing the mic to record the music — ducking will trigger immediately and there will be echo on air. " +
                "Fix: use a different output device, disable hardware monitoring in Windows Sound settings, " +
                "or use WASAPI exclusive mode for recording.",
                playerInfo.name, recInfo.name);
        }

        // BASS_SAMPLE_FLOAT: keep the mic path as float32 end-to-end so MicLevelDspCallback
        // receives float samples (not int16 pairs misinterpreted as float → garbage levels).
        int pushStream = Bass.BASS_StreamCreatePush(
            sampleRate, channels, BASSFlag.BASS_STREAM_DECODE | BASSFlag.BASS_SAMPLE_FLOAT, IntPtr.Zero);

        if (pushStream == 0)
        {
            Bass.BASS_RecordFree();
            throw new AudioEngineException(
                $"BASS_StreamCreatePush (mic) failed: {Bass.BASS_ErrorGetCode()}");
        }

        lock (_lock)
        {
            BassMix.BASS_Mixer_StreamAddChannel(_mixerHandle, pushStream, BASSFlag.BASS_DEFAULT);
        }

        _micPushStream    = pushStream;
        _micSampleRate    = sampleRate;
        _micRecordCallbackCount = 0;
        _micDspCallbackCount    = 0;
        _micLastBufferLogTick   = 0;
        _micLastClipLogTick     = 0;
        _micClipCount           = 0;
        _micWindowPeak          = 0f;
        _micRecordProc    = MicRecordCallback;
        _micRecordProcGch = GCHandle.Alloc(_micRecordProc);

        // Register sidechain DSP at priority 20000 (highest) — runs before any FX.
        // Measures raw peak only; it drives the VU meter, never the ducking.
        _log.LogInformation(
            "StartMicAsync: ducking config — DuckingLevelDb={LvlDb}, DuckingAttackMs={DAtkMs}, DuckingReleaseMs={DRelMs}",
            _activeSettings?.DuckingLevelDb, _activeSettings?.DuckingAttackMs, _activeSettings?.DuckingReleaseMs);

        _micLevelDsp    = MicLevelDspCallback;
        _micLevelDspGch = GCHandle.Alloc(_micLevelDsp);
        _micLevelDspHandle = Bass.BASS_ChannelSetDSP(
            pushStream, _micLevelDsp, IntPtr.Zero, 20000);

        if (_micLevelDspHandle == 0)
            _log.LogError("StartMicAsync: BASS_ChannelSetDSP returned 0 — level DSP NOT registered! BASS error={Err}",
                Bass.BASS_ErrorGetCode());
        else
            _log.LogInformation("StartMicAsync: level DSP registered, handle={Handle}", _micLevelDspHandle);

        _isMicActive = true;

        // Apply all configured FX/VST effects to the new push stream
        _log.LogInformation("StartMicAsync: applying config to stream {Stream} — {FxCount} FX, {VstCount} VST",
            pushStream, _micFxConfig.Count, _micVstConfig.Count);
        foreach (var slot in _micFxConfig)
            ApplyFxSlotToStream(pushStream, slot);
        foreach (var slot in _micVstConfig)
            ApplyVstSlotToStream(pushStream, slot);

        // BASS_SAMPLE_FLOAT: record in float32 so RecordCallback delivers float data
        // that can be pushed directly into the float push stream without format conversion.
        _micRecordHandle = Bass.BASS_RecordStart(
            sampleRate, channels, BASSFlag.BASS_SAMPLE_FLOAT, _micRecordProc, IntPtr.Zero);

        if (_micRecordHandle == 0)
        {
            var errorCode = Bass.BASS_ErrorGetCode();
            _isMicActive = false;
            if (_micLevelDspHandle != 0)
            {
                Bass.BASS_ChannelRemoveDSP(pushStream, _micLevelDspHandle);
                _micLevelDspHandle = 0;
            }
            if (_micLevelDspGch.IsAllocated) _micLevelDspGch.Free();
            _micLevelDsp = null;
            lock (_lock)
            {
                BassMix.BASS_Mixer_ChannelRemove(pushStream);
                Bass.BASS_StreamFree(pushStream);
            }
            _micPushStream = 0;
            _micRecordProcGch.Free();
            _micRecordProc = null;
            Bass.BASS_RecordFree();
            throw new AudioEngineException(
                $"BASS_RecordStart (mic) failed: {errorCode}");
        }

        // Diagnostic: log the actual negotiated recording format vs what we requested.
        // If the record channel's rate/chans differ from the request, BASS/Windows is
        // resampling under the hood — a source of artefacts and, with two independent USB
        // clocks, of slow buffer drift that distorts only after some time.
        var recFmt  = Bass.BASS_ChannelGetInfo(_micRecordHandle);
        var recInfo2 = Bass.BASS_RecordGetInfo();
        _log.LogInformation(
            "StartMicAsync: mic format — requested {ReqRate} Hz / {ReqCh} ch; " +
            "record channel {ActRate} Hz / {ActCh} ch; device current rate {DevRate} Hz",
            sampleRate, channels,
            recFmt?.freq, recFmt?.chans,
            recInfo2 != null ? recInfo2.freq : 0);

        // The mic button itself is the trigger: duck the music now, at the configured
        // level and attack. No voice detection, so no reaction delay.
        float targetGain = DuckingTargetGain();
        uint  attackMs   = _activeSettings?.DuckingAttackMs ?? 200;
        _log.LogDebug("StartMicAsync: ducking music to gain={G:F3} over {Ms} ms", targetGain, attackMs);
        BeginDuckingRamp(targetGain, attackMs);

        await _eventBus.PublishAsync(new MicActivatedEvent(), ct);
    }

    public async Task StopMicAsync(CancellationToken ct = default)
    {
        _log.LogInformation("StopMicAsync: entry — isMicActive={Active}", _isMicActive);

        if (!_isMicActive) return;

        // Mic off is the release trigger: ramp the music back to full volume.
        uint releaseMs = _activeSettings?.DuckingReleaseMs ?? 800;
        _log.LogDebug("StopMicAsync: releasing music over {Ms} ms", releaseMs);
        BeginDuckingRamp(1.0f, releaseMs);

        Interlocked.Exchange(ref _rawMicLevelDb, -60.0);

        _isMicActive = false;

        await Task.Delay(80, ct).ConfigureAwait(false);

        if (_micRecordHandle != 0)
        {
            Bass.BASS_ChannelStop(_micRecordHandle);
            Bass.BASS_StreamFree(_micRecordHandle);
            _micRecordHandle = 0;
        }

        Bass.BASS_RecordFree();

        if (_micRecordProcGch.IsAllocated) _micRecordProcGch.Free();
        _micRecordProc = null;

        if (_micPushStream != 0)
        {
            if (_micLevelDspHandle != 0)
            {
                Bass.BASS_ChannelRemoveDSP(_micPushStream, _micLevelDspHandle);
                _micLevelDspHandle = 0;
            }

            // Last chance to read the plugins' settings: BASS_StreamFree below takes them with it.
            CaptureMicVstStates();

            // Runtime handles are cleaned up by BASS_StreamFree; just clear the dicts
            _micFxHandles.Clear();
            _micVstHandles.Clear();
            _micVstEditorProcs.Clear();
            // _micFxConfig and _micVstConfig are preserved for next StartMicAsync

            lock (_lock)
            {
                BassMix.BASS_Mixer_ChannelRemove(_micPushStream);
                Bass.BASS_StreamFree(_micPushStream);
            }
            _micPushStream = 0;
        }

        if (_micLevelDspGch.IsAllocated) _micLevelDspGch.Free();
        _micLevelDsp = null;

        await _eventBus.PublishAsync(new MicDeactivatedEvent(), ct);
    }

    public double GetMicLevelDb()
    {
        if (!_isMicActive) return -60.0;
        return Interlocked.CompareExchange(ref _rawMicLevelDb, 0.0, 0.0);
    }

    public (double Left, double Right) GetPlaylistLevel(double offsetDb = 0.0)
    {
        if (_mixerHandle == 0) return (0, 0);
        // Via the backend: DirectSound reads the played mixer; WASAPI reads the device level.
        // Never call BASS_ChannelGetLevel directly on a decode mixer — it consumes audio.
        int raw = _outputBackend?.GetOutputLevel() ?? Bass.BASS_ChannelGetLevel(_mixerHandle);
        if (raw < 0) return (0, 0);
        return (ToVuScale(Un4seen.Bass.Utils.LowWord32(raw)),
                ToVuScale(Un4seen.Bass.Utils.HighWord32(raw)));

        double ToVuScale(int word)
        {
            double linear = word / 32768.0;
            double db     = linear > 0.0001 ? 20.0 * Math.Log10(linear) : -60.0;
            db += offsetDb;
            return Math.Clamp((db + 60.0) / 60.0 * 100.0, 0.0, 100.0);
        }
    }

    // ── Mic FX chain ──────────────────────────────────────────────────────────

    public Task<int> AddMicFxAsync(MicFxType fxType, CancellationToken ct = default)
    {
        int slotId = Interlocked.Increment(ref _micFxNextSlotId);
        var slot   = new MicFxSlot(slotId, fxType);
        _micFxConfig.Add(slot);

        // Apply immediately if mic is currently active
        if (_isMicActive && _micPushStream != 0)
            ApplyFxSlotToStream(_micPushStream, slot);

        return Task.FromResult(slotId);
    }

    public Task UpdateMicFxAsync(int slotId, IReadOnlyDictionary<string, float> parameters, CancellationToken ct = default)
    {
        var slot = _micFxConfig.Find(s => s.SlotId == slotId)
            ?? throw new KeyNotFoundException($"No mic FX slot with id {slotId}.");

        // Unknown keys dropped, values clamped — nothing reaches BASS unchecked.
        var clean = MicFxParams.Sanitize(slot.FxType, parameters);
        slot.Parameters.Clear();
        foreach (var (key, value) in clean)
            slot.Parameters[key] = value;

        // Live only while the mic is on; otherwise the values wait for the next StartMicAsync,
        // which applies the whole chain from config anyway.
        if (_micFxHandles.TryGetValue(slotId, out int fxHandle))
        {
            ApplyMicFxParameters(fxHandle, slot);
            _log.LogInformation("UpdateMicFxAsync: slot {SlotId} ({FxType}) applied live: {Params}",
                slotId, slot.FxType, string.Join(", ", clean.Select(p => $"{p.Key}={p.Value}")));
        }
        else
        {
            _log.LogInformation("UpdateMicFxAsync: slot {SlotId} ({FxType}) stored — mic inactive, " +
                "values apply when the mic is switched on", slotId, slot.FxType);
        }

        return Task.CompletedTask;
    }

    private void ApplyFxSlotToStream(int stream, MicFxSlot slot)
    {
        var bassType = slot.FxType switch
        {
            MicFxType.Compressor => BASSFXType.BASS_FX_BFX_COMPRESSOR2,
            MicFxType.PeakEq     => BASSFXType.BASS_FX_BFX_PEAKEQ,
            MicFxType.VolumeGain => BASSFXType.BASS_FX_BFX_VOLUME,
            MicFxType.FreeVerb   => BASSFXType.BASS_FX_BFX_FREEVERB,
            _                    => throw new ArgumentException($"Unknown FX type: {slot.FxType}")
        };

        int fxHandle = Bass.BASS_ChannelSetFX(stream, bassType, 0);
        if (fxHandle == 0)
        {
            _log.LogWarning("ApplyFxSlotToStream: BASS_ChannelSetFX returned 0 for {FxType} on stream {Stream}. BASS error={Err}. bass_fx.dll missing?",
                slot.FxType, stream, Bass.BASS_ErrorGetCode());
            return;
        }

        ApplyMicFxParameters(fxHandle, slot);
        _micFxHandles[slot.SlotId] = fxHandle;
    }

    private static void ApplyMicFxParameters(int fxHandle, MicFxSlot slot)
    {
        float P(string key) => slot.Parameters.TryGetValue(key, out float v) ? v : 0f;

        switch (slot.FxType)
        {
            case MicFxType.Compressor:
                Bass.BASS_FXSetParameters(fxHandle, new BASS_BFX_COMPRESSOR2
                {
                    fGain      = P("gain"),
                    fThreshold = P("threshold"),
                    fRatio     = P("ratio"),
                    fAttack    = P("attack"),
                    fRelease   = P("release"),
                    lChannel   = BASSFXChan.BASS_BFX_CHANALL
                });
                break;
            case MicFxType.PeakEq:
                Bass.BASS_FXSetParameters(fxHandle, new BASS_BFX_PEAKEQ
                {
                    lBand      = 0,
                    fBandwidth = P("bandwidth"),
                    fCenter    = P("center"),
                    fGain      = P("gain"),
                    lChannel   = BASSFXChan.BASS_BFX_CHANALL
                });
                break;
            case MicFxType.VolumeGain:
                Bass.BASS_FXSetParameters(fxHandle, new BASS_BFX_VOLUME
                {
                    lChannel = BASSFXChan.BASS_BFX_CHANALL,
                    fVolume  = P("volume")
                });
                break;
            case MicFxType.FreeVerb:
                Bass.BASS_FXSetParameters(fxHandle, new BASS_BFX_FREEVERB
                {
                    fDryMix   = P("drymix"),
                    fWetMix   = P("wetmix"),
                    fRoomSize = P("roomsize"),
                    fDamp     = P("damp"),
                    fWidth    = P("width"),
                    lChannel  = BASSFXChan.BASS_BFX_CHANALL
                });
                break;
        }
    }

    public Task RemoveMicFxAsync(int slotId, CancellationToken ct = default)
    {
        var slot = _micFxConfig.Find(s => s.SlotId == slotId)
            ?? throw new KeyNotFoundException($"No mic FX slot with id {slotId}.");

        if (_micFxHandles.TryGetValue(slotId, out int fxHandle) && _micPushStream != 0)
        {
            Bass.BASS_ChannelRemoveFX(_micPushStream, fxHandle);
            _micFxHandles.Remove(slotId);
        }

        _micFxConfig.Remove(slot);
        return Task.CompletedTask;
    }

    public IReadOnlyList<MicFxSlot> GetMicFxList() => _micFxConfig.AsReadOnly();

    // ── Mic VST chain ──────────────────────────────────────────────────────────

    public Task<int> AddMicVstAsync(string dllPath, CancellationToken ct = default)
    {
        _log.LogInformation("AddMicVstAsync: dllPath='{Dll}', micActive={Active}, pushStream={Stream}",
            dllPath, _isMicActive, _micPushStream);

        if (!File.Exists(dllPath))
        {
            _log.LogWarning("AddMicVstAsync: file does not exist: '{Dll}'", dllPath);
            throw new FileNotFoundException("VST DLL not found.", dllPath);
        }

        // Read plugin name without loading into stream (needs live stream for that)
        string pluginName = Path.GetFileNameWithoutExtension(dllPath);

        int slotId = Interlocked.Increment(ref _micVstNextSlotId);
        var slot   = new MicVstSlot(slotId, dllPath, pluginName);
        _micVstConfig.Add(slot);
        _log.LogInformation("AddMicVstAsync: created slot {SlotId} '{Name}' (config now has {Count} VST slots)",
            slotId, pluginName, _micVstConfig.Count);

        // Apply immediately if mic is currently active
        if (_isMicActive && _micPushStream != 0)
            ApplyVstSlotToStream(_micPushStream, slot);
        else
            _log.LogInformation("AddMicVstAsync: mic inactive — slot {SlotId} stored as config only, no live handle yet", slotId);

        return Task.FromResult(slotId);
    }

    private void ApplyVstSlotToStream(int stream, MicVstSlot slot)
    {
        _log.LogInformation("ApplyVstSlotToStream: loading VST slot {SlotId} '{Dll}' onto stream {Stream}",
            slot.SlotId, slot.DllPath, stream);

        int vstHandle = BassVst.BASS_VST_ChannelSetDSP(
            stream, slot.DllPath, BASSVSTDsp.BASS_VST_DEFAULT, 0);
        if (vstHandle == 0)
        {
            var err = Bass.BASS_ErrorGetCode();
            _log.LogError("ApplyVstSlotToStream: BASS_VST_ChannelSetDSP returned 0 for '{Dll}'. BASS error={Err}. " +
                "bass_vst.dll missing, DLL not a VST2 plugin, or bitness mismatch.", slot.DllPath, err);
            return; // bass_vst.dll not available or DLL error; plugin silently inactive
        }

        // Update plugin name + capture editor capabilities from VST metadata
        var info = new BASS_VST_INFO();
        bool gotInfo = BassVst.BASS_VST_GetInfo(vstHandle, info);
        if (!string.IsNullOrEmpty(info.effectName))
            slot.PluginName = info.effectName;

        _log.LogInformation(
            "ApplyVstSlotToStream: VST loaded. handle={Handle}, gotInfo={GotInfo}, name='{Name}', " +
            "hasEditor={HasEditor}, editorW={W}, editorH={H}, channels={Ch}, uniqueId={Uid}",
            vstHandle, gotInfo, info.effectName, info.hasEditor, info.editorWidth, info.editorHeight,
            info.chansOut, info.uniqueID);

        _micVstHandles[slot.SlotId] = vstHandle;

        RestoreVstState(vstHandle, slot);
    }

    public Task RemoveMicVstAsync(int slotId, CancellationToken ct = default)
    {
        var slot = _micVstConfig.Find(s => s.SlotId == slotId)
            ?? throw new KeyNotFoundException($"No mic VST slot with id {slotId}.");

        if (_micVstHandles.TryGetValue(slotId, out int vstHandle) && _micPushStream != 0)
        {
            BassVst.BASS_VST_ChannelRemoveDSP(_micPushStream, vstHandle);
            _micVstHandles.Remove(slotId);
            _micVstEditorProcs.Remove(slotId);
        }

        _micVstConfig.Remove(slot);
        return Task.CompletedTask;
    }

    public Task<(int Width, int Height)> OpenMicVstEditorAsync(int slotId, IntPtr parentWindow, CancellationToken ct = default)
    {
        _log.LogInformation("OpenMicVstEditorAsync: slotId={SlotId}, parentHwnd={Hwnd}, liveHandles=[{Handles}]",
            slotId, parentWindow, string.Join(",", _micVstHandles.Keys));

        if (!_micVstHandles.TryGetValue(slotId, out int vstHandle))
        {
            _log.LogWarning("OpenMicVstEditorAsync: slot {SlotId} has no live handle (mic inactive or VST failed to load)", slotId);
            throw new InvalidOperationException(
                $"VST slot {slotId} has no live handle — microphone must be active to open the editor.");
        }

        var info = new BASS_VST_INFO();
        BassVst.BASS_VST_GetInfo(vstHandle, info);
        _log.LogInformation("OpenMicVstEditorAsync: VST '{Name}' hasEditor={HasEditor} editorW={W} editorH={H}",
            info.effectName, info.hasEditor, info.editorWidth, info.editorHeight);

        if (!info.hasEditor)
        {
            _log.LogWarning("OpenMicVstEditorAsync: plugin '{Name}' reports hasEditor=false — it has no embeddable GUI", info.effectName);
            throw new InvalidOperationException($"Plugin '{info.effectName}' has no editor GUI.");
        }

        bool embedded = BassVst.BASS_VST_EmbedEditor(vstHandle, parentWindow);
        var err = Bass.BASS_ErrorGetCode();
        _log.LogInformation("OpenMicVstEditorAsync: BASS_VST_EmbedEditor(handle={Handle}, hwnd={Hwnd}) returned {Result}, BASS error={Err}",
            vstHandle, parentWindow, embedded, err);

        if (!embedded)
            throw new InvalidOperationException($"BASS_VST_EmbedEditor failed for slot {slotId}: {err}.");

        // The plugin may resize its GUI at any time (Stereo Tool does it when switching views).
        // It only *asks* — nothing moves unless the host resizes the parent window, so without
        // this callback a resized editor ends up clipped or collapsed to nothing.
        var proc = new VSTPROC((_, action, param1, param2, _) =>
        {
            if (action == BASSVSTAction.BASS_VST_EDITOR_RESIZED)
            {
                _log.LogInformation("VST slot {SlotId}: editor requested resize to {W}x{H}", slotId, param1, param2);
                MicVstEditorResized?.Invoke(slotId, param1, param2);
            }
            return 0;
        });
        _micVstEditorProcs[slotId] = proc;

        if (!BassVst.BASS_VST_SetCallback(vstHandle, proc, IntPtr.Zero))
            _log.LogWarning("OpenMicVstEditorAsync: BASS_VST_SetCallback failed for slot {SlotId}: {Err} — " +
                "editor resize requests will be ignored", slotId, Bass.BASS_ErrorGetCode());

        // Re-read the size: plugins are allowed to report their editor rect only once the editor
        // window exists, so the value from before the embed can be 0 or stale.
        var opened = new BASS_VST_INFO();
        BassVst.BASS_VST_GetInfo(vstHandle, opened);
        int width  = opened.editorWidth  > 0 ? opened.editorWidth  : info.editorWidth;
        int height = opened.editorHeight > 0 ? opened.editorHeight : info.editorHeight;
        _log.LogInformation("OpenMicVstEditorAsync: editor size after embed = {W}x{H}", width, height);

        return Task.FromResult((width, height));
    }

    public event Action<int, int, int>? MicVstEditorResized;

    public Task CloseMicVstEditorAsync(int slotId, CancellationToken ct = default)
    {
        if (!_micVstHandles.TryGetValue(slotId, out int vstHandle))
        {
            // Mic stopped (or the slot was removed) while the editor window was open — the handle
            // is already gone together with the editor, so there is nothing left to unembed.
            _log.LogInformation("CloseMicVstEditorAsync: slot {SlotId} has no live handle — nothing to unembed", slotId);
            return Task.CompletedTask;
        }

        bool closed = BassVst.BASS_VST_EmbedEditor(vstHandle, IntPtr.Zero);
        _log.LogInformation("CloseMicVstEditorAsync: BASS_VST_EmbedEditor(handle={Handle}, NULL) returned {Result}, BASS error={Err}",
            vstHandle, closed, Bass.BASS_ErrorGetCode());

        // Drop the callback only after the editor is gone — bass_vst may still call back while
        // closing it, and an unregistered-then-collected delegate would take the process down.
        BassVst.BASS_VST_SetCallback(vstHandle, null!, IntPtr.Zero);
        _micVstEditorProcs.Remove(slotId);

        return Task.CompletedTask;
    }

    public IReadOnlyList<MicVstSlot> GetMicVstList() => _micVstConfig.AsReadOnly();

    /// <summary>
    /// Copies the settings out of every loaded plugin into its slot, so they survive the plugin
    /// being unloaded. Only works while the mic is on — that is when the plugins exist at all —
    /// so it has to run before the handles go away, not after.
    /// </summary>
    public void CaptureMicVstStates()
    {
        foreach (var slot in _micVstConfig)
        {
            if (!_micVstHandles.TryGetValue(slot.SlotId, out int vstHandle))
                continue;

            byte[]? chunk = BassVst.BASS_VST_GetChunk(vstHandle, false);
            if (chunk is { Length: > 0 })
            {
                slot.StateChunk = chunk;
                _log.LogInformation("CaptureMicVstStates: slot {SlotId} '{Name}' — {Bytes} B chunk",
                    slot.SlotId, slot.PluginName, chunk.Length);
                continue;
            }

            // No chunk support. The raw parameter list is NOT used instead — see MicVstSlot for
            // why. Such a plugin manages its own settings; RDM only remembers that it is in the
            // chain, which is what has to survive a restart.
            _log.LogInformation("CaptureMicVstStates: slot {SlotId} '{Name}' does not support chunk mode — " +
                "only the slot is persisted, the plugin keeps its own settings",
                slot.SlotId, slot.PluginName);
        }
    }

    private void RestoreVstState(int vstHandle, MicVstSlot slot)
    {
        if (slot.StateChunk is not { Length: > 0 } chunk) return;

        int used = BassVst.BASS_VST_SetChunk(vstHandle, false, chunk);
        _log.LogInformation("RestoreVstState: slot {SlotId} '{Name}' — restored {Bytes} B chunk, plugin consumed {Used}",
            slot.SlotId, slot.PluginName, chunk.Length, used);
    }

    // ── Mic record callback ────────────────────────────────────────────────────

    private bool MicRecordCallback(int handle, IntPtr buffer, int length, IntPtr user)
    {
        if (!_isMicActive || _micPushStream == 0 || length <= 0)
            return false;

        int count = System.Threading.Interlocked.Increment(ref _micRecordCallbackCount);
        if (count == 1 || count == 10 || count == 100)
            _log.LogDebug("MicRecordCallback: call #{Count}, length={Len} bytes, pushStream={Stream}",
                count, length, _micPushStream);

        // The return value is the number of bytes still queued in the push stream.
        // A steadily RISING value means the mic clock is faster than the output clock
        // (independent USB clocks drifting apart) and the buffer will eventually overflow
        // → dropped data → distortion that grows over time. -1 signals a put error.
        int queued = Bass.BASS_StreamPutData(_micPushStream, buffer, length);

        long nowTick = Environment.TickCount64;
        if (queued < 0 || nowTick - _micLastBufferLogTick >= 2000)
        {
            _micLastBufferLogTick = nowTick;
            if (queued < 0)
            {
                _log.LogWarning("MicRecordCallback: BASS_StreamPutData error, code={Err}",
                    Bass.BASS_ErrorGetCode());
            }
            else
            {
                // push stream is mono float32, so bytes → ms = bytes / (rate * 4) * 1000
                double queuedMs = _micSampleRate > 0
                    ? queued / (double)(_micSampleRate * sizeof(float)) * 1000.0
                    : 0.0;
                _log.LogDebug(
                    "MicRecordCallback: push-stream queued={Bytes} bytes (~{Ms:F0} ms) at call #{Count}",
                    queued, queuedMs, count);
            }
        }
        return true;
    }

    // ── Mic level DSP (sidechain: analysis only, original audio passes unmodified) ──

    private void MicLevelDspCallback(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        if (!_isMicActive || length <= 0) return;

        int sampleCount = length / sizeof(float);
        if (sampleCount <= 0) return;

        int dspCount = System.Threading.Interlocked.Increment(ref _micDspCallbackCount);
        if (dspCount == 1 || dspCount == 10 || dspCount == 100)
            _log.LogDebug("MicLevelDspCallback: call #{Count}, sampleCount={N}", dspCount, sampleCount);

        if (sampleCount > _micLevelBuffer.Length)
            Array.Resize(ref _micLevelBuffer, sampleCount + 128);

        // Copy original PCM into pre-allocated buffer (no heap allocation in hot path)
        System.Runtime.InteropServices.Marshal.Copy(buffer, _micLevelBuffer, 0, sampleCount);

        // Raw peak + clip detection → UI/API VU-meter and distortion diagnostics
        float rawPeak = 0f;
        int   clipped = 0;
        for (int i = 0; i < sampleCount; i++)
        {
            float v = MathF.Abs(_micLevelBuffer[i]);
            if (v > rawPeak) rawPeak = v;
            if (v >= 0.999f) clipped++;   // at/above full-scale → hard clipping at the DAC
        }
        double rawDb = rawPeak > 1e-6f ? 20.0 * Math.Log10(rawPeak) : -60.0;
        Interlocked.Exchange(ref _rawMicLevelDb, rawDb);

        // Distortion diagnostic: accumulate the clipped-sample count and the window peak,
        // then log at most every ~2 s when clipping occurred. Clipping that is present from
        // the moment the mic opens = input gain too hot (or mic + music summing past 0 dBFS
        // in the mixer). These fields are only touched on this single DSP thread.
        _micClipCount += clipped;
        if (rawPeak > _micWindowPeak) _micWindowPeak = rawPeak;
        long nowTick = Environment.TickCount64;
        if (nowTick - _micLastClipLogTick >= 2000)
        {
            if (_micClipCount > 0)
                _log.LogWarning(
                    "MicLevelDspCallback: {Clipped} clipped mic samples in last window, peak={PeakDb:F1} dBFS — input too hot / distortion likely",
                    _micClipCount,
                    _micWindowPeak > 1e-6f ? 20.0 * Math.Log10(_micWindowPeak) : -60.0);
            _micClipCount       = 0;
            _micWindowPeak      = 0f;
            _micLastClipLogTick = nowTick;
        }

        // Note: DSPPROC buffer is NOT modified — original audio flows to the mixer unchanged.
    }

    // ── Volume envelope DSP ───────────────────────────────────────────────────

    private void InstallEnvelopeDsp(int stream, IReadOnlyList<RDM.Core.Models.EnvelopePoint>? envelope)
    {
        RemoveEnvelopeDsp();
        if (envelope is null || envelope.Count == 0) return;

        _playlistEnvelope = envelope;
        _envelopeDsp      = EnvelopeDspCallback;
        _envelopeDspGch   = GCHandle.Alloc(_envelopeDsp);
        // Priority 10000: runs after any FX but before output; high enough to not conflict with mic DSP at 20000
        _envelopeDspHandle = Bass.BASS_ChannelSetDSP(stream, _envelopeDsp, IntPtr.Zero, 10000);
    }

    private void RemoveEnvelopeDsp()
    {
        if (_envelopeDspHandle != 0 && _playlistStream != 0)
            Bass.BASS_ChannelRemoveDSP(_playlistStream, _envelopeDspHandle);
        _envelopeDspHandle = 0;
        _playlistEnvelope  = null;
        if (_envelopeDspGch.IsAllocated) _envelopeDspGch.Free();
        _envelopeDsp = null;
    }

    private unsafe void EnvelopeDspCallback(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        var env = _playlistEnvelope;
        if (env is null || env.Count == 0) return;

        long posBytes = Bass.BASS_ChannelGetPosition(channel, BASSMode.BASS_POS_BYTE);
        double posSec = Bass.BASS_ChannelBytes2Seconds(channel, posBytes);
        float gain    = InterpolateEnvelope(env, posSec);

        if (Math.Abs(gain - 1f) < 0.001f) return; // avoid touching samples at unity gain

        float* samples = (float*)buffer;
        int    count   = length / sizeof(float);
        for (int i = 0; i < count; i++)
            samples[i] *= gain;
    }

    private static float InterpolateEnvelope(IReadOnlyList<RDM.Core.Models.EnvelopePoint> env, double timeSec)
    {
        if (env.Count == 0)  return 1f;
        if (timeSec <= env[0].TimeS) return (float)env[0].Volume;
        for (int i = 1; i < env.Count; i++)
        {
            if (timeSec <= env[i].TimeS)
            {
                double t = (timeSec - env[i - 1].TimeS) / (env[i].TimeS - env[i - 1].TimeS);
                return (float)(env[i - 1].Volume + t * (env[i].Volume - env[i - 1].Volume));
            }
        }
        return (float)env[env.Count - 1].Volume;
    }

    // Returns the USB hardware portion of a BASS device id string, stripping the interface number
    // (mi_00 vs mi_01) so we can detect playback and recording endpoints on the same USB chip.
    // Example: "usb#vid_08bb&pid_2902&mi_00#7&2937103..." → "usb#vid_08bb&pid_2902#7&2937103..."
    private static string? ExtractUsbHardwareId(string? id)
    {
        if (id is null) return null;
        // Drop the &mi_XX segment to compare only VID/PID and instance suffix
        return System.Text.RegularExpressions.Regex.Replace(id, @"&mi_\w+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    // ── Voice track recording ─────────────────────────────────────────────────

    public async Task StartVoiceTrackRecordingAsync(string outputWavPath, CancellationToken ct = default)
    {
        if (_isRecording)
            throw new InvalidOperationException("Recording already in progress.");

        // Resolve recording device: DeviceInputId (UUID) → SystemDeviceId → BASS rec index
        int recIndex = 0; // default recording device
        if (_activeSettings?.DeviceInputId is not null)
        {
            var devices = await _deviceRepository.GetByStudioAsync(_activeSettings.StudioId, ct);
            var inputDev = devices.FirstOrDefault(d => d.DeviceId == _activeSettings.DeviceInputId);
            if (inputDev is not null)
            {
                var recInfos = Bass.BASS_RecordGetDeviceInfos();
                if (recInfos is not null)
                {
                    for (int i = 0; i < recInfos.Length; i++)
                    {
                        var info = recInfos[i];
                        if (info is null) continue;
                        var sysId = string.IsNullOrEmpty(info.id) ? $"bass-rec-{i}" : info.id;
                        if (string.Equals(sysId, inputDev.SystemDeviceId, StringComparison.OrdinalIgnoreCase))
                        {
                            recIndex = i;
                            break;
                        }
                    }
                }
            }
        }

        Bass.BASS_RecordInit(recIndex);

        _recordSampleRate = _activeSettings is not null ? (int)_activeSettings.SampleRate : 44100;
        _recordChannels   = 1; // mono for voice tracks

        Directory.CreateDirectory(Path.GetDirectoryName(outputWavPath) ?? ".");
        _recordStream       = new FileStream(outputWavPath, FileMode.Create, FileAccess.Write, FileShare.None);
        _recordWriter       = new BinaryWriter(_recordStream);
        _recordDataStartPos = WriteWavHeader(_recordWriter, _recordSampleRate, _recordChannels);

        _recordProc   = RecordCallback;
        _recordProcGch = GCHandle.Alloc(_recordProc);

        _isRecording  = true;
        _recordHandle = Bass.BASS_RecordStart(_recordSampleRate, _recordChannels, BASSFlag.BASS_DEFAULT, _recordProc, IntPtr.Zero);

        if (_recordHandle == 0)
        {
            _isRecording = false;
            _recordProcGch.Free();
            _recordWriter.Dispose();
            _recordStream.Dispose();
            _recordWriter = null;
            _recordStream = null;
            Bass.BASS_RecordFree();
            throw new AudioEngineException($"BASS_RecordStart failed: {Bass.BASS_ErrorGetCode()}");
        }
    }

    public async Task<uint> StopVoiceTrackRecordingAsync(CancellationToken ct = default)
    {
        if (!_isRecording) return 0;

        _isRecording = false;

        // Give the callback one final chance to flush
        await Task.Delay(80, ct).ConfigureAwait(false);

        Bass.BASS_ChannelStop(_recordHandle);
        Bass.BASS_StreamFree(_recordHandle);
        _recordHandle = 0;
        Bass.BASS_RecordFree();

        if (_recordProcGch.IsAllocated) _recordProcGch.Free();

        uint durationMs = 0;
        if (_recordWriter is not null && _recordStream is not null)
        {
            long dataBytes = _recordStream.Position - _recordDataStartPos;
            durationMs     = dataBytes > 0
                ? (uint)(dataBytes * 1000.0 / (_recordSampleRate * _recordChannels * 2))
                : 0;
            PatchWavHeader(_recordStream, _recordWriter, _recordDataStartPos);
            _recordWriter.Dispose();
            _recordStream.Dispose();
            _recordWriter = null;
            _recordStream = null;
        }

        return durationMs;
    }

    public float GetVoiceTrackRecordingLevel()
    {
        if (!_isRecording || _recordHandle == 0) return 0f;
        int level = Bass.BASS_ChannelGetLevel(_recordHandle);
        // Low word = left/mono channel, range 0-32768
        return Math.Clamp((level & 0xFFFF) / 32768.0f, 0f, 1f);
    }

    private bool RecordCallback(int handle, IntPtr buffer, int length, IntPtr user)
    {
        if (!_isRecording || _recordWriter is null || length <= 0)
            return false;

        var bytes = new byte[length];
        Marshal.Copy(buffer, bytes, 0, length);
        _recordWriter.Write(bytes);
        return true;
    }

    // Writes a WAV header with zero sizes, returns the stream position of the data start.
    private static long WriteWavHeader(BinaryWriter w, int sampleRate, int channels)
    {
        int bitsPerSample = 16;
        int byteRate      = sampleRate * channels * bitsPerSample / 8;
        int blockAlign    = channels * bitsPerSample / 8;

        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(0); // total size − 8, patched later
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);           // fmt chunk size
        w.Write((short)1);     // PCM format
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)bitsPerSample);
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(0); // data chunk size, patched later
        w.Flush();
        return w.BaseStream.Position; // = 44
    }

    private static void PatchWavHeader(FileStream fs, BinaryWriter w, long dataStart)
    {
        w.Flush();
        long dataBytes = fs.Position - dataStart;
        // Patch data chunk size at offset 40
        fs.Seek(40, SeekOrigin.Begin);
        w.Write((int)dataBytes);
        // Patch RIFF total size at offset 4
        fs.Seek(4, SeekOrigin.Begin);
        w.Write((int)(dataBytes + 36));
        w.Flush();
        fs.Seek(0, SeekOrigin.End);
    }
}
