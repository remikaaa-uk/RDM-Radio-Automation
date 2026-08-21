using System.Data;
using System.Text.Json;
using Dapper;
using RDM.Core;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Infrastructure.Repositories.Helpers;
using RDM.Shared.DTOs;
using RDM.Shared.Enums;

namespace RDM.Infrastructure.Repositories;

public sealed class AssetRepository : IAssetRepository
{
    private readonly IDbConnectionFactory _factory;

    public AssetRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<(IReadOnlyList<Asset> Items, int Total)> SearchAsync(
        AssetSearchParams searchParams, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        
        var whereClauses = new List<string>();
        var parameters = new DynamicParameters();

        if (!string.IsNullOrWhiteSpace(searchParams.Query))
        {
            whereClauses.Add("(a.title LIKE @Query OR a.artist LIKE @Query)");
            parameters.Add("Query", $"%{searchParams.Query}%");
        }

        if (searchParams.AssetType.HasValue)
        {
            whereClauses.Add("a.asset_type = @AssetType");
            parameters.Add("AssetType", EnumMapper.ToDb(searchParams.AssetType.Value));
        }

        if (!string.IsNullOrWhiteSpace(searchParams.FormatId))
        {
            whereClauses.Add("a.format_id = @FormatId");
            parameters.Add("FormatId", searchParams.FormatId);
        }

        if (searchParams.Status.HasValue)
        {
            whereClauses.Add("a.status = @Status");
            parameters.Add("Status", EnumMapper.ToDb(searchParams.Status.Value));
        }

        if (!string.IsNullOrWhiteSpace(searchParams.Genre))
        {
            whereClauses.Add("a.genre = @Genre");
            parameters.Add("Genre", searchParams.Genre);
        }

        if (!string.IsNullOrWhiteSpace(searchParams.SubcategoryId))
        {
            whereClauses.Add("a.subcategory_id = @SubcategoryId");
            parameters.Add("SubcategoryId", searchParams.SubcategoryId);
        }

        var whereSql  = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : string.Empty;
        var orderBySql = BuildOrderBy(searchParams.SortColumn, searchParams.SortAscending);

        var countSql = $"SELECT COUNT(*) FROM assets a LEFT JOIN asset_formats af ON af.format_id = a.format_id {whereSql}";
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(countSql, parameters, cancellationToken: ct));

        var selectSql = $"""
            SELECT a.asset_id, a.asset_type, a.format_id, af.name AS format_name,
                   a.subcategory_id, sc.name AS subcategory_name, a.title, a.artist, a.album,
                   a.duration_ms, a.checksum,
                   a.rdm_file_path, a.stream_url, a.image_path, a.bpm, a.year, a.rating, a.mood,
                   a.gender, a.language, a.genre, a.comments, a.is_damaged, a.is_variable_duration, a.status,
                   a.start_date, a.end_date, a.play_limit, a.play_count,
                   a.last_played_at, a.loudness_lufs, a.loudness_peak,
                   a.created_at, a.updated_at,
                   a.cue_start, a.cue_intro, a.cue_ramp2, a.cue_ramp3, a.cue_outro,
                   a.cue_start_next, a.cue_fade_out, a.cue_fade_end, a.cue_end,
                   a.cue_hook_in, a.cue_hook_fade, a.cue_hook_out,
                   a.cue_loop_in, a.cue_loop_out, a.cue_anchor
            FROM assets a
            LEFT JOIN asset_formats af ON af.format_id = a.format_id
            LEFT JOIN asset_subcategories sc ON sc.subcategory_id = a.subcategory_id
            {whereSql}
            ORDER BY {orderBySql}
            LIMIT @Limit OFFSET @Offset
            """;
            
        parameters.Add("Limit", searchParams.Limit);
        parameters.Add("Offset", searchParams.Offset);

        var rows = await conn.QueryAsync<AssetRow>(new CommandDefinition(selectSql, parameters, cancellationToken: ct));
        var items = rows.Select(MapRow).ToList();

        return (items, total);
    }

    private static string BuildOrderBy(string? col, bool asc)
    {
        var d = asc ? "ASC" : "DESC";
        return col switch
        {
            "Artist"     => $"a.artist {d}, a.title ASC",
            "Title"      => $"a.title {d}",
            "Album"      => $"a.album {d}, a.artist ASC",
            "IntroMs"    => $"a.cue_intro {d}",
            "DurationMs" => $"a.duration_ms {d}",
            "Year"       => $"a.year {d}",
            "PlayCount"  => $"a.play_count {d}",
            "Status"     => $"a.status {d}",
            "Mood"       => $"a.mood {d}",
            "Gender"     => $"a.gender {d}",
            "Language"   => $"a.language {d}",
            "Rating"     => $"a.rating {d}",
            "CreatedAt"  => $"a.created_at {d}",
            "AssetType"  => $"a.asset_type {d}",
            "Bpm"        => $"a.bpm {d}",
            "FormatName" => $"af.name {d}, a.artist ASC",
            _            => "a.artist ASC, a.title ASC"
        };
    }

    public async Task<Asset?> GetByIdAsync(string assetId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<AssetRow>(
            new CommandDefinition(
                """
                SELECT a.asset_id, a.asset_type, a.format_id, af.name AS format_name,
                       a.subcategory_id, sc.name AS subcategory_name, a.title, a.artist, a.album,
                       a.duration_ms, a.checksum,
                       a.rdm_file_path, a.stream_url, a.image_path, a.bpm, a.year, a.rating, a.mood,
                       a.gender, a.language, a.genre, a.comments, a.is_damaged, a.is_variable_duration, a.status,
                       a.start_date, a.end_date, a.play_limit, a.play_count,
                       a.last_played_at, a.loudness_lufs, a.loudness_peak,
                       a.created_at, a.updated_at,
                       a.cue_start, a.cue_intro, a.cue_ramp2, a.cue_ramp3, a.cue_outro,
                       a.cue_start_next, a.cue_fade_out, a.cue_fade_end, a.cue_end,
                       a.cue_hook_in, a.cue_hook_fade, a.cue_hook_out,
                       a.cue_loop_in, a.cue_loop_out, a.cue_anchor
                FROM assets a
                LEFT JOIN asset_formats af ON af.format_id = a.format_id
                LEFT JOIN asset_subcategories sc ON sc.subcategory_id = a.subcategory_id
                WHERE a.asset_id = @AssetId
                """,
                new { AssetId = assetId },
                cancellationToken: ct));
        return row is null ? null : MapRow(row);
    }

    public async Task<Asset?> FindByFilePathAsync(string filePath, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<AssetRow>(
            new CommandDefinition(
                """
                SELECT a.asset_id, a.asset_type, a.format_id, af.name AS format_name,
                       a.subcategory_id, a.title, a.artist, a.album,
                       a.duration_ms, a.checksum,
                       a.rdm_file_path, a.stream_url, a.image_path, a.bpm, a.year, a.rating, a.mood,
                       a.gender, a.language, a.genre, a.comments, a.is_damaged, a.is_variable_duration, a.status,
                       a.start_date, a.end_date, a.play_limit, a.play_count,
                       a.last_played_at, a.loudness_lufs, a.loudness_peak,
                       a.created_at, a.updated_at,
                       a.cue_start, a.cue_intro, a.cue_ramp2, a.cue_ramp3, a.cue_outro,
                       a.cue_start_next, a.cue_fade_out, a.cue_fade_end, a.cue_end,
                       a.cue_hook_in, a.cue_hook_fade, a.cue_hook_out,
                       a.cue_loop_in, a.cue_loop_out, a.cue_anchor
                FROM assets a
                LEFT JOIN asset_formats af ON af.format_id = a.format_id
                WHERE a.rdm_file_path = @FilePath
                """,
                new { FilePath = filePath },
                cancellationToken: ct));
        return row is null ? null : MapRow(row);
    }

    public async Task<Asset?> GetByChecksumAsync(string checksum, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<AssetRow>(
            new CommandDefinition(
                """
                SELECT asset_id, asset_type, format_id, title, artist, album,
                       duration_ms, checksum,
                       rdm_file_path, image_path, bpm, year, rating, mood,
                       gender, language, genre, comments, is_damaged, is_variable_duration, status,
                       start_date, end_date, play_limit, play_count,
                       last_played_at, loudness_lufs, loudness_peak,
                       created_at, updated_at
                FROM assets
                WHERE checksum = @Checksum
                """,
                new { Checksum = checksum },
                cancellationToken: ct));
        return row is null ? null : MapRow(row);
    }

    public async Task<IReadOnlyList<Asset>> GetByFormatAsync(
        string formatId, AssetStatus? status = null, CancellationToken ct = default)
    {
        var statusFilter = status is null ? string.Empty : " AND status = @Status";
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<AssetRow>(
            new CommandDefinition(
                $"""
                SELECT asset_id, asset_type, format_id, title, artist, album,
                       duration_ms, checksum,
                       rdm_file_path, image_path, bpm, year, rating, mood,
                       gender, language, genre, comments, is_damaged, is_variable_duration, status,
                       start_date, end_date, play_limit, play_count,
                       last_played_at, loudness_lufs, loudness_peak,
                       created_at, updated_at
                FROM assets
                WHERE format_id = @FormatId{statusFilter}
                """,
                new
                {
                    FormatId = formatId,
                    Status   = status.HasValue ? EnumMapper.ToDb(status.Value) : null
                },
                cancellationToken: ct));
        return rows.Select(MapRow).ToList();
    }

    public async Task CreateAsync(Asset asset, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        try
        {
            await conn.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO assets (
                        asset_id, asset_type, format_id, subcategory_id, title, artist, album,
                        duration_ms, checksum,
                        rdm_file_path, stream_url, image_path, bpm, year, rating, mood,
                        gender, language, genre, comments, is_damaged, is_variable_duration, status,
                        start_date, end_date, play_limit, play_count,
                        last_played_at, loudness_lufs, loudness_peak,
                        cue_start, cue_intro, cue_ramp2, cue_ramp3, cue_outro,
                        cue_start_next, cue_fade_out, cue_fade_end, cue_end,
                        cue_hook_in, cue_hook_fade, cue_hook_out,
                        cue_loop_in, cue_loop_out, cue_anchor,
                        created_at, updated_at
                    ) VALUES (
                        @AssetId, @AssetType, @FormatId, @SubcategoryId, @Title, @Artist, @Album,
                        @DurationMs, @Checksum,
                        @RdmFilePath, @StreamUrl, @ImagePath, @Bpm, @Year, @Rating, @Mood,
                        @Gender, @Language, @Genre, @Comments, @IsDamaged, @IsVariableDuration, @Status,
                        @StartDate, @EndDate, @PlayLimit, @PlayCount,
                        @LastPlayedAt, @LoudnessLufs, @LoudnessPeak,
                        @CueStart, @CueIntro, @CueRamp2, @CueRamp3, @CueOutro,
                        @CueStartNext, @CueFadeOut, @CueFadeEnd, @CueEnd,
                        @CueHookIn, @CueHookFade, @CueHookOut,
                        @CueLoopIn, @CueLoopOut, @CueAnchor,
                        @CreatedAt, @UpdatedAt
                    )
                    """,
                    new
                    {
                        asset.AssetId,
                        AssetType     = EnumMapper.ToDb(asset.AssetType),
                        asset.FormatId,
                        asset.SubcategoryId,
                        asset.Title,
                        asset.Artist,
                        asset.Album,
                        asset.DurationMs,
                        asset.Checksum,
                        asset.RdmFilePath,
                        asset.StreamUrl,
                        asset.ImagePath,
                        asset.Bpm,
                        asset.Year,
                        asset.Rating,
                        asset.Mood,
                        asset.Gender,
                        asset.Language,
                        asset.Genre,
                        asset.Comments,
                        IsDamaged  = asset.IsDamaged ? 1 : 0,
                        IsVariableDuration = asset.IsVariableDuration ? 1 : 0,
                        Status     = EnumMapper.ToDb(asset.Status),
                        asset.StartDate,
                        asset.EndDate,
                        asset.PlayLimit,
                        asset.PlayCount,
                        asset.LastPlayedAt,
                        asset.LoudnessLufs,
                        asset.LoudnessPeak,
                        asset.CueStart,
                        asset.CueIntro,
                        asset.CueRamp2,
                        asset.CueRamp3,
                        asset.CueOutro,
                        asset.CueStartNext,
                        asset.CueFadeOut,
                        asset.CueFadeEnd,
                        asset.CueEnd,
                        asset.CueHookIn,
                        asset.CueHookFade,
                        asset.CueHookOut,
                        asset.CueLoopIn,
                        asset.CueLoopOut,
                        asset.CueAnchor,
                        asset.CreatedAt,
                        asset.UpdatedAt
                    },
                    cancellationToken: ct));
        }
        catch (MySqlConnector.MySqlException ex) when (ex.Number == 1062)
        {
            throw new DuplicateAssetException();
        }
    }

    public async Task UpdateAsync(Asset asset, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE assets SET
                    asset_type     = @AssetType,
                    format_id      = @FormatId,
                    subcategory_id = @SubcategoryId,
                    title          = @Title,
                    artist         = @Artist,
                    album          = @Album,
                    duration_ms    = @DurationMs,
                    rdm_file_path  = @RdmFilePath,
                    stream_url     = @StreamUrl,
                    image_path     = @ImagePath,
                    bpm            = @Bpm,
                    year           = @Year,
                    rating         = @Rating,
                    mood           = @Mood,
                    gender         = @Gender,
                    language       = @Language,
                    genre          = @Genre,
                    comments       = @Comments,
                    is_damaged     = @IsDamaged,
                    is_variable_duration = @IsVariableDuration,
                    status         = @Status,
                    start_date     = @StartDate,
                    end_date       = @EndDate,
                    play_limit     = @PlayLimit,
                    play_count     = @PlayCount,
                    last_played_at = @LastPlayedAt,
                    loudness_lufs  = @LoudnessLufs,
                    loudness_peak  = @LoudnessPeak
                WHERE asset_id = @AssetId
                """,
                new
                {
                    asset.AssetId,
                    AssetType      = EnumMapper.ToDb(asset.AssetType),
                    asset.FormatId,
                    asset.SubcategoryId,
                    asset.Title,
                    asset.Artist,
                    asset.Album,
                    asset.DurationMs,
                    asset.RdmFilePath,
                    asset.StreamUrl,
                    asset.ImagePath,
                    asset.Bpm,
                    asset.Year,
                    asset.Rating,
                    asset.Mood,
                    asset.Gender,
                    asset.Language,
                    asset.Genre,
                    asset.Comments,
                    IsDamaged      = asset.IsDamaged ? 1 : 0,
                    IsVariableDuration = asset.IsVariableDuration ? 1 : 0,
                    Status         = EnumMapper.ToDb(asset.Status),
                    asset.StartDate,
                    asset.EndDate,
                    asset.PlayLimit,
                    asset.PlayCount,
                    asset.LastPlayedAt,
                    asset.LoudnessLufs,
                    asset.LoudnessPeak
                },
                cancellationToken: ct));
    }

    public async Task UpdateCueMarkersAsync(
        string assetId, CueMarkersDto m, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE assets SET
                    cue_start      = @CueStart,
                    cue_intro      = @CueIntro,
                    cue_ramp2      = @CueRamp2,
                    cue_ramp3      = @CueRamp3,
                    cue_outro      = @CueOutro,
                    cue_start_next = @CueStartNext,
                    cue_fade_out   = @CueFadeOut,
                    cue_fade_end   = @CueFadeEnd,
                    cue_end        = @CueEnd,
                    cue_hook_in    = @CueHookIn,
                    cue_hook_fade  = @CueHookFade,
                    cue_hook_out   = @CueHookOut,
                    cue_loop_in    = @CueLoopIn,
                    cue_loop_out   = @CueLoopOut,
                    cue_anchor     = @CueAnchor
                WHERE asset_id = @AssetId
                """,
                new
                {
                    AssetId      = assetId,
                    CueStart     = m.Start,
                    CueIntro     = m.Intro,
                    CueRamp2     = m.Ramp2,
                    CueRamp3     = m.Ramp3,
                    CueOutro     = m.Outro,
                    CueStartNext = m.StartNext,
                    CueFadeOut   = m.FadeOut,
                    CueFadeEnd   = m.FadeEnd,
                    CueEnd       = m.End,
                    CueHookIn    = m.HookIn,
                    CueHookFade  = m.HookFade,
                    CueHookOut   = m.HookOut,
                    CueLoopIn    = m.LoopIn,
                    CueLoopOut   = m.LoopOut,
                    CueAnchor    = m.Anchor
                },
                cancellationToken: ct));
    }

    public async Task UpdateStatusAsync(string assetId, AssetStatus status, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "UPDATE assets SET status = @Status WHERE asset_id = @AssetId",
                new { AssetId = assetId, Status = EnumMapper.ToDb(status) },
                cancellationToken: ct));
    }

    public async Task UpdatePlayCountAsync(
        string assetId, uint playCount, DateTime lastPlayedAt, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE assets
                SET play_count = @PlayCount, last_played_at = @LastPlayedAt
                WHERE asset_id = @AssetId
                """,
                new { AssetId = assetId, PlayCount = playCount, LastPlayedAt = lastPlayedAt },
                cancellationToken: ct));
    }

    public async Task<int> UpdateLoudnessAsync(
        string assetId, decimal? loudnessLufs, decimal? loudnessPeak, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE assets
                SET loudness_lufs = @LoudnessLufs, loudness_peak = @LoudnessPeak
                WHERE asset_id = @AssetId
                """,
                new { AssetId = assetId, LoudnessLufs = loudnessLufs, LoudnessPeak = loudnessPeak },
                cancellationToken: ct));
    }

    public async Task<int> UpdateBpmAsync(string assetId, decimal bpm, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE assets SET bpm = @Bpm WHERE asset_id = @AssetId
                """,
                new { AssetId = assetId, Bpm = bpm },
                cancellationToken: ct));
    }

    public async Task<int> UpdateDurationAsync(string assetId, uint durationMs, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        return await conn.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE assets SET duration_ms = @DurationMs WHERE asset_id = @AssetId
                """,
                new { AssetId = assetId, DurationMs = durationMs },
                cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AssetWaveformInfo>> GetAllForWaveformScanAsync(CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<WaveformScanRow>(
            new CommandDefinition(
                "SELECT asset_id, rdm_file_path FROM assets WHERE status = 'ACTIVE'",
                cancellationToken: ct));
        return rows.Select(r => new AssetWaveformInfo(r.asset_id, r.rdm_file_path))
                   .ToList();
    }

    private sealed class WaveformScanRow
    {
        public string  asset_id      { get; set; } = "";
        public string? rdm_file_path { get; set; }
    }

    public async Task DeleteAsync(string assetId, CancellationToken ct = default)
    {
        using var conn = await _factory.CreateOpenConnectionAsync(ct);
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM playlist_items WHERE asset_id = @AssetId",
                new { AssetId = assetId },
                transaction: tx,
                cancellationToken: ct));
        await CleanScheduledEventsActionsAsync(conn, tx, new HashSet<string> { assetId }, ct);
        await conn.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM assets WHERE asset_id = @AssetId",
                new { AssetId = assetId },
                transaction: tx,
                cancellationToken: ct));
        tx.Commit();
    }

    public async Task<IReadOnlyList<string?>> GetFilePathsByIdsAsync(
        IReadOnlyList<string> assetIds, CancellationToken ct = default)
    {
        if (assetIds.Count == 0) return [];
        using var conn = _factory.CreateConnection();
        var paths = await conn.QueryAsync<string?>(
            new CommandDefinition(
                "SELECT rdm_file_path FROM assets WHERE asset_id IN @AssetIds",
                new { AssetIds = assetIds },
                cancellationToken: ct));
        return paths.ToList();
    }

    public async Task<int> BatchDeleteAsync(IReadOnlyList<string> assetIds, CancellationToken ct = default)
    {
        if (assetIds.Count == 0) return 0;
        using var conn = await _factory.CreateOpenConnectionAsync(ct);
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM playlist_items WHERE asset_id IN @AssetIds",
                new { AssetIds = assetIds },
                transaction: tx,
                cancellationToken: ct));
        await CleanScheduledEventsActionsAsync(conn, tx, new HashSet<string>(assetIds), ct);
        var deleted = await conn.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM assets WHERE asset_id IN @AssetIds",
                new { AssetIds = assetIds },
                transaction: tx,
                cancellationToken: ct));
        tx.Commit();
        return deleted;
    }

    public async Task<IReadOnlyList<(string AssetId, string? RdmFilePath, string Title)>> GetAllFilePathsAsync(
        CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<FilePathRow>(
            new CommandDefinition(
                "SELECT asset_id, rdm_file_path, title FROM assets",
                cancellationToken: ct));
        return rows.Select(r => (r.asset_id, r.rdm_file_path, r.title)).ToList();
    }

    public async Task OptimizeDatabaseAsync(CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "OPTIMIZE TABLE assets, playlist_items, playout_log",
                cancellationToken: ct,
                commandTimeout: 300));
    }

    private static async Task CleanScheduledEventsActionsAsync(
        IDbConnection conn, IDbTransaction tx, HashSet<string> assetIds, CancellationToken ct)
    {
        var events = await conn.QueryAsync<ScheduledEventRow>(
            new CommandDefinition(
                """
                SELECT event_id, actions
                FROM scheduled_events
                WHERE JSON_SEARCH(actions, 'one', 'PLAY_FILE', NULL, '$[*].type') IS NOT NULL
                """,
                transaction: tx,
                cancellationToken: ct));

        foreach (var row in events)
        {
            JsonElement[] all;
            try { all = JsonSerializer.Deserialize<JsonElement[]>(row.actions) ?? []; }
            catch { continue; }

            var filtered = all.Where(a =>
                !(a.TryGetProperty("type", out var t) && t.GetString() == "PLAY_FILE" &&
                  a.TryGetProperty("payload", out var p) &&
                  p.TryGetProperty("asset_id", out var aid) &&
                  assetIds.Contains(aid.GetString() ?? ""))).ToArray();

            if (filtered.Length == all.Length) continue;

            await conn.ExecuteAsync(
                new CommandDefinition(
                    "UPDATE scheduled_events SET actions = @Actions WHERE event_id = @EventId",
                    new { Actions = JsonSerializer.Serialize(filtered), EventId = row.event_id },
                    transaction: tx,
                    cancellationToken: ct));
        }
    }

    private sealed class ScheduledEventRow
    {
        public string event_id { get; set; } = "";
        public string actions  { get; set; } = "";
    }

    private sealed class FilePathRow
    {
        public string  asset_id      { get; set; } = "";
        public string? rdm_file_path { get; set; }
        public string  title         { get; set; } = "";
    }

    private static Asset MapRow(AssetRow r) => new()
    {
        AssetId        = r.asset_id,
        AssetType      = EnumMapper.ToAssetType(r.asset_type),
        FormatId        = r.format_id,
        FormatName      = r.format_name,
        SubcategoryId   = r.subcategory_id,
        SubcategoryName = r.subcategory_name,
        Title          = r.title ?? string.Empty,
        Artist         = r.artist,
        Album          = r.album,
        DurationMs     = r.duration_ms,
        Checksum       = r.checksum,
        RdmFilePath    = r.rdm_file_path,
        StreamUrl      = r.stream_url,
        ImagePath      = r.image_path,
        Bpm            = r.bpm,
        Year           = r.year,
        Rating         = r.rating.HasValue ? (byte)r.rating.Value : null,
        Mood           = r.mood,
        Gender         = r.gender,
        Language       = r.language,
        Genre          = r.genre,
        Comments       = r.comments,
        IsDamaged      = r.is_damaged,
        IsVariableDuration = r.is_variable_duration,
        Status         = EnumMapper.ToAssetStatus(r.status),
        StartDate      = r.start_date,
        EndDate        = r.end_date,
        PlayLimit      = r.play_limit,
        PlayCount      = r.play_count,
        LastPlayedAt   = r.last_played_at,
        LoudnessLufs   = r.loudness_lufs,
        LoudnessPeak   = r.loudness_peak,
        CreatedAt      = r.created_at,
        UpdatedAt      = r.updated_at,
        CueStart       = r.cue_start,
        CueIntro       = r.cue_intro,
        CueRamp2       = r.cue_ramp2,
        CueRamp3       = r.cue_ramp3,
        CueOutro       = r.cue_outro,
        CueStartNext   = r.cue_start_next,
        CueFadeOut     = r.cue_fade_out,
        CueFadeEnd     = r.cue_fade_end,
        CueEnd         = r.cue_end,
        CueHookIn      = r.cue_hook_in,
        CueHookFade    = r.cue_hook_fade,
        CueHookOut     = r.cue_hook_out,
        CueLoopIn      = r.cue_loop_in,
        CueLoopOut     = r.cue_loop_out,
        CueAnchor      = r.cue_anchor,
    };

    private sealed class AssetRow
    {
        public string    asset_id        { get; set; } = "";
        public string    asset_type      { get; set; } = "";
        public string?   format_id       { get; set; }
        public string?   format_name      { get; set; }
        public string?   subcategory_id   { get; set; }
        public string?   subcategory_name { get; set; }
        public string    title           { get; set; } = "";
        public string?   artist          { get; set; }
        public string?   album           { get; set; }
        public uint      duration_ms   { get; set; }
        public string    checksum      { get; set; } = "";
        public string?   rdm_file_path { get; set; }
        public string?   stream_url    { get; set; }
        public string?   image_path      { get; set; }
        public decimal?  bpm             { get; set; }
        public int?      year            { get; set; }
        public int?      rating          { get; set; }
        public string?   mood            { get; set; }
        public string?   gender          { get; set; }
        public string?   language        { get; set; }
        public string?   genre           { get; set; }
        public string?   comments        { get; set; }
        public bool      is_damaged      { get; set; }
        public bool      is_variable_duration { get; set; }
        public string    status          { get; set; } = "";
        public DateTime? start_date      { get; set; }
        public DateTime? end_date        { get; set; }
        public uint?     play_limit      { get; set; }
        public uint      play_count      { get; set; }
        public DateTime? last_played_at  { get; set; }
        public decimal?  loudness_lufs   { get; set; }
        public decimal?  loudness_peak   { get; set; }
        public DateTime  created_at      { get; set; }
        public DateTime  updated_at      { get; set; }
        public double?   cue_start       { get; set; }
        public double?   cue_intro       { get; set; }
        public double?   cue_ramp2       { get; set; }
        public double?   cue_ramp3       { get; set; }
        public double?   cue_outro       { get; set; }
        public double?   cue_start_next  { get; set; }
        public double?   cue_fade_out    { get; set; }
        public double?   cue_fade_end    { get; set; }
        public double?   cue_end         { get; set; }
        public double?   cue_hook_in     { get; set; }
        public double?   cue_hook_fade   { get; set; }
        public double?   cue_hook_out    { get; set; }
        public double?   cue_loop_in     { get; set; }
        public double?   cue_loop_out    { get; set; }
        public double?   cue_anchor      { get; set; }
    }

}
