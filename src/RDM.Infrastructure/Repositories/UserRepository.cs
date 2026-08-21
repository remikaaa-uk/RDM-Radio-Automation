using Dapper;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Infrastructure.Repositories.Helpers;
using RDM.Shared.Enums;

namespace RDM.Infrastructure.Repositories;

public sealed class UserRepository : IUserRepository
{
    private readonly IDbConnectionFactory _factory;

    public UserRepository(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<User?> GetByIdAsync(string userId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
            new CommandDefinition(
                """
                SELECT user_id, studio_id, username, password_hash, role,
                       enabled, created_at, last_login_at
                FROM users
                WHERE user_id = @UserId
                """,
                new { UserId = userId },
                cancellationToken: ct));

        return row is null ? null : MapRow(row);
    }

    public async Task<User?> GetByUsernameAsync(string studioId, string username, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var row = await conn.QuerySingleOrDefaultAsync<UserRow>(
            new CommandDefinition(
                """
                SELECT user_id, studio_id, username, password_hash, role,
                       enabled, created_at, last_login_at
                FROM users
                WHERE studio_id = @StudioId AND username = @Username
                """,
                new { StudioId = studioId, Username = username },
                cancellationToken: ct));

        return row is null ? null : MapRow(row);
    }

    public async Task<IReadOnlyList<User>> GetByStudioAsync(string studioId, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        var rows = await conn.QueryAsync<UserRow>(
            new CommandDefinition(
                """
                SELECT user_id, studio_id, username, password_hash, role,
                       enabled, created_at, last_login_at
                FROM users
                WHERE studio_id = @StudioId
                ORDER BY username
                """,
                new { StudioId = studioId },
                cancellationToken: ct));

        return rows.Select(MapRow).ToList();
    }

    public async Task CreateAsync(User user, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO users (user_id, studio_id, username, password_hash, role, enabled)
                VALUES (@UserId, @StudioId, @Username, @PasswordHash, @Role, @Enabled)
                """,
                new
                {
                    user.UserId,
                    user.StudioId,
                    user.Username,
                    user.PasswordHash,
                    Role = EnumMapper.ToDb(user.Role),
                    Enabled = user.Enabled ? 1 : 0
                },
                cancellationToken: ct));
    }

    public async Task UpdateLastLoginAsync(string userId, DateTime loginAt, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "UPDATE users SET last_login_at = @LoginAt WHERE user_id = @UserId",
                new { UserId = userId, LoginAt = loginAt },
                cancellationToken: ct));
    }

    public async Task UpdatePasswordHashAsync(
        string userId, string passwordHash, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "UPDATE users SET password_hash = @PasswordHash WHERE user_id = @UserId",
                new { UserId = userId, PasswordHash = passwordHash },
                cancellationToken: ct));
    }

    public async Task UpdateRoleAsync(string userId, UserRole role, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "UPDATE users SET role = @Role WHERE user_id = @UserId",
                new { UserId = userId, Role = EnumMapper.ToDb(role) },
                cancellationToken: ct));
    }

    public async Task UpdateEnabledAsync(string userId, bool enabled, CancellationToken ct = default)
    {
        using var conn = _factory.CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(
                "UPDATE users SET enabled = @Enabled WHERE user_id = @UserId",
                new { UserId = userId, Enabled = enabled ? 1 : 0 },
                cancellationToken: ct));
    }

    private static User MapRow(UserRow row) => new()
    {
        UserId = row.user_id,
        StudioId = row.studio_id,
        Username = row.username,
        PasswordHash = row.password_hash,
        Role = EnumMapper.ToUserRole(row.role),
        Enabled = row.enabled,
        CreatedAt = row.created_at,
        LastLoginAt = row.last_login_at
    };

    private sealed record UserRow(
        string user_id,
        string studio_id,
        string username,
        string password_hash,
        string role,
        bool enabled,
        DateTime created_at,
        DateTime? last_login_at);
}
