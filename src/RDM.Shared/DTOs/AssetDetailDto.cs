using System;

namespace RDM.Shared.DTOs;

/// Full asset record — used by TrackEditorWindow. Richer than AssetDto (search results).
public record AssetDetailDto(
    string                      AssetId,
    string                      AssetType,
    string?                     FormatId,
    string?                     SubcategoryId,
    string                      Title,
    string?                     Artist,
    string?                     Album,
    uint                        DurationMs,
    string?                     RdmFilePath,
    string?                     StreamUrl,
    string?                     ImagePath,
    decimal?                    Bpm,
    int?                        Year,
    byte?                       Rating,
    string?                     Mood,
    string?                     Gender,
    string?                     Language,
    string?                     Genre,
    string?                     Comments,
    bool                        IsDamaged,
    bool                        IsVariableDuration,
    string                      Status,
    DateTime?                   StartDate,
    DateTime?                   EndDate,
    uint?                       PlayLimit,
    uint                        PlayCount,
    DateTime?                   LastPlayedAt,
    decimal?                    LoudnessLufs,
    decimal?                    LoudnessPeak,
    CueMarkersDto?              CueMarkers = null
);

/// Cue Editor named markers — all values in seconds, null means not set.
public record CueMarkersDto(
    double? Start,
    double? Intro,
    double? Ramp2,
    double? Ramp3,
    double? Outro,
    double? StartNext,
    double? FadeOut,
    double? FadeEnd,
    double? End,
    double? HookIn,
    double? HookFade,
    double? HookOut,
    double? LoopIn,
    double? LoopOut,
    double? Anchor
);

/// Request body for PUT /api/v1/track/{assetId}.
public record UpdateAssetRequestDto(
    string                          Title,
    string?                         Artist,
    string?                         Album,
    string?                         FormatId,
    string?                         SubcategoryId,
    decimal?                        Bpm,
    int?                            Year,
    byte?                           Rating,
    string?                         Mood,
    string?                         Gender,
    string?                         Language,
    string?                         Genre,
    string?                         Comments,
    string                          Status,
    DateTime?                       StartDate,
    DateTime?                       EndDate,
    uint?                           PlayLimit,
    string?                         AssetType  = null,
    CueMarkersDto?                  CueMarkers = null,
    string?                         ImagePath  = null,
    string?                         StreamUrl  = null,
    bool?                           IsVariableDuration = null
);

public record UpdateAssetResponseDto(
    string   AssetId,
    DateTime UpdatedAt
);

public record PurgeOrphansResponseDto(
    int                    Deleted,
    IReadOnlyList<string>  DeletedTitles
);

public record OptimizeDatabaseResponseDto(
    bool Success,
    long DurationMs
);

public record DeleteAssetResponseDto(
    string AssetId,
    bool   FileDeleted
);

public record BatchDeleteAssetsRequestDto(
    IReadOnlyList<string> AssetIds,
    bool                  DeleteFiles
);

public record BatchDeleteAssetsResponseDto(
    int Deleted,
    int FilesDeleted
);

public record PflFromFileRequestDto(string FilePath);
