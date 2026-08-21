using Dapper;
using RDM.Core.Entities;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Repositories;

public sealed class TriggerMappingRepository : ITriggerMappingRepository
{
    private readonly IDbConnectionFactory _dbFactory;

    public TriggerMappingRepository(IDbConnectionFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<TriggerActionMapping>> GetAllAsync()
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();

        var rows = await conn.QueryAsync<TriggerRow>("""
            SELECT id, name, source_device_type, source_device_id,
                   target_signature, target_action_id, target_parameter, is_enabled
            FROM trigger_action_mappings
            ORDER BY source_device_type, name
            """);

        return rows.Select(Map).ToList();
    }

    public async Task SaveAsync(TriggerActionMapping mapping)
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();

        await conn.ExecuteAsync("""
            INSERT INTO trigger_action_mappings
                (id, name, source_device_type, source_device_id, target_signature, target_action_id, target_parameter, is_enabled)
            VALUES
                (@Id, @Name, @SourceDeviceType, @SourceDeviceId, @TargetSignature, @TargetActionId, @TargetParameter, @IsEnabled)
            ON DUPLICATE KEY UPDATE
                name               = VALUES(name),
                source_device_type = VALUES(source_device_type),
                source_device_id   = VALUES(source_device_id),
                target_signature   = VALUES(target_signature),
                target_action_id   = VALUES(target_action_id),
                target_parameter   = VALUES(target_parameter),
                is_enabled         = VALUES(is_enabled)
            """,
            new
            {
                Id               = mapping.Id.ToString(),
                mapping.Name,
                mapping.SourceDeviceType,
                mapping.SourceDeviceId,
                mapping.TargetSignature,
                TargetActionId   = mapping.TargetActionId.ToString(),
                mapping.TargetParameter,
                mapping.IsEnabled
            });
    }

    public async Task DeleteAsync(Guid id)
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM trigger_action_mappings WHERE id = @Id",
            new { Id = id.ToString() });
    }

    private static TriggerActionMapping Map(TriggerRow row)
    {
        Enum.TryParse<ActionId>(row.target_action_id, out var actionId);
        return new TriggerActionMapping
        {
            Id               = Guid.Parse(row.id),
            Name             = row.name,
            SourceDeviceType = row.source_device_type,
            SourceDeviceId   = row.source_device_id,
            TargetSignature  = row.target_signature,
            TargetActionId   = actionId,
            TargetParameter  = row.target_parameter,
            IsEnabled        = row.is_enabled
        };
    }

    private sealed record TriggerRow(
        string  id,
        string  name,
        string  source_device_type,
        string? source_device_id,
        string  target_signature,
        string  target_action_id,
        string? target_parameter,
        bool    is_enabled);
}
