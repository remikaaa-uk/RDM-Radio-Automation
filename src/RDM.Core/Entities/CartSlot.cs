namespace RDM.Core.Entities;

public sealed class CartSlot
{
    public string SlotId { get; init; } = string.Empty;
    public string CartwallId { get; init; } = string.Empty;
    public string? AssetId { get; init; }
    public byte SlotNumber { get; init; }
    public string? Label { get; init; }
    public string? Color { get; init; }
    public string? Hotkey { get; init; }
    public bool Loop { get; init; }
    public uint? FadeoutMs { get; init; }
    public decimal OutputGainDb { get; init; }
}
