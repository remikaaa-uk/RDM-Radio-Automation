using RDM.Core.Entities;

namespace RDM.Core.Interfaces;

public interface ISubcategoryRepository
{
    Task<IReadOnlyList<Subcategory>> GetByFormatIdAsync(string formatId, CancellationToken ct = default);
    Task<Subcategory> CreateAsync(string formatId, string name, CancellationToken ct = default);
    Task RenameAsync(string subcategoryId, string newName, CancellationToken ct = default);
    Task DeleteAsync(string subcategoryId, CancellationToken ct = default);
    Task<int> CountAssetsAsync(string subcategoryId, CancellationToken ct = default);
}
