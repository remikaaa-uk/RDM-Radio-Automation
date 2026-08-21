namespace RDM.Shared.DTOs;

public record PlaybackStatusResponseDto(
    string Status,
    string State
);

public record PlaybackNextResponseDto(
    string Status,
    string AssetId,
    string Title,
    string? Artist
);

public record PlaybackResetResponseDto(
    string Status,
    uint PositionMs
);

public record PlaybackPlaySpecificResponseDto(
    string Status,
    string AssetId,
    string Title,
    string? Artist
);
