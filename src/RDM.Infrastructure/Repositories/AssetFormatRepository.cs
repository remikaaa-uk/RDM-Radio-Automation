using Dapper;
using RDM.Core.Entities;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Repositories;

public sealed class AssetFormatRepository : IAssetFormatRepository
{
    private readonly IDbConnectionFactory _factory;

    public AssetFormatRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<AssetFormat>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<AssetFormatRow>(
            new CommandDefinition(
                "SELECT format_id, name, description, created_at FROM asset_formats ORDER BY name",
                cancellationToken: ct));
        return rows.Select(MapRow).ToList();
    }

    public async Task<AssetFormat?> GetByIdAsync(string formatId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<AssetFormatRow>(
            new CommandDefinition(
                "SELECT format_id, name, description, created_at FROM asset_formats WHERE format_id = @FormatId",
                new { FormatId = formatId },
                cancellationToken: ct));
        return row is null ? null : MapRow(row);
    }

    public async Task<AssetFormat> CreateAsync(string name, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString();
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO asset_formats (format_id, name) VALUES (@Id, @Name)",
                new { Id = id, Name = name },
                cancellationToken: ct));
        return (await GetByIdAsync(id, ct))!;
    }

    public async Task RenameAsync(string formatId, string newName, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "UPDATE asset_formats SET name = @Name WHERE format_id = @FormatId",
                new { Name = newName, FormatId = formatId },
                cancellationToken: ct));
    }

    public async Task DeleteAsync(string formatId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM asset_formats WHERE format_id = @FormatId",
                new { FormatId = formatId },
                cancellationToken: ct));
    }

    public async Task<int> CountAssetsAsync(string formatId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM assets WHERE format_id = @FormatId",
                new { FormatId = formatId },
                cancellationToken: ct));
    }

    private static AssetFormat MapRow(AssetFormatRow r) => new()
    {
        FormatId    = r.format_id,
        Name        = r.name,
        Description = r.description,
        CreatedAt   = r.created_at
    };

    private sealed record AssetFormatRow(
        string    format_id,
        string    name,
        string?   description,
        DateTime  created_at);
}
