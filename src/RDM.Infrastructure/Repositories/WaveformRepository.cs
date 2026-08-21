using Dapper;
using RDM.Core.Interfaces;
using RDM.Core.Models;

namespace RDM.Infrastructure.Repositories;

public sealed class WaveformRepository : IWaveformRepository
{
    private readonly IDbConnectionFactory _factory;

    public WaveformRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task SaveAsync(string assetId, byte[] waveData, int nPoints, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO asset_waveforms (asset_id, wave_data, n_points, version, created_at)
                VALUES (@AssetId, @WaveData, @NPoints, @Version, UTC_TIMESTAMP())
                ON DUPLICATE KEY UPDATE
                    wave_data  = VALUES(wave_data),
                    n_points   = VALUES(n_points),
                    version    = VALUES(version),
                    created_at = UTC_TIMESTAMP()
                """,
                new { AssetId = assetId, WaveData = waveData, NPoints = nPoints, Version = WaveformConstants.Version },
                cancellationToken: ct));
    }

    public async Task<byte[]?> GetAsync(string assetId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<byte[]>(
            new CommandDefinition(
                "SELECT wave_data FROM asset_waveforms WHERE asset_id = @AssetId",
                new { AssetId = assetId },
                cancellationToken: ct));
    }

    public async Task<bool> ExistsAsync(string assetId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var count = await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(1) FROM asset_waveforms WHERE asset_id = @AssetId",
                new { AssetId = assetId },
                cancellationToken: ct));
        return count > 0;
    }

    public async Task<IReadOnlySet<string>> GetExistingAssetIdsAsync(CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var ids = await conn.QueryAsync<string>(
            new CommandDefinition(
                "SELECT asset_id FROM asset_waveforms",
                cancellationToken: ct));
        return ids.ToHashSet();
    }
}
