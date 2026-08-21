namespace RDM.Core.Entities;

public sealed class Studio
{
    public string StudioId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}
