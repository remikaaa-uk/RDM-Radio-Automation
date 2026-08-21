using System;

namespace RDM.Shared.DTOs;

public record AssetDto(
    string AssetId,
    string AssetType,
    string? FormatName,
    string? SubcategoryName,
    string Title,
    string? Artist,
    string? Album,
    uint DurationMs,
    decimal? Bpm,
    int? Year,
    byte? Rating,
    string Status,
    decimal? LoudnessLufs,
    string? Mood,
    string? Gender,
    string? Language,
    string? Genre,
    uint PlayCount,
    DateTime CreatedAt,
    CueMarkersDto? CueMarkers = null
);

public record AssetSearchEnvelopeDto(
    int Total,
    int Limit,
    int Offset,
    IReadOnlyList<AssetDto> Items
);

public record AssetStatusDto(
    string AssetId,
    string Status,
    bool IsDamaged,
    uint PlayCount,
    uint? PlayLimit,
    DateTime? LastPlayedAt
);

public record AssetStatusUpdateRequestDto(
    string Status
);

public record AssetStatusUpdateResponseDto(
    string AssetId,
    string Status,
    DateTime UpdatedAt
);
