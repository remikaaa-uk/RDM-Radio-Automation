using RDM.Shared.Enums;

namespace RDM.Core.Entities;

public sealed class AssetCuePoint
{
    public string CueId { get; init; } = string.Empty;
    public string AssetId { get; init; } = string.Empty;
    public MarkerType MarkerType { get; init; }
    public uint PositionMs { get; init; }
    public string? Label { get; init; }
}
