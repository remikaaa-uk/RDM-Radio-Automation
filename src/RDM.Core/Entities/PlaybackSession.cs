using RDM.Shared.Enums;

namespace RDM.Core.Entities;

public sealed class PlaybackSession
{
    public string SessionId { get; init; } = string.Empty;
    public string StudioId { get; init; } = string.Empty;
    public string? CurrentAssetId { get; init; }
    public uint CurrentPositionMs { get; init; }
    public string? NextAssetId { get; init; }
    public string? PlaylistId { get; init; }
    public string? PlaylistItemId { get; init; }
    public SessionState State { get; init; }
    public PlaylistMode Mode { get; init; }
    public DateTime SnapshotAt { get; init; }
}
