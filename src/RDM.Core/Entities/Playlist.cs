using RDM.Shared.Enums;

namespace RDM.Core.Entities;

public sealed class Playlist
{
    public string       PlaylistId   { get; init; } = string.Empty;
    public string       StudioId     { get; init; } = string.Empty;
    public string       Name         { get; init; } = string.Empty;
    public PlaylistType PlaylistType { get; init; } = PlaylistType.Saved;
    public DateTime     CreatedAt    { get; init; }
}
