using System;

namespace RDM.Shared.DTOs;

public record PlaylistItemDto(
    string                      ItemId,
    string?                     AssetId,
    uint                        Position,
    string                      ItemType,           // "ASSET" | "DUMMY"
    string?                     ExternalFilePath,
    string?                     DummyLabel,
    string?                     DummyNote,
    uint?                       DummyDurationMs,
    uint?                       CrossfadeMs,
    int?                        LeadInMs,
    uint?                       TrimStartMs,
    uint?                       TrimEndMs,
    string                      SegueType,          // "AUTO" | "MANUAL" | "TIMED"
    DateTime?                   ScheduledAt,
    bool                        AutoLinkNext,
    // Denormalized asset fields (null for DUMMY)
    string?                     Title,
    string?                     Artist,
    uint?                       DurationMs,
    string?                     AssetType,          // "TRACK" | "CART" | "SWEEPER" | "VOICETRACK"
    string?                     FormatName,
    string?                     Status,
    bool?                       IsDamaged,
    string?                     Comments,           // operator notes from the library; shown as the playlist row tooltip
    CueMarkersDto?              CueMarkers = null,
    string?                     VolumeEnvelope = null
);

public record PlaylistItemsEnvelopeDto(
    IReadOnlyList<PlaylistItemDto> Items,
    string                         Mode,           // AUTO | LIVE_ASSIST | MANUAL
    string                         State,          // IDLE | PLAYING | PAUSED
    string?                        CurrentItemId
);

public record AddPlaylistItemRequestDto(
    string?  AssetId,
    string?  ExternalFilePath,
    int      Position,
    string   ItemType = "ASSET"
);

public record AddPlaylistItemResponseDto(
    string ItemId,
    uint   Position
);

/// Adds a non-library file to the queue as an external item: played directly from disk,
/// never imported into the asset library. Title/Artist/DurationMs are captured at add time
/// (from M3U #EXTINF, or ID3 read server-side when omitted).
public record AddExternalPlaylistItemRequestDto(
    string  FilePath,
    int     Position,
    string? Title      = null,
    string? Artist     = null,
    uint?   DurationMs = null
);

public record ReorderPlaylistItemRequestDto(
    string ItemId,
    int    NewPosition
);

public record PatchPlaylistItemDto(
    uint?   CrossfadeMs    = null,
    int?    LeadInMs       = null,
    uint?   TrimStartMs    = null,
    uint?   TrimEndMs      = null,
    string? SegueType      = null,
    bool?   AutoLinkNext   = null,
    string? VolumeEnvelope = null
);

public record ChangePlaylistModeRequestDto(string Mode);
