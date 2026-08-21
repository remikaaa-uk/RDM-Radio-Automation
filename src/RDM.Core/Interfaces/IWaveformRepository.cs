namespace RDM.Core.Interfaces;

public interface IWaveformRepository
{
    Task SaveAsync(string assetId, byte[] waveData, int nPoints, CancellationToken ct = default);
    Task<byte[]?> GetAsync(string assetId, CancellationToken ct = default);
    Task<bool> ExistsAsync(string assetId, CancellationToken ct = default);
    Task<IReadOnlySet<string>> GetExistingAssetIdsAsync(CancellationToken ct = default);
}
