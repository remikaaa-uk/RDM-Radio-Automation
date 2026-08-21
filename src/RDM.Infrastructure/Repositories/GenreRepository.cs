using Dapper;
using RDM.Core.Entities;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Repositories;

public sealed class GenreRepository : IGenreRepository
{
    private readonly IDbConnectionFactory _factory;

    public GenreRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<Genre>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<GenreRow>(
            new CommandDefinition(
                "SELECT genre_id, name, sort_order, created_at FROM genres ORDER BY sort_order, name",
                cancellationToken: ct));
        return rows.Select(MapRow).ToList();
    }

    public async Task<Genre> CreateAsync(string name, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString();
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "INSERT INTO genres (genre_id, name) VALUES (@Id, @Name)",
                new { Id = id, Name = name },
                cancellationToken: ct));
        var row = await conn.QuerySingleAsync<GenreRow>(
            new CommandDefinition(
                "SELECT genre_id, name, sort_order, created_at FROM genres WHERE genre_id = @Id",
                new { Id = id },
                cancellationToken: ct));
        return MapRow(row);
    }

    public async Task RenameAsync(string genreId, string newName, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "UPDATE genres SET name = @Name WHERE genre_id = @Id",
                new { Name = newName, Id = genreId },
                cancellationToken: ct));
    }

    public async Task DeleteAsync(string genreId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM genres WHERE genre_id = @Id",
                new { Id = genreId },
                cancellationToken: ct));
    }

    private static Genre MapRow(GenreRow r) => new()
    {
        GenreId   = r.genre_id,
        Name      = r.name,
        SortOrder = r.sort_order,
        CreatedAt = r.created_at
    };

    private sealed record GenreRow(
        string   genre_id,
        string   name,
        int      sort_order,
        DateTime created_at);
}
