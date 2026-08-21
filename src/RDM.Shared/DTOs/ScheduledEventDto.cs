using System;
using System.Collections.Generic;

namespace RDM.Shared.DTOs;

public record ScheduledEventActionDto(
    string Type,
    object Payload
);

public record ScheduledEventDto(
    string EventId,
    string Name,
    string EventType,
    string Category,
    bool Enabled,
    string? EventHour,
    IReadOnlyList<string> Days,
    IReadOnlyList<int> Hours,
    bool SmartTiming,
    IReadOnlyList<ScheduledEventActionDto> Actions,
    DateTime? LastFiredAt,
    bool SkipNext,
    string? OnlyOnDate = null   // yyyy-MM-dd, only for ONE_TIME events
);

public record ScheduledEventResponseEnvelopeDto(
    IReadOnlyList<ScheduledEventDto> Items
);

public record ScheduledEventCreateDto(
    string Name,
    string EventType,
    string Category,
    bool Enabled,
    string? EventHour,
    IReadOnlyList<string> Days,
    IReadOnlyList<int> Hours,
    bool SmartTiming,
    IReadOnlyList<ScheduledEventActionDto> Actions,
    string? OnlyOnDate = null   // yyyy-MM-dd, required for ONE_TIME events
);

public record ScheduledEventCreatedResponseDto(
    string EventId,
    string Name,
    DateTime CreatedAt
);

public record ScheduledEventUpdateResponseDto(
    string EventId,
    DateTime UpdatedAt
);

public record ScheduledEventPatchDto(
    bool? Enabled,
    bool? SkipNext
);

public record NextScheduledEventDto(
    string EventId,
    string Name,
    DateTime FiresAt,
    uint RemainingMs,
    bool SkipNext
);
