namespace RDM.Core.Entities;

public sealed class Cartwall
{
    public string CartwallId { get; init; } = string.Empty;
    public string StudioId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public byte PageOrder { get; init; }
    public string? Hotkey { get; init; }
}
