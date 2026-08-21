using System;
using System.Collections.Generic;

namespace RDM.Shared.DTOs;

/// Request to scan a set of already-enumerated file paths for tracks that do not
/// yet exist in the library (Update Tracks feature). The UI enumerates audio
/// files locally (recursively, filtered by SupportedAudioExtensions) and sends
/// the resulting paths here.
public record ScanRequestDto(
    IReadOnlyList<string> FilePaths
);

public record ScanResponseDto(
    string ScanId,
    string Status
);

public record ScanStatusDto(
    string ScanId,
    string Status, // "QUEUED", "PROCESSING", "COMPLETED", "FAILED"
    int Done,
    int Total,
    DateTime? CompletedAt = null
);

/// A file found during scanning that is new to the library (exists neither by
/// path nor by checksum). Metadata is read for display only — the actual import
/// re-reads it through the full pipeline.
public record NewTrackDto(
    string FilePath,
    string Filename,
    string? Artist,
    string? Title,
    int? DurationMs,
    string? Folder
);
