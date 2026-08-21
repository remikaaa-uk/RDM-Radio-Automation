using RDM.Core.Entities;

namespace RDM.Core.Interfaces;

public interface IAudioDeviceRepository
{
    Task<AudioDevice?> GetByIdAsync(string deviceId, CancellationToken ct = default);
    Task<IReadOnlyList<AudioDevice>> GetByStudioAsync(string studioId, CancellationToken ct = default);
    Task UpsertAsync(AudioDevice device, CancellationToken ct = default);
    Task UpdateAvailabilityAsync(string deviceId, bool isAvailable, DateTime lastSeenAt, CancellationToken ct = default);
}
