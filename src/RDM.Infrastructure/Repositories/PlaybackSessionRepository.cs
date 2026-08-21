using Dapper;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Infrastructure.Repositories.Helpers;

namespace RDM.Infrastructure.Repositories;

public sealed class PlaybackSessionRepository : IPlaybackSessionRepository
{
    private readonly IDbConnectionFactory _factory;

    public PlaybackSessionRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<PlaybackSession?> GetByStudioAsync(string studioId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<PlaybackSessionRow>(
            new CommandDefinition(
                """
                SELECT session_id, studio_id,
                       current_asset_id, current_position_ms, next_asset_id,
                       playlist_id, playlist_item_id,
                       state, mode, snapshot_at
                FROM playback_sessions
                WHERE studio_id = @StudioId
                """,
                new { StudioId = studioId },
                cancellationToken: ct));
        return row is null ? null : MapRow(row);
    }

    public async Task UpsertAsync(PlaybackSession session, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var affected = await conn.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE playback_sessions SET
                    session_id          = @SessionId,
                    current_asset_id    = @CurrentAssetId,
                    current_position_ms = @CurrentPositionMs,
                    next_asset_id       = @NextAssetId,
                    playlist_id         = @PlaylistId,
                    playlist_item_id    = @PlaylistItemId,
                    state               = @State,
                    mode                = @Mode,
                    snapshot_at         = @SnapshotAt
                WHERE studio_id = @StudioId
                """,
                BuildParams(session),
                cancellationToken: ct));

        if (affected == 0)
        {
            await conn.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO playback_sessions (
                        session_id, studio_id,
                        current_asset_id, current_position_ms, next_asset_id,
                        playlist_id, playlist_item_id,
                        state, mode, snapshot_at
                    ) VALUES (
                        @SessionId, @StudioId,
                        @CurrentAssetId, @CurrentPositionMs, @NextAssetId,
                        @PlaylistId, @PlaylistItemId,
                        @State, @Mode, @SnapshotAt
                    )
                    """,
                    BuildParams(session),
                    cancellationToken: ct));
        }
    }

    private static object BuildParams(PlaybackSession s) => new
    {
        s.SessionId,
        s.StudioId,
        s.CurrentAssetId,
        s.CurrentPositionMs,
        s.NextAssetId,
        s.PlaylistId,
        s.PlaylistItemId,
        State       = EnumMapper.ToDb(s.State),
        Mode        = EnumMapper.ToDb(s.Mode),
        s.SnapshotAt
    };

    private static PlaybackSession MapRow(PlaybackSessionRow r) => new()
    {
        SessionId          = r.session_id,
        StudioId           = r.studio_id,
        CurrentAssetId     = r.current_asset_id,
        CurrentPositionMs  = r.current_position_ms,
        NextAssetId        = r.next_asset_id,
        PlaylistId         = r.playlist_id,
        PlaylistItemId     = r.playlist_item_id,
        State              = EnumMapper.ToSessionState(r.state),
        Mode               = EnumMapper.ToPlaylistMode(r.mode),
        SnapshotAt         = r.snapshot_at
    };

    private sealed record PlaybackSessionRow(
        string   session_id,
        string   studio_id,
        string?  current_asset_id,
        uint     current_position_ms,
        string?  next_asset_id,
        string?  playlist_id,
        string?  playlist_item_id,
        string   state,
        string   mode,
        DateTime snapshot_at);
}
