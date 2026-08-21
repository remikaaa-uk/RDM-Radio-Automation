using RDM.Core.Entities;
using RDM.Shared.Enums;

namespace RDM.Core.Interfaces;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(string userId, CancellationToken ct = default);
    Task<User?> GetByUsernameAsync(string studioId, string username, CancellationToken ct = default);
    Task<IReadOnlyList<User>> GetByStudioAsync(string studioId, CancellationToken ct = default);
    Task CreateAsync(User user, CancellationToken ct = default);
    Task UpdateLastLoginAsync(string userId, DateTime loginAt, CancellationToken ct = default);
    Task UpdatePasswordHashAsync(string userId, string passwordHash, CancellationToken ct = default);
    Task UpdateRoleAsync(string userId, UserRole role, CancellationToken ct = default);
    Task UpdateEnabledAsync(string userId, bool enabled, CancellationToken ct = default);
}
