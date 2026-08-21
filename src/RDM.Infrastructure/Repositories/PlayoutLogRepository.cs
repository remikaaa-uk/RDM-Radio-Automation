using Dapper;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Infrastructure.Repositories.Helpers;

namespace RDM.Infrastructure.Repositories;

public sealed class PlayoutLogRepository : IPlayoutLogRepository
{
    private readonly IDbConnectionFactory _factory;

    public PlayoutLogRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task CreateAsync(PlayoutLog entry, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO playout_log (log_id, studio_id, asset_id, temp_title, temp_artist,
                                         started_at, ended_at, source_type)
                VALUES (@LogId, @StudioId, @AssetId, @TempTitle, @TempArtist,
                        @StartedAt, @EndedAt, @SourceType)
                """,
                new
                {
                    entry.LogId,
                    entry.StudioId,
                    entry.AssetId,
                    entry.TempTitle,
                    entry.TempArtist,
                    entry.StartedAt,
                    entry.EndedAt,
                    SourceType = EnumMapper.ToDb(entry.SourceType)
                },
                cancellationToken: ct));
    }

    public async Task UpdateEndedAtAsync(string logId, DateTime endedAt, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "UPDATE playout_log SET ended_at = @EndedAt WHERE log_id = @LogId",
                new { LogId = logId, EndedAt = endedAt },
                cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PlayoutLog>> GetByStudioAsync(
        string studioId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<PlayoutLogRow>(
            new CommandDefinition(
                """
                SELECT log_id, studio_id, asset_id, temp_title, temp_artist,
                       started_at, ended_at, source_type
                FROM playout_log
                WHERE studio_id = @StudioId
                  AND started_at BETWEEN @From AND @To
                ORDER BY started_at DESC
                """,
                new { StudioId = studioId, From = from, To = to },
                cancellationToken: ct));
        return rows.Select(MapRow).ToList();
    }

    public async Task<IReadOnlyList<PlayoutLog>> GetRecentAsync(
        string studioId, int limit, int offset = 0, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<PlayoutLogRow>(
            new CommandDefinition(
                """
                SELECT log_id, studio_id, asset_id, temp_title, temp_artist,
                       started_at, ended_at, source_type
                FROM playout_log
                WHERE studio_id = @StudioId
                ORDER BY started_at DESC
                LIMIT @Limit OFFSET @Offset
                """,
                new { StudioId = studioId, Limit = limit, Offset = offset },
                cancellationToken: ct));
        return rows.Select(MapRow).ToList();
    }

    public async Task<int> CountAsync(string studioId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM playout_log WHERE studio_id = @StudioId",
                new { StudioId = studioId },
                cancellationToken: ct));
    }

    private static PlayoutLog MapRow(PlayoutLogRow r) => new()
    {
        LogId      = r.log_id,
        StudioId   = r.studio_id,
        AssetId    = r.asset_id,
        TempTitle  = r.temp_title,
        TempArtist = r.temp_artist,
        StartedAt  = r.started_at,
        EndedAt    = r.ended_at,
        SourceType = EnumMapper.ToSourceType(r.source_type)
    };

    private sealed record PlayoutLogRow(
        string    log_id,
        string    studio_id,
        string?   asset_id,
        string?   temp_title,
        string?   temp_artist,
        DateTime  started_at,
        DateTime? ended_at,
        string    source_type);
}
