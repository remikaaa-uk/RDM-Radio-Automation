using RDM.Core.Entities;

namespace RDM.Core.Interfaces;

public interface IPlayoutLogRepository
{
    Task CreateAsync(PlayoutLog entry, CancellationToken ct = default);
    Task UpdateEndedAtAsync(string logId, DateTime endedAt, CancellationToken ct = default);
    Task<IReadOnlyList<PlayoutLog>> GetByStudioAsync(string studioId, DateTime from, DateTime to, CancellationToken ct = default);
    Task<IReadOnlyList<PlayoutLog>> GetRecentAsync(string studioId, int limit, int offset = 0, CancellationToken ct = default);
    Task<int> CountAsync(string studioId, CancellationToken ct = default);
}
