using Dapper;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Infrastructure.Repositories.Helpers;

namespace RDM.Infrastructure.Repositories;

public sealed class PlaylistRepository : IPlaylistRepository
{
    private readonly IDbConnectionFactory _factory;

    public PlaylistRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<Playlist?> GetByIdAsync(string playlistId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<PlaylistRow>(
            new CommandDefinition(
                """
                SELECT playlist_id, studio_id, name, playlist_type, created_at
                FROM playlists
                WHERE playlist_id = @PlaylistId
                """,
                new { PlaylistId = playlistId },
                cancellationToken: ct));
        return row is null ? null : MapRow(row);
    }

    public async Task<IReadOnlyList<Playlist>> GetByStudioAsync(string studioId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<PlaylistRow>(
            new CommandDefinition(
                """
                SELECT playlist_id, studio_id, name, playlist_type, created_at
                FROM playlists
                WHERE studio_id = @StudioId
                  AND playlist_type = 'SAVED'
                ORDER BY created_at
                """,
                new { StudioId = studioId },
                cancellationToken: ct));
        return rows.Select(MapRow).ToList();
    }

    public async Task<Playlist?> GetOnAirPlaylistAsync(string studioId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<PlaylistRow>(
            new CommandDefinition(
                """
                SELECT playlist_id, studio_id, name, playlist_type, created_at
                FROM playlists
                WHERE studio_id = @StudioId
                  AND playlist_type = 'ON_AIR'
                LIMIT 1
                """,
                new { StudioId = studioId },
                cancellationToken: ct));
        return row is null ? null : MapRow(row);
    }

    public async Task CreateAsync(Playlist playlist, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO playlists (playlist_id, studio_id, name, playlist_type, created_at)
                VALUES (@PlaylistId, @StudioId, @Name, @PlaylistType, @CreatedAt)
                """,
                new
                {
                    playlist.PlaylistId,
                    playlist.StudioId,
                    playlist.Name,
                    PlaylistType = EnumMapper.ToDb(playlist.PlaylistType),
                    playlist.CreatedAt
                },
                cancellationToken: ct));
    }

    public async Task DeleteAsync(string playlistId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM playlists WHERE playlist_id = @PlaylistId",
                new { PlaylistId = playlistId },
                cancellationToken: ct));
    }

    public async Task UpdateNameAsync(string playlistId, string name, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE playlists SET name = @Name WHERE playlist_id = @PlaylistId",
            new { PlaylistId = playlistId, Name = name },
            cancellationToken: ct));
    }

    public async Task ClearItemsAsync(string playlistId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM playlist_items WHERE playlist_id = @PlaylistId",
            new { PlaylistId = playlistId },
            cancellationToken: ct));
    }

    public async Task AddItemAsync(PlaylistItem item, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO playlist_items (
                    item_id, playlist_id, asset_id, position, item_type,
                    external_file_path, dummy_label, dummy_note, dummy_duration_ms,
                    crossfade_ms, lead_in_ms, trim_start_ms, trim_end_ms,
                    segue_type, scheduled_at, auto_link_next, volume_envelope
                ) VALUES (
                    @ItemId, @PlaylistId, @AssetId, @Position, @ItemType,
                    @ExternalFilePath, @DummyLabel, @DummyNote, @DummyDurationMs,
                    @CrossfadeMs, @LeadInMs, @TrimStartMs, @TrimEndMs,
                    @SegueType, @ScheduledAt, @AutoLinkNext, @VolumeEnvelope
                )
                """,
                new
                {
                    item.ItemId,
                    item.PlaylistId,
                    item.AssetId,
                    item.Position,
                    ItemType          = EnumMapper.ToDb(item.ItemType),
                    item.ExternalFilePath,
                    item.DummyLabel,
                    item.DummyNote,
                    item.DummyDurationMs,
                    item.CrossfadeMs,
                    item.LeadInMs,
                    item.TrimStartMs,
                    item.TrimEndMs,
                    SegueType         = EnumMapper.ToDb(item.SegueType),
                    item.ScheduledAt,
                    AutoLinkNext      = item.AutoLinkNext ? 1 : 0,
                    item.VolumeEnvelope
                },
                cancellationToken: ct));
    }

    public async Task UpdateItemAsync(PlaylistItem item, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE playlist_items SET
                    asset_id           = @AssetId,
                    position           = @Position,
                    item_type          = @ItemType,
                    external_file_path = @ExternalFilePath,
                    dummy_label        = @DummyLabel,
                    dummy_note         = @DummyNote,
                    dummy_duration_ms  = @DummyDurationMs,
                    crossfade_ms       = @CrossfadeMs,
                    lead_in_ms         = @LeadInMs,
                    trim_start_ms      = @TrimStartMs,
                    trim_end_ms        = @TrimEndMs,
                    segue_type         = @SegueType,
                    scheduled_at       = @ScheduledAt,
                    auto_link_next     = @AutoLinkNext,
                    volume_envelope    = @VolumeEnvelope
                WHERE item_id = @ItemId
                """,
                new
                {
                    item.ItemId,
                    item.AssetId,
                    item.Position,
                    ItemType          = EnumMapper.ToDb(item.ItemType),
                    item.ExternalFilePath,
                    item.DummyLabel,
                    item.DummyNote,
                    item.DummyDurationMs,
                    item.CrossfadeMs,
                    item.LeadInMs,
                    item.TrimStartMs,
                    item.TrimEndMs,
                    SegueType         = EnumMapper.ToDb(item.SegueType),
                    item.ScheduledAt,
                    AutoLinkNext      = item.AutoLinkNext ? 1 : 0,
                    item.VolumeEnvelope
                },
                cancellationToken: ct));
    }

    public async Task RemoveItemAsync(string itemId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM playlist_items WHERE item_id = @ItemId",
                new { ItemId = itemId },
                cancellationToken: ct));
    }

    private static Playlist MapRow(PlaylistRow r) => new()
    {
        PlaylistId   = r.playlist_id,
        StudioId     = r.studio_id,
        Name         = r.name,
        PlaylistType = EnumMapper.ToPlaylistType(r.playlist_type),
        CreatedAt    = r.created_at
    };

    private static PlaylistItem MapItemRow(PlaylistItemRow r) => new()
    {
        ItemId            = r.item_id,
        PlaylistId        = r.playlist_id,
        AssetId           = r.asset_id,
        Position          = r.position,
        ItemType          = EnumMapper.ToPlaylistItemType(r.item_type),
        ExternalFilePath  = r.external_file_path,
        DummyLabel        = r.dummy_label,
        DummyNote         = r.dummy_note,
        DummyDurationMs   = r.dummy_duration_ms,
        CrossfadeMs       = r.crossfade_ms,
        LeadInMs          = r.lead_in_ms,
        TrimStartMs       = r.trim_start_ms,
        TrimEndMs         = r.trim_end_ms,
        SegueType         = EnumMapper.ToSegueType(r.segue_type),
        ScheduledAt       = r.scheduled_at,
        AutoLinkNext      = r.auto_link_next,
        VolumeEnvelope    = r.volume_envelope
    };

    public async Task<IReadOnlyList<PlaylistItem>> GetItemsAsync(string playlistId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<PlaylistItemRow>(
            new CommandDefinition(
                """
                SELECT item_id, playlist_id, asset_id, position, item_type,
                       external_file_path, dummy_label, dummy_note, dummy_duration_ms,
                       crossfade_ms, lead_in_ms, trim_start_ms, trim_end_ms,
                       segue_type, scheduled_at, auto_link_next, volume_envelope
                FROM playlist_items
                WHERE playlist_id = @PlaylistId
                ORDER BY position
                """,
                new { PlaylistId = playlistId },
                cancellationToken: ct));
        return rows.Select(MapItemRow).ToList();
    }

    private sealed record PlaylistRow(
        string   playlist_id,
        string   studio_id,
        string   name,
        string   playlist_type,
        DateTime created_at);

    private sealed record PlaylistItemRow(
        string    item_id,
        string    playlist_id,
        string?   asset_id,
        uint      position,
        string    item_type,
        string?   external_file_path,
        string?   dummy_label,
        string?   dummy_note,
        uint?     dummy_duration_ms,
        uint?     crossfade_ms,
        int?      lead_in_ms,
        uint?     trim_start_ms,
        uint?     trim_end_ms,
        string    segue_type,
        DateTime? scheduled_at,
        bool      auto_link_next,
        string?   volume_envelope);
}
