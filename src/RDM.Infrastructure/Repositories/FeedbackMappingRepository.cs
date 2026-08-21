using Dapper;
using RDM.Core.Entities;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Repositories;

public sealed class FeedbackMappingRepository : IFeedbackMappingRepository
{
    private readonly IDbConnectionFactory _dbFactory;

    public FeedbackMappingRepository(IDbConnectionFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<IReadOnlyList<FeedbackRule>> GetAllAsync()
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();

        var rows = await conn.QueryAsync<FeedbackRow>("""
            SELECT id, event_name, target_device_id, target_device_type,
                   channel, note_code, velocity, dr_target, dr_gpo_index, serial_command, is_enabled
            FROM feedback_mappings
            ORDER BY event_name
            """);

        return rows.Select(Map).ToList();
    }

    public async Task SaveAsync(FeedbackRule rule)
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();

        await conn.ExecuteAsync("""
            INSERT INTO feedback_mappings
                (id, event_name, target_device_id, target_device_type,
                 channel, note_code, velocity, dr_target, dr_gpo_index, serial_command, is_enabled)
            VALUES
                (@Id, @EventName, @TargetDeviceId, @TargetDeviceType,
                 @Channel, @NoteCode, @Velocity, @DrTarget, @DrGpoIndex, @SerialCommand, @IsEnabled)
            ON DUPLICATE KEY UPDATE
                event_name         = VALUES(event_name),
                target_device_id   = VALUES(target_device_id),
                target_device_type = VALUES(target_device_type),
                channel            = VALUES(channel),
                note_code          = VALUES(note_code),
                velocity           = VALUES(velocity),
                dr_target          = VALUES(dr_target),
                dr_gpo_index       = VALUES(dr_gpo_index),
                serial_command     = VALUES(serial_command),
                is_enabled         = VALUES(is_enabled)
            """,
            new
            {
                Id               = rule.Id.ToString(),
                rule.EventName,
                rule.TargetDeviceId,
                rule.TargetDeviceType,
                rule.Channel,
                rule.NoteCode,
                rule.Velocity,
                rule.DrTarget,
                rule.DrGpoIndex,
                rule.SerialCommand,
                rule.IsEnabled
            });
    }

    public async Task DeleteAsync(Guid id)
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();
        await conn.ExecuteAsync(
            "DELETE FROM feedback_mappings WHERE id = @Id",
            new { Id = id.ToString() });
    }

    private static FeedbackRule Map(FeedbackRow row) => new()
    {
        Id               = Guid.Parse(row.id),
        EventName        = row.event_name,
        TargetDeviceId   = row.target_device_id,
        TargetDeviceType = row.target_device_type,
        Channel          = row.channel,
        NoteCode         = row.note_code,
        Velocity         = row.velocity,
        DrTarget         = row.dr_target,
        DrGpoIndex       = row.dr_gpo_index,
        SerialCommand    = row.serial_command,
        IsEnabled        = row.is_enabled
    };

    private sealed record FeedbackRow(
        string  id,
        string  event_name,
        string  target_device_id,
        string  target_device_type,
        byte    channel,
        byte    note_code,
        byte    velocity,
        string? dr_target,
        int?    dr_gpo_index,
        string? serial_command,
        bool    is_enabled);
}
