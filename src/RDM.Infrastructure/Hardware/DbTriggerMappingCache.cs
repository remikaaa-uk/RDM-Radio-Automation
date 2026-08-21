using Dapper;
using Microsoft.Extensions.Logging;
using RDM.Core.Entities;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Hardware;

public sealed class DbTriggerMappingCache : ITriggerMappingCache
{
    private readonly IDbConnectionFactory _dbFactory;
    private readonly ILogger<DbTriggerMappingCache> _logger;

    private volatile Dictionary<TriggerLookupKey, IReadOnlyList<TriggerActionMapping>> _cache = new();

    public DbTriggerMappingCache(IDbConnectionFactory dbFactory, ILogger<DbTriggerMappingCache> logger)
    {
        _dbFactory = dbFactory;
        _logger    = logger;
    }

    public async Task InitializeAsync()
    {
        _cache = await LoadFromDbAsync();
        _logger.LogInformation("TriggerMappingCache: załadowano {Count} mapowań", _cache.Count);
    }

    public async Task ReloadAsync()
    {
        _cache = await LoadFromDbAsync();
        _logger.LogInformation("TriggerMappingCache: przeładowano {Count} mapowań", _cache.Count);
    }

    public bool TryGetMappings(TriggerLookupKey key, out IReadOnlyList<TriggerActionMapping> mappings)
    {
        return _cache.TryGetValue(key, out mappings!);
    }

    private async Task<Dictionary<TriggerLookupKey, IReadOnlyList<TriggerActionMapping>>> LoadFromDbAsync()
    {
        using var conn = await _dbFactory.CreateOpenConnectionAsync();

        var rows = await conn.QueryAsync<TriggerActionMappingRow>("""
            SELECT id, name, source_device_type, source_device_id,
                   target_signature, target_action_id, target_parameter, is_enabled
            FROM trigger_action_mappings
            WHERE is_enabled = 1
            """);

        var result = new Dictionary<TriggerLookupKey, IReadOnlyList<TriggerActionMapping>>();

        foreach (var row in rows)
        {
            if (!Enum.TryParse<ActionId>(row.target_action_id, out var actionId))
            {
                _logger.LogWarning("Nieznana akcja '{ActionId}' w trigger_action_mappings — pominięto", row.target_action_id);
                continue;
            }

            var mapping = new TriggerActionMapping
            {
                Id               = Guid.Parse(row.id),
                Name             = row.name,
                SourceDeviceType = row.source_device_type,
                SourceDeviceId   = row.source_device_id,
                TargetSignature  = row.target_signature,
                TargetActionId   = actionId,
                TargetParameter  = row.target_parameter,
                IsEnabled        = row.is_enabled,
            };

            var key = new TriggerLookupKey(row.source_device_type, row.target_signature);

            if (result.TryGetValue(key, out var existing))
                ((List<TriggerActionMapping>)existing).Add(mapping);
            else
                result[key] = new List<TriggerActionMapping> { mapping };
        }

        return result;
    }

    private sealed record TriggerActionMappingRow(
        string  id,
        string  name,
        string  source_device_type,
        string? source_device_id,
        string  target_signature,
        string  target_action_id,
        string? target_parameter,
        bool    is_enabled);
}
