using Microsoft.Extensions.Logging;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Shared.Enums;

namespace RDM.Infrastructure;

/// <summary>
/// Verifies credentials using BCrypt (cost factor 12) and returns the authenticated User.
/// Placed in Infrastructure because it depends on BCrypt.Net-Next (external library).
///
/// LoginWindow must use this service — never query IUserRepository directly.
/// </summary>
public sealed class AuthService : IAuthService
{
    private readonly IUserRepository      _userRepo;
    private readonly ILoginThrottle       _throttle;
    private readonly ILogger<AuthService> _logger;

    public AuthService(IUserRepository userRepo, ILoginThrottle throttle, ILogger<AuthService> logger)
    {
        _userRepo = userRepo;
        _throttle = throttle;
        _logger   = logger;
    }

    public async Task<User?> AuthenticateAsync(
        string studioId, string username, string password, CancellationToken ct = default)
    {
        // Brute-force guard keyed by studio + username (case-insensitive).
        var key = $"{studioId}|{username.ToLowerInvariant()}";

        if (_throttle.IsLockedOut(key, out var retryAfter))
        {
            _logger.LogWarning(
                "AuthService: user '{Username}' temporarily locked out ({Seconds:F0}s remaining) after repeated failures",
                username, retryAfter.TotalSeconds);
            return null;
        }

        var user = await _userRepo.GetByUsernameAsync(studioId, username, ct);

        if (user is null)
        {
            // Count unknown-user attempts too — otherwise username probing / spraying is unthrottled.
            _throttle.RegisterFailure(key);
            _logger.LogDebug(
                "AuthService: user '{Username}' not found in studio {StudioId}",
                username, studioId);
            return null;
        }

        if (!user.Enabled)
        {
            _logger.LogWarning(
                "AuthService: login attempt for disabled user '{Username}'", username);
            return null;
        }

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            _throttle.RegisterFailure(key);
            _logger.LogDebug(
                "AuthService: wrong password for user '{Username}'", username);
            return null;
        }

        // Success — clear the failure counter for this identity.
        _throttle.Reset(key);

        await _userRepo.UpdateLastLoginAsync(user.UserId, DateTime.UtcNow, ct);

        _logger.LogInformation(
            "AuthService: user '{Username}' authenticated (role={Role})",
            username, user.Role);

        return user;
    }

    public async Task<bool> ChangePasswordAsync(
        string userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        var user = await _userRepo.GetByIdAsync(userId, ct);
        if (user is null)
        {
            _logger.LogWarning(
                "AuthService.ChangePassword: user {UserId} not found", userId);
            return false;
        }

        if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
        {
            _logger.LogDebug(
                "AuthService.ChangePassword: wrong current password for user {UserId}", userId);
            return false;
        }

        string newHash = BCrypt.Net.BCrypt.HashPassword(newPassword, workFactor: 12);
        await _userRepo.UpdatePasswordHashAsync(userId, newHash, ct);

        _logger.LogInformation(
            "AuthService.ChangePassword: password changed for user {UserId}", userId);

        return true;
    }

    public async Task<User> CreateUserAsync(
        string studioId, string username, string password, UserRole role, CancellationToken ct = default)
    {
        var existing = await _userRepo.GetByUsernameAsync(studioId, username, ct);
        if (existing is not null)
        {
            _logger.LogWarning(
                "AuthService.CreateUser: username '{Username}' already taken in studio {StudioId}",
                username, studioId);
            throw new InvalidOperationException($"Username '{username}' is already taken.");
        }

        var user = new User
        {
            UserId       = Guid.NewGuid().ToString(),
            StudioId     = studioId,
            Username     = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12),
            Role         = role,
            Enabled      = true,
            CreatedAt    = DateTime.UtcNow
        };

        await _userRepo.CreateAsync(user, ct);

        _logger.LogInformation(
            "AuthService.CreateUser: created user '{Username}' (role={Role}) in studio {StudioId}",
            username, role, studioId);

        return user;
    }

    public async Task ResetPasswordAsync(string userId, string newPassword, CancellationToken ct = default)
    {
        string newHash = BCrypt.Net.BCrypt.HashPassword(newPassword, workFactor: 12);
        await _userRepo.UpdatePasswordHashAsync(userId, newHash, ct);

        _logger.LogInformation(
            "AuthService.ResetPassword: password reset for user {UserId}", userId);
    }
}
