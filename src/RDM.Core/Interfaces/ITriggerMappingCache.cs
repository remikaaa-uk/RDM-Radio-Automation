using RDM.Core.Entities;

namespace RDM.Core.Interfaces;

public readonly record struct TriggerLookupKey(string DeviceType, string Signature);

public interface ITriggerMappingCache
{
    Task InitializeAsync();
    Task ReloadAsync();
    bool TryGetMappings(TriggerLookupKey key, out IReadOnlyList<TriggerActionMapping> mappings);
}
