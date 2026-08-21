using RDM.Core.Entities;

namespace RDM.Core.Interfaces;

public interface IScheduledEventRepository
{
    Task<ScheduledEvent?> GetByIdAsync(string eventId, CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledEvent>> GetByStudioAsync(string studioId, CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledEvent>> GetEnabledAsync(string studioId, CancellationToken ct = default);
    Task CreateAsync(ScheduledEvent scheduledEvent, CancellationToken ct = default);
    Task UpdateAsync(ScheduledEvent scheduledEvent, CancellationToken ct = default);
    Task UpdateSkipNextAsync(string eventId, bool skipNext, CancellationToken ct = default);
    Task UpdateLastFiredAsync(string eventId, DateTime firedAt, CancellationToken ct = default);
    Task DeleteAsync(string eventId, CancellationToken ct = default);
}
