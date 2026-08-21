using System;

namespace RDM.Shared.DTOs;

public record WebSocketFrameDto(
    string EventId,
    string EventType,
    DateTime Timestamp,
    object Payload
);
