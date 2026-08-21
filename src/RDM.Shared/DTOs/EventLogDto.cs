using System;
using System.Collections.Generic;

namespace RDM.Shared.DTOs;

public record EventLogEntryDto(
    string   EventId,
    string   EventName,
    DateTime FiredAt,
    string   Result     // "SUCCESS" | "PARTIAL" | "FAILED"
);

public record EventLogEnvelopeDto(IReadOnlyList<EventLogEntryDto> Items);
