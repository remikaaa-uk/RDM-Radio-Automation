using RDM.Core.Entities;

namespace RDM.Core.Interfaces;

public interface IGenreRepository
{
    Task<IReadOnlyList<Genre>> GetAllAsync(CancellationToken ct = default);
    Task<Genre> CreateAsync(string name, CancellationToken ct = default);
    Task RenameAsync(string genreId, string newName, CancellationToken ct = default);
    Task DeleteAsync(string genreId, CancellationToken ct = default);
}
