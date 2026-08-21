using RDM.Core.Entities;
using RDM.Shared.Enums;

namespace RDM.Core.Interfaces;

public interface IAuthService
{
    Task<User?> AuthenticateAsync(string studioId, string username, string password, CancellationToken ct = default);
    Task<bool> ChangePasswordAsync(string userId, string currentPassword, string newPassword, CancellationToken ct = default);

    /// <summary>Admin action: create a new account. Throws <see cref="InvalidOperationException"/> if the username is taken.</summary>
    Task<User> CreateUserAsync(string studioId, string username, string password, UserRole role, CancellationToken ct = default);

    /// <summary>Admin action: overwrite a user's password without knowing the current one.</summary>
    Task ResetPasswordAsync(string userId, string newPassword, CancellationToken ct = default);
}
