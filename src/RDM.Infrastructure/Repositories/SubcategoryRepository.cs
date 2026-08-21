using Dapper;
using RDM.Core.Entities;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Repositories;

public sealed class SubcategoryRepository : ISubcategoryRepository
{
    private readonly IDbConnectionFactory _factory;

    public SubcategoryRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<Subcategory>> GetByFormatIdAsync(string formatId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<SubcategoryRow>(
            new CommandDefinition(
                """
                SELECT subcategory_id, format_id, name, sort_order, created_at
                FROM asset_subcategories
                WHERE format_id = @FormatId
                ORDER BY sort_order, name
                """,
                new { FormatId = formatId },
                cancellationToken: ct));
        return rows.Select(MapRow).ToList();
    }

    public async Task<Subcategory> CreateAsync(string formatId, string name, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString();
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO asset_subcategories (subcategory_id, format_id, name) VALUES (@Id, @FormatId, @Name)",
                new { Id = id, FormatId = formatId, Name = name },
                cancellationToken: ct));
        var row = await conn.QuerySingleAsync<SubcategoryRow>(
            new CommandDefinition(
                "SELECT subcategory_id, format_id, name, sort_order, created_at FROM asset_subcategories WHERE subcategory_id = @Id",
                new { Id = id },
                cancellationToken: ct));
        return MapRow(row);
    }

    public async Task RenameAsync(string subcategoryId, string newName, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "UPDATE asset_subcategories SET name = @Name WHERE subcategory_id = @Id",
                new { Name = newName, Id = subcategoryId },
                cancellationToken: ct));
    }

    public async Task DeleteAsync(string subcategoryId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM asset_subcategories WHERE subcategory_id = @Id",
                new { Id = subcategoryId },
                cancellationToken: ct));
    }

    public async Task<int> CountAssetsAsync(string subcategoryId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM assets WHERE subcategory_id = @Id",
                new { Id = subcategoryId },
                cancellationToken: ct));
    }

    private static Subcategory MapRow(SubcategoryRow r) => new()
    {
        SubcategoryId = r.subcategory_id,
        FormatId      = r.format_id,
        Name          = r.name,
        SortOrder     = r.sort_order,
        CreatedAt     = r.created_at
    };

    private sealed record SubcategoryRow(
        string   subcategory_id,
        string   format_id,
        string   name,
        int      sort_order,
        DateTime created_at);
}
