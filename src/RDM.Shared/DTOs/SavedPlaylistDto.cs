using System;
using System.Collections.Generic;

namespace RDM.Shared.DTOs;

public record SavedPlaylistSummaryDto(
    string   PlaylistId,
    string   Name,
    DateTime CreatedAt,
    int      ItemCount
);

public record SavedPlaylistsEnvelopeDto(IReadOnlyList<SavedPlaylistSummaryDto> Items);

public record SavedPlaylistDetailDto(
    string                          PlaylistId,
    string                          Name,
    DateTime                        CreatedAt,
    IReadOnlyList<PlaylistItemDto>  Items
);

public record SavePlaylistRequestDto(
    string                               Name,
    IReadOnlyList<PlaylistItemSaveDto>   Items
);

/// Minimal item descriptor for saving a playlist to the database.
public record PlaylistItemSaveDto(
    string?  AssetId,
    string   ItemType,
    string?  DummyLabel,
    string?  DummyNote,
    uint?    DummyDurationMs,
    uint?    CrossfadeMs,
    uint?    TrimStartMs,
    uint?    TrimEndMs,
    string   SegueType,
    bool     AutoLinkNext,
    string?  VolumeEnvelope = null
);

public record SavePlaylistResponseDto(
    string   PlaylistId,
    string   Name,
    DateTime CreatedAt
);
