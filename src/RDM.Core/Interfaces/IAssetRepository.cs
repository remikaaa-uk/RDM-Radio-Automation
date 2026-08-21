using RDM.Core.Entities;
using RDM.Core.Models;
using RDM.Shared.DTOs;
using RDM.Shared.Enums;


namespace RDM.Core.Interfaces;

public interface IAssetRepository
{
    Task<Asset?> GetByIdAsync(string assetId, CancellationToken ct = default);
    Task<Asset?> FindByFilePathAsync(string filePath, CancellationToken ct = default);
    Task<(IReadOnlyList<Asset> Items, int Total)> SearchAsync(AssetSearchParams searchParams, CancellationToken ct = default);
    Task<Asset?> GetByChecksumAsync(string checksum, CancellationToken ct = default);
    Task<IReadOnlyList<Asset>> GetByFormatAsync(string formatId, AssetStatus? status = null, CancellationToken ct = default);
    Task CreateAsync(Asset asset, CancellationToken ct = default);
    Task UpdateAsync(Asset asset, CancellationToken ct = default);
    Task UpdateCueMarkersAsync(string assetId, CueMarkersDto markers, CancellationToken ct = default);
    Task UpdateStatusAsync(string assetId, AssetStatus status, CancellationToken ct = default);
    Task UpdatePlayCountAsync(string assetId, uint playCount, DateTime lastPlayedAt, CancellationToken ct = default);
    Task<int> UpdateDurationAsync(string assetId, uint durationMs, CancellationToken ct = default);
    Task<int> UpdateLoudnessAsync(string assetId, decimal? loudnessLufs, decimal? loudnessPeak, CancellationToken ct = default);
    Task<int> UpdateBpmAsync(string assetId, decimal bpm, CancellationToken ct = default);
    Task<IReadOnlyList<AssetWaveformInfo>> GetAllForWaveformScanAsync(CancellationToken ct = default);
    Task DeleteAsync(string assetId, CancellationToken ct = default);
    Task<IReadOnlyList<string?>> GetFilePathsByIdsAsync(IReadOnlyList<string> assetIds, CancellationToken ct = default);
    Task<int> BatchDeleteAsync(IReadOnlyList<string> assetIds, CancellationToken ct = default);
    Task<IReadOnlyList<(string AssetId, string? RdmFilePath, string Title)>> GetAllFilePathsAsync(CancellationToken ct = default);
    Task OptimizeDatabaseAsync(CancellationToken ct = default);
}
