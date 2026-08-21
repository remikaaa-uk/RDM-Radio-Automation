using Dapper;
using Microsoft.Extensions.Logging;
using RDM.Core.Entities;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Hardware;

public sealed class DbFeedbackMappingCache : IFeedbackMappingCache
{
    private readonly IDbConnectionFactory _dbFactory;
    private readonly ILogger<DbFeedbackMappingCache> _logger;

    private volatile Dictionary<string, IReadOnlyList<FeedbackRule>> _cache = new();

    public DbFeedbackMappingCache(IDbConnectionFactory dbFactory, ILogger<DbFeedbackMappingCache> logger)
    {
        _dbFactory = dbFactory;
        _logger    = logger;
    }

    public async Task InitializeAsync()
    {
        _cache = await LoadFromDbAsync();
        _logger.LogInformation("FeedbackMappingCache: załadowano {Count} reguł", _cache.Values.Sum(r => r.Count));
    }

    public async Task ReloadAsync()
    {
        _cache = await LoadFromDbAsync();
        _logger.LogInformation("FeedbackMappingCache: przeładowano {Count} reguł", _cache.Values.Sum(r => r.Count));
    }

    public IReadOnlyList<FeedbackRule> GetFeedbackRules(string eventName)
    {
        return _cache.TryGetValue(eventName, out var rules) ? rules : [];
    }

    private async Task<Dictionary<string, IReadOnlyList<FeedbackRule>>> LoadFromDbAsync()
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();

        var rows = await conn.QueryAsync<FeedbackRuleRow>("""
            SELECT id, event_name, target_device_id, target_device_type,
                   channel, note_code, velocity, dr_target, dr_gpo_index, serial_command, is_enabled
            FROM feedback_mappings
            WHERE is_enabled = 1
            """);

        var result = new Dictionary<string, IReadOnlyList<FeedbackRule>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var rule = new FeedbackRule
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
                IsEnabled        = row.is_enabled,
            };

            if (result.TryGetValue(row.event_name, out var existing))
                ((List<FeedbackRule>)existing).Add(rule);
            else
                result[row.event_name] = new List<FeedbackRule> { rule };
        }

        return result;
    }

    private sealed record FeedbackRuleRow(
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
