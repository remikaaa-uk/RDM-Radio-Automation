using System;
using System.Collections.Generic;

namespace RDM.Shared.DTOs;

public record ImportRequestDto(
    string FilePath,
    string AssetType,
    string? FormatId,
    string? SubcategoryId,
    bool ReadRdm,
    bool ReadMmd,
    bool ReadWfrm,
    bool ReadId3
);

public record ImportResponseDto(
    string ImportId,
    string Status,
    string FilePath
);

public record ImportStatusDto(
    string ImportId,
    string Status,
    string? AssetId,
    string? Title,
    string? Artist,
    DateTime? CompletedAt,
    bool IsDuplicate = false
);

public record AnalyzeLoudnessRequestDto(
    IReadOnlyList<string> AssetIds
);

public record AnalyzeLoudnessResponseDto(
    int Queued,
    string Message
);

public record AnalyzeCueRequestDto(
    IReadOnlyList<string> AssetIds,
    double StartDb,
    double NextStartDb,
    double EndDb
);

public record AnalyzeCueResponseDto(
    int Queued,
    string Message
);

public record AddStreamRequestDto(
    string Name,
    string StreamUrl
);

public record AddStreamResponseDto(
    string AssetId,
    string Name,
    string StreamUrl
);

public record Id3PeekRequestDto(string FilePath);

public record Id3PeekResponseDto(
    string?  Title,
    string?  Artist,
    string?  Album,
    int?     Year,
    decimal? Bpm,
    string?  Genre,
    int?     DurationMs,
    string?  PictureBase64,
    string?  PictureMimeType
);
