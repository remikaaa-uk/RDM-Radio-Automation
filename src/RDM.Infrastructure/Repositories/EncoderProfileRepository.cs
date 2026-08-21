using Dapper;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Infrastructure.Repositories.Helpers;

namespace RDM.Infrastructure.Repositories;

public sealed class EncoderProfileRepository : IEncoderProfileRepository
{
    private const string SelectColumns = """
        profile_id, studio_id, name,
        format, bitrate_kbps, sample_rate_hz, channels,
        server_type, host, port, mount, username, stream_id, password_encrypted, use_ssl, use_put,
        stream_name, genre, stream_url, description, is_public,
        enabled, armed, auto_start, reconnect_delay_seconds, title_mode, title_text, created_at, updated_at
        """;

    private readonly IDbConnectionFactory _factory;

    public EncoderProfileRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<EncoderProfile?> GetByIdAsync(string profileId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<EncoderProfileRow>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM encoder_profiles WHERE profile_id = @ProfileId",
                new { ProfileId = profileId },
                cancellationToken: ct));
        return row is null ? null : MapRow(row);
    }

    public async Task<IReadOnlyList<EncoderProfile>> GetByStudioAsync(string studioId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<EncoderProfileRow>(
            new CommandDefinition(
                $"SELECT {SelectColumns} FROM encoder_profiles WHERE studio_id = @StudioId ORDER BY name",
                new { StudioId = studioId },
                cancellationToken: ct));
        return rows.Select(MapRow).ToList();
    }

    /// <summary>Profiles the bottom-bar button starts. Armed is not the same as auto-start.</summary>
    public async Task<IReadOnlyList<EncoderProfile>> GetArmedAsync(string studioId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<EncoderProfileRow>(
            new CommandDefinition(
                $"""
                 SELECT {SelectColumns} FROM encoder_profiles
                 WHERE studio_id = @StudioId AND enabled = 1 AND armed = 1
                 ORDER BY name
                 """,
                new { StudioId = studioId },
                cancellationToken: ct));
        return rows.Select(MapRow).ToList();
    }

    public async Task<IReadOnlyList<EncoderProfile>> GetAutoStartAsync(string studioId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<EncoderProfileRow>(
            new CommandDefinition(
                $"""
                 SELECT {SelectColumns} FROM encoder_profiles
                 WHERE studio_id = @StudioId AND enabled = 1 AND auto_start = 1
                 ORDER BY name
                 """,
                new { StudioId = studioId },
                cancellationToken: ct));
        return rows.Select(MapRow).ToList();
    }

    public async Task CreateAsync(EncoderProfile p, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO encoder_profiles (
                    profile_id, studio_id, name,
                    format, bitrate_kbps, sample_rate_hz, channels,
                    server_type, host, port, mount, username, stream_id, password_encrypted, use_ssl, use_put,
                    stream_name, genre, stream_url, description, is_public,
                    enabled, armed, auto_start, reconnect_delay_seconds, title_mode, title_text, created_at, updated_at
                ) VALUES (
                    @ProfileId, @StudioId, @Name,
                    @Format, @BitrateKbps, @SampleRateHz, @Channels,
                    @ServerType, @Host, @Port, @Mount, @Username, @StreamId, @PasswordEncrypted, @UseSsl, @UsePut,
                    @StreamName, @Genre, @StreamUrl, @Description, @IsPublic,
                    @Enabled, @Armed, @AutoStart, @ReconnectDelaySeconds, @TitleMode, @TitleText, @CreatedAt, @UpdatedAt
                )
                """,
                ToParams(p),
                cancellationToken: ct));
    }

    public async Task UpdateAsync(EncoderProfile p, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE encoder_profiles SET
                    name               = @Name,
                    format             = @Format,
                    bitrate_kbps       = @BitrateKbps,
                    sample_rate_hz     = @SampleRateHz,
                    channels           = @Channels,
                    server_type        = @ServerType,
                    host               = @Host,
                    port               = @Port,
                    mount              = @Mount,
                    username           = @Username,
                    stream_id          = @StreamId,
                    password_encrypted = @PasswordEncrypted,
                    use_ssl            = @UseSsl,
                    use_put            = @UsePut,
                    stream_name        = @StreamName,
                    genre              = @Genre,
                    stream_url         = @StreamUrl,
                    description        = @Description,
                    is_public          = @IsPublic,
                    enabled            = @Enabled,
                    armed              = @Armed,
                    auto_start         = @AutoStart,
                    reconnect_delay_seconds = @ReconnectDelaySeconds,
                    title_mode         = @TitleMode,
                    title_text         = @TitleText,
                    updated_at         = @UpdatedAt
                WHERE profile_id = @ProfileId
                """,
                ToParams(p),
                cancellationToken: ct));
    }

    public async Task DeleteAsync(string profileId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM encoder_profiles WHERE profile_id = @ProfileId",
                new { ProfileId = profileId },
                cancellationToken: ct));
    }

    private static object ToParams(EncoderProfile p) => new
    {
        p.ProfileId,
        p.StudioId,
        p.Name,
        Format     = EnumMapper.ToDb(p.Format),
        p.BitrateKbps,
        p.SampleRateHz,
        p.Channels,
        ServerType = EnumMapper.ToDb(p.ServerType),
        p.Host,
        p.Port,
        p.Mount,
        p.Username,
        p.StreamId,
        p.PasswordEncrypted,
        p.UseSsl,
        p.UsePut,
        p.StreamName,
        p.Genre,
        p.StreamUrl,
        p.Description,
        p.IsPublic,
        p.Enabled,
        p.Armed,
        p.AutoStart,
        p.ReconnectDelaySeconds,
        TitleMode = EnumMapper.ToDb(p.TitleMode),
        p.TitleText,
        p.CreatedAt,
        p.UpdatedAt
    };

    private static EncoderProfile MapRow(EncoderProfileRow r) => new()
    {
        ProfileId         = r.profile_id,
        StudioId          = r.studio_id,
        Name              = r.name,
        Format            = EnumMapper.ToEncoderFormat(r.format),
        BitrateKbps       = r.bitrate_kbps,
        SampleRateHz      = (int?)r.sample_rate_hz,
        Channels          = r.channels,
        ServerType        = EnumMapper.ToCastServerType(r.server_type),
        Host              = r.host,
        Port              = r.port,
        Mount             = r.mount,
        Username          = r.username,
        StreamId          = r.stream_id,
        PasswordEncrypted = r.password_encrypted,
        UseSsl            = r.use_ssl,
        UsePut            = r.use_put,
        StreamName        = r.stream_name,
        Genre             = r.genre,
        StreamUrl         = r.stream_url,
        Description       = r.description,
        IsPublic          = r.is_public,
        Enabled           = r.enabled,
        Armed             = r.armed,
        AutoStart         = r.auto_start,
        ReconnectDelaySeconds = r.reconnect_delay_seconds,
        TitleMode         = EnumMapper.ToStreamTitleMode(r.title_mode),
        TitleText         = r.title_text,
        CreatedAt         = r.created_at,
        UpdatedAt         = r.updated_at
    };

    private sealed record EncoderProfileRow(
        string    profile_id,
        string    studio_id,
        string    name,
        string    format,
        ushort    bitrate_kbps,
        uint?     sample_rate_hz,
        byte      channels,
        string    server_type,
        string    host,
        ushort    port,
        string?   mount,
        string?   username,
        string?   stream_id,
        byte[]?   password_encrypted,
        bool      use_ssl,
        bool      use_put,
        string?   stream_name,
        string?   genre,
        string?   stream_url,
        string?   description,
        bool      is_public,
        bool      enabled,
        bool      armed,
        bool      auto_start,
        int       reconnect_delay_seconds,
        string    title_mode,
        string?   title_text,
        DateTime  created_at,
        DateTime? updated_at);
}
