using RDM.Core.Entities;

namespace RDM.Core.Interfaces;

public interface IEncoderProfileRepository
{
    Task<EncoderProfile?> GetByIdAsync(string profileId, CancellationToken ct = default);
    Task<IReadOnlyList<EncoderProfile>> GetByStudioAsync(string studioId, CancellationToken ct = default);

    /// <summary>
    /// Profiles the operator marked ready — the set the bottom-bar button starts.
    /// Wider than <see cref="GetAutoStartAsync"/>: auto-starting implies armed, not the reverse.
    /// </summary>
    Task<IReadOnlyList<EncoderProfile>> GetArmedAsync(string studioId, CancellationToken ct = default);

    /// <summary>Profiles flagged to start automatically once the audio engine is up.</summary>
    Task<IReadOnlyList<EncoderProfile>> GetAutoStartAsync(string studioId, CancellationToken ct = default);

    Task CreateAsync(EncoderProfile profile, CancellationToken ct = default);
    Task UpdateAsync(EncoderProfile profile, CancellationToken ct = default);
    Task DeleteAsync(string profileId, CancellationToken ct = default);
}
