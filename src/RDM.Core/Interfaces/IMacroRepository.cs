using RDM.Core.Entities;

namespace RDM.Core.Interfaces;

public interface IMacroRepository
{
    Task<Macro?> GetByIdAsync(Guid macroId);
    Task<IReadOnlyList<Macro>> GetAllAsync();
    Task<IReadOnlyList<Macro>> GetAllEnabledAsync();
    Task SaveMacroAsync(Macro macro);
    Task DeleteMacroAsync(Guid macroId);
    Task SaveStepAsync(MacroStep step);
    Task DeleteStepAsync(Guid stepId);
}
