using RDM.Core.Entities;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Hardware;

/// <summary>
/// Etap 1: hardcoded in-memory mapping cache (no DB).
/// Keys: PageUp=Play, PageDown=Stop, Home=Next — non-conflicting with hotkeys.json.
/// Replaced by DbTriggerMappingCache in Etap 2.
/// </summary>
public sealed class HardcodedTriggerMappingCache : ITriggerMappingCache
{
    private readonly Dictionary<TriggerLookupKey, IReadOnlyList<TriggerActionMapping>> _cache;

    public HardcodedTriggerMappingCache()
    {
        _cache = BuildCache();
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task ReloadAsync() => Task.CompletedTask;

    public bool TryGetMappings(TriggerLookupKey key, out IReadOnlyList<TriggerActionMapping> mappings)
    {
        return _cache.TryGetValue(key, out mappings!);
    }

    private static Dictionary<TriggerLookupKey, IReadOnlyList<TriggerActionMapping>> BuildCache()
    {
        var entries = new[]
        {
            Mapping("PageUp",   ActionId.PlayerPlay),
            Mapping("PageDown", ActionId.PlayerStop),
            Mapping("Home",     ActionId.PlayerNext),
        };

        return entries.ToDictionary(
            m => new TriggerLookupKey("KEYBOARD", m.TargetSignature),
            m => (IReadOnlyList<TriggerActionMapping>)[m]);
    }

    private static TriggerActionMapping Mapping(string keyCode, ActionId actionId) => new()
    {
        Id              = Guid.NewGuid(),
        Name            = $"hw_{keyCode}",
        SourceDeviceType = "KEYBOARD",
        TargetSignature  = $"Key_{keyCode}",
        TargetActionId   = actionId,
        IsEnabled        = true,
    };
}
