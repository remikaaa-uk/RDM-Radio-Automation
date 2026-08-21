using RDM.Core.Entities;

namespace RDM.Core.Interfaces;

public interface IAssetFormatRepository
{
    Task<IReadOnlyList<AssetFormat>> GetAllAsync(CancellationToken ct = default);
    Task<AssetFormat?> GetByIdAsync(string formatId, CancellationToken ct = default);
    Task<AssetFormat> CreateAsync(string name, CancellationToken ct = default);
    Task RenameAsync(string formatId, string newName, CancellationToken ct = default);
    Task DeleteAsync(string formatId, CancellationToken ct = default);
    Task<int> CountAssetsAsync(string formatId, CancellationToken ct = default);
}
