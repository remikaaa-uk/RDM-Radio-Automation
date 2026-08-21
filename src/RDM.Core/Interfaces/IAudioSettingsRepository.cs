using RDM.Core.Entities;

namespace RDM.Core.Interfaces;

public interface IAudioSettingsRepository
{
    Task<AudioSettings?> GetByStudioAsync(string studioId, CancellationToken ct = default);
    Task CreateAsync(AudioSettings settings, CancellationToken ct = default);
    Task UpdateAsync(AudioSettings settings, CancellationToken ct = default);
    Task UpdateBackupLastAtAsync(string settingsId, DateTime lastAt, CancellationToken ct = default);
}
