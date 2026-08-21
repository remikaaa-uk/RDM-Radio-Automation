namespace RDM.Shared.DTOs;

public record ErrorResponseDto(
    string ErrorCode,
    string Message,
    string TraceId
);
