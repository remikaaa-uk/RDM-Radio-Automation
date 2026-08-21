using System.Collections.Generic;

namespace RDM.Shared.DTOs;

public record CartSlotDto(
    string   SlotId,
    string   CartwallId,
    string?  AssetId,
    int      SlotNumber,
    string?  Label,
    string?  Color,         // hex "#RRGGBB" or null
    string?  Hotkey,
    bool     Loop,
    uint?    FadeoutMs,
    decimal  OutputGainDb,
    // Denormalized asset fields
    string?  Title,
    string?  Artist,
    uint?    DurationMs
);

public record CartwallDto(
    string                      CartwallId,
    string                      Name,
    int                         PageOrder,
    string?                     Hotkey,
    IReadOnlyList<CartSlotDto>  Slots
);

public record CartwallsEnvelopeDto(IReadOnlyList<CartwallDto> Items);

public record PatchCartSlotDto(
    string? AssetId     = null,
    string? Label       = null,
    string? Color       = null,
    string? Hotkey      = null,
    bool?   Loop        = null,
    bool    ClearAsset  = false
);

public record CartTriggerResponseDto(
    string  SlotId,
    string  Status,
    uint?   DurationMs
);

public record SetCartwallModeRequestDto(string Mode);  // "ON" | "PFL" | "OFF"

public record CreateCartwallDto(string Name);

public record PatchCartwallDto(string? Name = null, int? PageOrder = null);
