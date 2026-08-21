using System;

namespace RDM.Shared.DTOs;

public record CurrentTrackDto(
    string AssetId,
    string Title,
    string? Artist,
    uint DurationMs,
    uint PositionMs,
    uint RemainingMs,
    DateTime? StartedAt,
    CueMarkersDto? CueMarkers = null
);

public record NextTrackDto(
    string AssetId,
    string Title,
    string? Artist,
    uint DurationMs,
    DateTime? ScheduledAt
);

public record NowPlayingDto(
    CurrentTrackDto? NowPlaying,
    NextTrackDto? NextTrack,
    string PlaylistMode,
    string State,
    bool LoopCurrent = false
);

public record PlayoutLogDto(
    string? AssetId,
    string? Title,
    string? Artist,
    DateTime StartedAt,
    DateTime? EndedAt,
    string SourceType
);

public record PlayoutHistoryResponseDto(
    IReadOnlyList<PlayoutLogDto> History,
    int Total
);
