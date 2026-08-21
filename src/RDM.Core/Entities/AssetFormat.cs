namespace RDM.Core.Entities;

public sealed class AssetFormat
{
    public string FormatId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; }
}
