using RDM.Core.Entities;

namespace RDM.Core.Interfaces;

public interface ITriggerMappingRepository
{
    Task<IReadOnlyList<TriggerActionMapping>> GetAllAsync();
    Task SaveAsync(TriggerActionMapping mapping);
    Task DeleteAsync(Guid id);
}
