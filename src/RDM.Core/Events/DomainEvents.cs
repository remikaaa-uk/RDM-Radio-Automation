using RDM.Core.Models;
using RDM.Shared.Enums;

namespace RDM.Core.Events;

// ── Audio Engine Events ───────────────────────────────────────────────────────

public record TrackStartedEvent(
    string  AssetId,
    string? Title,
    string? Artist,
    uint    DurationMs);

public record TrackOutroReachedEvent(
    string AssetId,
    long   RemainingMs);

public record TrackIntroReachedEvent(string AssetId);

public record TrackEndedEvent(
    string AssetId,
    string EndReason);   // "NATURAL" | "SKIPPED" | "STOPPED"

public record TrackStartNextReachedEvent(string AssetId);

public record TrackFadeOutReachedEvent(string AssetId);

public record TrackCueEndReachedEvent(string AssetId);

public record CuePointReachedEvent(
    string     AssetId,
    MarkerType MarkerType,
    uint       PositionMs);

public record CartTriggeredEvent(
    string SlotId,
    string AssetId,
    bool   Loop,
    uint   DurationMs = 0);

public record CartEndedEvent(
    string SlotId,
    string AssetId);

public record BufferUnderrunEvent(
    string         SourceId,
    DateTimeOffset Timestamp);

public record EngineReadyEvent(
    int                    SampleRate,
    int                    BufferSize,
    IReadOnlyList<string>  ActiveDevices);

// ── Asset Events ──────────────────────────────────────────────────────────────

public record AssetCreatedEvent(
    string  AssetId,
    string  Title,
    string? Artist);

public record AssetLibraryChangedEvent();

public record WaveformReadyEvent(string AssetId);

public record LoudnessAnalyzedEvent(
    string  AssetId,
    decimal LufsIntegrated,
    decimal TruePeak);

// ── Playlist Domain Events (Bug 1 fix: enriched with ItemId) ─────────────────
// PlaylistEngine translates raw audio TrackStartedEvent/TrackEndedEvent into
// these playlist-level events that carry the unique PlaylistItem.ItemId,
// preventing correlation errors when the same asset appears multiple times.

public record PlaylistItemStartedEvent(
    string    ItemId,
    string?   AssetId,          // null for external (non-library) files played from disk
    string    Title,
    string?   Artist,
    uint      DurationMs,
    AssetType AssetType,
    string?   FormatId,
    double    VuOffsetDb = 0.0); // display-only VU meter dB correction, LoudnessTargetLufs - Asset.LoudnessLufs

public record PlaylistItemEndedEvent(
    string  ItemId,
    string? AssetId,            // null for external (non-library) files played from disk
    string  EndReason);   // "NATURAL" | "SKIPPED" | "STOPPED"

public record PlaylistStartedEvent(string PlaylistId);

public record PlaylistStoppedEvent(
    string PlaylistId,
    string Reason);      // "NATURAL" | "MANUAL" | "ERROR"

public record PlaylistModeChangedEvent(
    PlaylistMode PreviousMode,
    PlaylistMode NewMode);

/// Raised whenever the live queue's contents are replaced wholesale (ClearAsync /
/// LoadPlaylistAsync) rather than edited item-by-item — those already have their own
/// UI-local refresh (ApiClientService.PlaylistUpdated). This covers changes triggered
/// outside a direct UI command, e.g. a scheduled event's CLEAR_PLAYLIST/LOAD_PLAYLIST action.
public record PlaylistQueueReloadedEvent(string PlaylistId);

/// Raised by PlaylistEngine.OnDeadAirTickAsync when the master output has been silent for
/// DeadAirThresholdS seconds while in AUTO mode. SilenceMs = how long the output has been silent.
public record DeadAirWarningEvent(
    long         SilenceMs,
    PlaylistMode Mode);

// ── Scheduled Event Events ────────────────────────────────────────────────────

public record ScheduledEventFiredEvent(
    string   EventId,
    string   Name,
    DateTime FiredAt,
    string   ActionsJson,
    string   Result);    // "SUCCESS" | "PARTIAL" | "FAILED"

public record ScheduledEventSkippedEvent(
    string EventId,
    string Name);

// ── AUX Input Events ──────────────────────────────────────────────────────────

/// Published by BassAudioEngine ~30 Hz while an AUX input is active.
/// Consumed in-process by AuxPlayersViewModel via IEventBus (not broadcast over WebSocket).
public record AuxLevelChangedEvent(
    int   AuxIndex,
    float LevelDb);

public record AuxActivatedEvent(int AuxIndex);
public record AuxDeactivatedEvent(int AuxIndex);

/// Published by BassAudioEngine ~10 Hz while an AUX player is playing.
/// Consumed in-process by AuxPlayersViewModel to drive the waveform playhead.
public record AuxPositionChangedEvent(int AuxIndex, uint PositionMs, uint DurationMs);

/// Published by BassAudioEngine when a deck's load/loop/volume/route changes, so the AUX tiles
/// reflect actions from ANY trigger (in-app command, API, MIDI/script, hardware) — not just the UI.
public record AuxLoadedEvent(
    int    AuxIndex,
    string FilePath,
    uint   DurationMs,
    string WaveformPath,
    uint   StartMs,
    uint   EndMs);
public record AuxLoopChangedEvent(int AuxIndex, bool Loop);

/// Repeat-current-track was switched on the main player. Published by PlaylistEngine so the
/// player UI reflects the state the playout engine really holds, whatever triggered the change.
public record PlayerLoopChangedEvent(bool Loop);

/// A looping track just rewound to <paramref name="PositionMs"/> (its CueStart). Position is
/// tracked as wall-clock time elapsed since the track started, so every consumer that shows a
/// playhead or a countdown has to restart that clock here — the track did not end and no
/// TrackStarted follows.
public record PlayerLoopedEvent(string AssetId, uint PositionMs);
public record AuxVolumeChangedEvent(int AuxIndex, float Gain);
public record AuxRouteChangedEvent(int AuxIndex, bool On, bool Pfl);

// ── Internet Stream Events ────────────────────────────────────────────────────
public record StreamMetaChangedEvent(string AssetId, string StreamTitle);
public record StreamConnectFailedEvent(string AssetId, string Reason);

// ── Encoder / Streaming Events ────────────────────────────────────────────────
// One event carries the whole status snapshot: the UI indicator aggregates several sessions and
// would otherwise have to reassemble state from a stream of finer-grained events.
public record EncoderStatusChangedEvent(EncoderStatus Status);

// ── Recording Events ──────────────────────────────────────────────────────────
public record RecordingStatusChangedEvent(RecordingStatus Status);

// ── PFL Events ────────────────────────────────────────────────────────────────
public record PflEndedEvent(string AssetId);

// ── Mic Events ────────────────────────────────────────────────────────────────
public record MicActivatedEvent();
public record MicDeactivatedEvent();

// ── Backup Events ─────────────────────────────────────────────────────────────

public record BackupCompletedEvent(
    string   Path,
    long     SizeBytes,
    DateTime Timestamp);

public record BackupFailedEvent(string Reason);
