using RDM.Core.Entities;

namespace RDM.Core.Interfaces;

public interface IScriptRepository
{
    Task<Script?> GetByIdAsync(Guid scriptId);
    Task<IReadOnlyList<Script>> GetAllAsync();
    Task<IReadOnlyList<Script>> GetAllEnabledAsync();
    Task SaveAsync(Script script);
    Task DeleteAsync(Guid scriptId);
}
