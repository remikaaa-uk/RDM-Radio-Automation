using Dapper;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Infrastructure.Repositories.Helpers;

namespace RDM.Infrastructure.Repositories;

public sealed class AudioDeviceRepository : IAudioDeviceRepository
{
    private readonly IDbConnectionFactory _factory;

    public AudioDeviceRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<AudioDevice?> GetByIdAsync(string deviceId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<AudioDeviceRow>(
            new CommandDefinition(
                """
                SELECT device_id, studio_id, system_device_id, friendly_name,
                       driver_type, is_available, last_seen_at
                FROM audio_devices
                WHERE device_id = @DeviceId
                """,
                new { DeviceId = deviceId },
                cancellationToken: ct));
        return row is null ? null : MapRow(row);
    }

    public async Task<IReadOnlyList<AudioDevice>> GetByStudioAsync(
        string studioId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<AudioDeviceRow>(
            new CommandDefinition(
                """
                SELECT device_id, studio_id, system_device_id, friendly_name,
                       driver_type, is_available, last_seen_at
                FROM audio_devices
                WHERE studio_id = @StudioId
                """,
                new { StudioId = studioId },
                cancellationToken: ct));
        return rows.Select(MapRow).ToList();
    }

    public async Task UpsertAsync(AudioDevice device, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var affected = await conn.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE audio_devices SET
                    studio_id        = @StudioId,
                    system_device_id = @SystemDeviceId,
                    friendly_name    = @FriendlyName,
                    driver_type      = @DriverType,
                    is_available     = @IsAvailable,
                    last_seen_at     = @LastSeenAt
                WHERE device_id = @DeviceId
                """,
                BuildParams(device),
                cancellationToken: ct));

        if (affected == 0)
        {
            await conn.ExecuteAsync(
                new CommandDefinition(
                    """
                    INSERT INTO audio_devices (device_id, studio_id, system_device_id,
                                               friendly_name, driver_type, is_available, last_seen_at)
                    VALUES (@DeviceId, @StudioId, @SystemDeviceId,
                            @FriendlyName, @DriverType, @IsAvailable, @LastSeenAt)
                    """,
                    BuildParams(device),
                    cancellationToken: ct));
        }
    }

    public async Task UpdateAvailabilityAsync(
        string deviceId, bool isAvailable, DateTime lastSeenAt, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE audio_devices
                SET is_available = @IsAvailable, last_seen_at = @LastSeenAt
                WHERE device_id = @DeviceId
                """,
                new { DeviceId = deviceId, IsAvailable = isAvailable ? 1 : 0, LastSeenAt = lastSeenAt },
                cancellationToken: ct));
    }

    private static object BuildParams(AudioDevice d) => new
    {
        d.DeviceId,
        d.StudioId,
        d.SystemDeviceId,
        d.FriendlyName,
        DriverType   = EnumMapper.ToDb(d.DriverType),
        IsAvailable  = d.IsAvailable ? 1 : 0,
        d.LastSeenAt
    };

    private static AudioDevice MapRow(AudioDeviceRow r) => new()
    {
        DeviceId       = r.device_id,
        StudioId       = r.studio_id,
        SystemDeviceId = r.system_device_id,
        FriendlyName   = r.friendly_name,
        DriverType     = EnumMapper.ToDriverType(r.driver_type),
        IsAvailable    = r.is_available,
        LastSeenAt     = r.last_seen_at
    };

    private sealed record AudioDeviceRow(
        string    device_id,
        string    studio_id,
        string    system_device_id,
        string    friendly_name,
        string    driver_type,
        bool      is_available,
        DateTime? last_seen_at);
}
