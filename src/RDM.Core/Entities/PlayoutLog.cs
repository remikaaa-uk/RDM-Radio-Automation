using RDM.Shared.Enums;

namespace RDM.Core.Entities;

public sealed class PlayoutLog
{
    public string LogId { get; init; } = string.Empty;
    public string StudioId { get; init; } = string.Empty;
    public string? AssetId { get; init; }
    public string? TempTitle { get; init; }
    public string? TempArtist { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public SourceType SourceType { get; init; }
}
