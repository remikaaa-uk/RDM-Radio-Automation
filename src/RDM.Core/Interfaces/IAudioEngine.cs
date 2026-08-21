using RDM.Core.Entities;
using RDM.Core.Models;
using RDM.Shared.Enums;

namespace RDM.Core.Interfaces;

public interface IAudioEngine
{
    bool IsInitialized { get; }

    Task InitializeAsync(AudioSettings settings, CancellationToken ct = default);
    Task ShutdownAsync(CancellationToken ct = default);

    /// Hot-swaps the active audio settings so live-read parameters (AUX ducking level,
    /// microphone ducking level/threshold/attack/release) take effect without restarting
    /// the engine. Parameters that require re-initialisation — sample rate, buffer size,
    /// bit depth, output mode and device routing — are ignored until the next
    /// <see cref="InitializeAsync"/>. No-op when the engine is not initialised.
    Task UpdateSettingsAsync(AudioSettings settings, CancellationToken ct = default);

    Task LoadTrackAsync(string assetId, string filePath, IReadOnlyList<AssetCuePoint> cuePoints, IReadOnlyList<EnvelopePoint>? envelope = null, CancellationToken ct = default);
    Task LoadInternetStreamAsync(string assetId, string streamUrl, CancellationToken ct = default);
    Task SeekPlaylistStreamAsync(uint positionMs, CancellationToken ct = default);
    Task PlayAsync(CancellationToken ct = default);
    Task PauseAsync(CancellationToken ct = default);
    /// Stops the playlist stream. When <paramref name="fadeoutMs"/> &gt; 0 the current track
    /// fades to silence over that many milliseconds and is freed afterwards; 0 cuts instantly.
    Task StopAsync(uint fadeoutMs = 0, CancellationToken ct = default);
    Task ResetAsync(CancellationToken ct = default);
    /// Arms or disarms repeat-current-track on the loaded stream. The repeat covers the
    /// region [<paramref name="loopStartMs"/>, <paramref name="loopEndMs"/>] — the track's
    /// CueStart/CueEnd — so a looping track never plays trimmed silence;
    /// <paramref name="loopEndMs"/> = 0 means "end of file".
    /// The engine only rewinds the audio: suppressing the playlist's advance to the next item
    /// is the caller's job (see IPlaylistController.SetLoopCurrentAsync).
    /// Arming with nothing loaded is legal — the state is remembered, and the caller re-arms
    /// with the new region on the next load.
    Task SetPlayerLoopAsync(bool loop, uint loopStartMs = 0, uint loopEndMs = 0, CancellationToken ct = default);

    Task TriggerCartAsync(string slotId, string assetId, string filePath, bool loop, CancellationToken ct = default);
    Task StopCartAsync(string slotId, CancellationToken ct = default);
    Task SetCartwallModeAsync(string mode, CancellationToken ct = default);

    Task SetVolumeAsync(string sourceId, float gainLinear, CancellationToken ct = default);
    Task FadeOutAsync(uint durationMs, CancellationToken ct = default);

    /// Starts an incoming track that plays simultaneously with the current one and
    /// fades the outgoing track to silence — a true overlapping crossfade (RadioDJ/mAirList
    /// style). The incoming stream becomes the active playlist stream immediately; the
    /// outgoing stream keeps playing, slides to zero over <paramref name="fadeOutMs"/>
    /// (optionally after <paramref name="fadeDelayMs"/>), then is freed automatically.
    Task CrossfadeToAsync(
        string assetId,
        string filePath,
        IReadOnlyList<AssetCuePoint> cuePoints,
        uint cueStartMs,
        uint fadeOutMs,
        uint fadeDelayMs,
        IReadOnlyList<EnvelopePoint>? envelope = null,
        CancellationToken ct = default);
    Task StartPflAsync(string assetId, string filePath, CancellationToken ct = default);
    Task StopPflAsync(CancellationToken ct = default);
    Task SeekPflAsync(int offsetMs, CancellationToken ct = default);

    // Multi-lane PFL preview — used by the segue editor to audition a mix.
    // Up to two independent lanes (0/1) play simultaneously so adjacent tracks
    // can be crossfaded by adjusting each lane's gain over the overlap.
    Task PreviewPlayAsync(int lane, string filePath, int positionMs, float gain, CancellationToken ct = default);
    Task PreviewSetGainAsync(int lane, float gain, CancellationToken ct = default);
    Task PreviewStopAsync(int lane, CancellationToken ct = default);
    Task PreviewStopAllAsync(CancellationToken ct = default);

    /// Current playback position (ms) of preview lane <paramref name="lane"/>, or -1 when the
    /// lane is idle or not actively playing. Lets the segue editor sync its playhead to the real
    /// BASS sample clock instead of a drifting wall-clock estimate.
    int GetPreviewPositionMs(int lane);

    /// Pauses a preview lane in place (keeps the stream and its position) so playback can resume
    /// seamlessly — unlike PreviewStop, which frees the lane.
    Task PreviewPauseAsync(int lane, CancellationToken ct = default);
    Task PreviewResumeAsync(int lane, CancellationToken ct = default);

    /// Seeks a preview lane to an absolute position (ms) within its file.
    Task PreviewSeekAsync(int lane, int positionMs, CancellationToken ct = default);

    /// Decodes the actual PCM for the source window [<paramref name="startMs"/>,
    /// <paramref name="endMs"/>] of <paramref name="filePath"/> and reduces it to
    /// <paramref name="columns"/> mono-mixed min/max pairs. Used by the segue editor
    /// to draw a sample-accurate waveform (Audacity-style) when the precomputed peaks
    /// are too coarse for the current zoom. Returns null on failure or empty window.
    Task<WaveformWindow?> ReadWaveformWindowAsync(
        string filePath, double startMs, double endMs, int columns, CancellationToken ct = default);

    /// Scans <paramref name="filePath"/> and returns the detected cue positions (in
    /// seconds) for START, NEXT_START and END based on the supplied dB thresholds, plus the
    /// file's actual decoded duration (DurationSec — a free byproduct of the linear scan).
    /// Only these fields are populated; all other marker fields are null.
    /// Returns null when the file cannot be decoded or no signal is found.
    Task<(double? Start, double? NextStart, double? End, double? DurationSec)?> AnalyzeCuePointsAsync(
        string filePath,
        double startDb,
        double nextStartDb,
        double endDb,
        CancellationToken ct = default);

    // ── AUX players (independent file decks 0..3) ─────────────────────────────

    /// Loads an arbitrary audio file into AUX <paramref name="index"/> in a paused
    /// state, generates its waveform peak cache and auto-detects START/END cues
    /// from the Audio DJ silence thresholds. Playback starts only on PlayAuxAsync.
    Task<AuxLoadResult> LoadAuxAsync(int index, string filePath, CancellationToken ct = default);
    Task PlayAuxAsync(int index, CancellationToken ct = default);
    Task PauseAuxAsync(int index, CancellationToken ct = default);
    /// Stops playback and rewinds to start; the file stays loaded. When <paramref name="fadeoutMs"/>
    /// &gt; 0 the deck fades to silence over that many milliseconds before pausing; 0 cuts instantly.
    Task StopAuxAsync(int index, uint fadeoutMs = 0, CancellationToken ct = default);
    /// Stops and unloads the file, freeing the BASS stream.
    Task EjectAuxAsync(int index, CancellationToken ct = default);
    Task SetAuxLoopAsync(int index, bool loop, CancellationToken ct = default);
    Task SetAuxVolumeAsync(int index, float gainLinear, CancellationToken ct = default);
    /// Routes the AUX deck: On = program bus (on air), Pfl = monitoring on the PFL device.
    Task SetAuxRouteAsync(int index, bool on, bool pfl, CancellationToken ct = default);
    /// Current playback position (ms) of AUX <paramref name="index"/>, or 0 when idle.
    int  GetAuxPositionMs(int index);

    // ── Streaming encoders (non-consuming taps on the program bus) ────────────

    /// Starts streaming <paramref name="profile"/> to its cast server. The password is passed
    /// separately, already decrypted — the engine never touches <see cref="ISecretProtector"/>,
    /// so plaintext exists only where the caller put it.
    /// Returns once the encoder is created; connecting and reconnecting continue in the background,
    /// observable through <see cref="GetEncoderStatus"/> and the encoder domain events.
    Task StartEncoderAsync(EncoderProfile profile, string? password, CancellationToken ct = default);

    /// Stops the session and cancels any pending reconnect. Safe to call when not running.
    Task StopEncoderAsync(string profileId, CancellationToken ct = default);

    /// Current state of one session, or null when that profile was never started.
    EncoderStatus? GetEncoderStatus(string profileId);

    /// Snapshot of every session the engine currently knows about.
    IReadOnlyList<EncoderStatus> GetAllEncoderStatuses();

    /// Offers the current "now playing" line to every live session. Each one decides what to do
    /// with it from its own profile — send it, send its own fixed text, or send nothing — because
    /// the session is what holds the profile; the caller only knows what is playing.
    Task SetEncoderTitleAsync(string nowPlayingTitle, CancellationToken ct = default);

    /// Whether this installation can actually produce <paramref name="format"/> — i.e. whether its
    /// BASSenc add-on DLL is present. Asked by the API so a profile using a missing format is
    /// refused at creation with a clear message, instead of failing later on air.
    bool IsEncoderFormatAvailable(EncoderFormat format);

    // ── Recording (the same non-consuming tap, writing to a file) ─────────────

    /// Starts recording the program bus to a file inside <paramref name="request"/>.Directory.
    /// One recording at a time — calling this while a recording runs returns the running one
    /// untouched, so a double click cannot silently split the archive into two files.
    /// Faults come back as <see cref="RecordingState.Error"/> in the returned status, not as
    /// exceptions; only an uninitialised engine throws.
    Task<RecordingStatus> StartRecordingAsync(RecordingRequest request, CancellationToken ct = default);

    /// Finalises the file and returns the closing status (including its path). Safe when idle.
    Task<RecordingStatus> StopRecordingAsync(CancellationToken ct = default);

    /// Current recorder state. <see cref="RecordingState.Stopped"/> when nothing ever ran.
    RecordingStatus GetRecordingStatus();

    // ── Microphone (antenna mic routed to program bus) ────────────────────────
    bool IsMicActive { get; }

    /// Initialises the recording device from AudioSettings.DeviceInputId,
    /// creates a push stream routed to the main mixer and starts the capture.
    /// Throws if a voice track recording is already in progress (mutual exclusion).
    Task StartMicAsync(CancellationToken ct = default);

    /// Stops the active microphone, removes it from the mixer and frees BASS resources.
    Task StopMicAsync(CancellationToken ct = default);

    /// Returns the current peak level of the mic input in dBFS (0.0 = full scale, -60 = silence).
    /// Returns -60 when the mic is not active.
    double GetMicLevelDb();

    // ── Microphone FX / VST chain ─────────────────────────────────────────────

    /// Adds a built-in BFX effect to the mic signal chain. Works even when mic is off.
    /// Returns a slotId used to identify the slot. Requires bass_fx.dll when mic is active.
    Task<int> AddMicFxAsync(MicFxType fxType, CancellationToken ct = default);

    /// Removes an effect slot by slotId (returned from AddMicFxAsync).
    Task RemoveMicFxAsync(int slotId, CancellationToken ct = default);

    /// Replaces the parameters of an effect slot, keyed as in MicFxParams. Unknown keys are
    /// dropped and values clamped to their range. Takes effect immediately when the mic is on,
    /// otherwise at the next start.
    Task UpdateMicFxAsync(int slotId, IReadOnlyDictionary<string, float> parameters, CancellationToken ct = default);

    IReadOnlyList<MicFxSlot> GetMicFxList();

    /// Adds a VST 2.x plugin to the mic signal chain config. Works even when mic is off.
    /// Returns a slotId. Requires bass_vst.dll when mic is active.
    Task<int> AddMicVstAsync(string dllPath, CancellationToken ct = default);

    /// Removes a VST plugin slot by slotId (returned from AddMicVstAsync).
    Task RemoveMicVstAsync(int slotId, CancellationToken ct = default);

    /// Embeds the VST plugin editor into parentWindow. Mic must be active (live handle needed).
    /// parentWindow must be a window dedicated to this editor: the plugin creates its GUI as a
    /// child window at (0,0) and never resizes the parent, so anything already drawn there is
    /// covered. Returns the editor size in pixels, queried after the editor was created — plugins
    /// are allowed to report it only at that point.
    Task<(int Width, int Height)> OpenMicVstEditorAsync(int slotId, IntPtr parentWindow, CancellationToken ct = default);

    /// Unembeds the editor opened by OpenMicVstEditorAsync. Must be called before the parent
    /// window is destroyed, otherwise the plugin keeps its editor flagged as open and refuses
    /// to embed it again (BASS_ERROR_ALREADY).
    Task CloseMicVstEditorAsync(int slotId, CancellationToken ct = default);

    /// Raised as (slotId, widthPx, heightPx) when a plugin asks for a different editor size.
    /// The plugin only requests it — the host owns the window, so ignoring this leaves the GUI
    /// clipped. Fired from a BASS thread: marshal to the UI thread before touching any window.
    event Action<int, int, int>? MicVstEditorResized;

    IReadOnlyList<MicVstSlot> GetMicVstList();

    /// Copies each loaded plugin's own settings into its MicVstSlot so they can be persisted.
    /// Requires the mic to be active — that is when the plugins are loaded — and must therefore
    /// run before the mic is stopped or the engine shut down.
    void CaptureMicVstStates();

    // ── Voice track recording ─────────────────────────────────────────────────
    bool IsRecording { get; }

    /// Initialises the recording device from AudioSettings.DeviceInputId and
    /// starts capturing to a WAV file at <paramref name="outputWavPath"/>.
    Task StartVoiceTrackRecordingAsync(string outputWavPath, CancellationToken ct = default);

    /// Stops the active recording and closes the WAV file.
    /// Returns the recorded duration in milliseconds.
    Task<uint> StopVoiceTrackRecordingAsync(CancellationToken ct = default);

    /// Returns the current peak level of the recording stream (0.0–1.0).
    /// Returns 0 when not recording.
    float GetVoiceTrackRecordingLevel();

    Task ScheduleSweeperAsync(long triggerAtMs, string sweeperFilePath, float duckDb = 6f, CancellationToken ct = default);

    /// Returns the current peak level of the main playlist mixer output as
    /// two values (Left, Right) in the range 0–100, mapped logarithmically
    /// so that −18 dBFS ≈ 70 (green/yellow boundary) and 0 dBFS = 100.
    /// <paramref name="offsetDb"/> is a display-only correction (e.g. LUFS-based) added
    /// before the 0–100 mapping — it does not touch playback gain.
    /// Returns (0, 0) when the engine is not playing or not initialised.
    (double Left, double Right) GetPlaylistLevel(double offsetDb = 0.0);

    // Removes any pending sweeper BASS_SYNC_POS callback for the current stream.
    // Must be called before NextTrackAsync / StopAsync / ClearAsync to prevent
    // a scheduled sweeper from firing on the wrong (next) track. (Bug 2 fix)
    Task CancelScheduledSweepersAsync(CancellationToken ct = default);
}
