using RDM.Core.Entities;

namespace RDM.Core.Interfaces;

public interface IPlaybackSessionRepository
{
    Task<PlaybackSession?> GetByStudioAsync(string studioId, CancellationToken ct = default);
    Task UpsertAsync(PlaybackSession session, CancellationToken ct = default);
}
