using Dapper;
using FluentAssertions;
using RDM.Core.Entities;
using RDM.Infrastructure.Repositories;
using RDM.Infrastructure.Tests.Infrastructure;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Infrastructure.Tests;

[Collection("MariaDb")]
public sealed class UserRepositoryTests : IAsyncLifetime
{
    private readonly MariaDbTestFixture _fixture;
    private readonly UserRepository _sut;
    private readonly List<string> _userIds = new();

    public UserRepositoryTests(MariaDbTestFixture fixture)
    {
        _fixture = fixture;
        _sut = new UserRepository(fixture.Factory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_userIds.Count == 0) return;
        using var conn = _fixture.Factory.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM users WHERE user_id IN @Ids", new { Ids = _userIds });
    }

    [Fact]
    public async Task Create_ShouldInsert()
    {
        var id   = NewId();
        var user = BuildUser(id, "operator1");

        await _sut.CreateAsync(user);

        var result = await _sut.GetByIdAsync(id);
        result.Should().NotBeNull();
        result!.UserId.Should().Be(id);
        result.Username.Should().Be("operator1");
        result.Role.Should().Be(UserRole.Operator);
        result.Enabled.Should().BeTrue();
        result.StudioId.Should().Be(_fixture.StudioId);
    }

    [Fact]
    public async Task GetById_ShouldReturn()
    {
        var id   = NewId();
        var user = BuildUser(id, "admin1", role: UserRole.Admin);
        await _sut.CreateAsync(user);

        var result = await _sut.GetByIdAsync(id);

        result.Should().NotBeNull();
        result!.Username.Should().Be("admin1");
        result.Role.Should().Be(UserRole.Admin);
        result.PasswordHash.Should().Be(user.PasswordHash);
        result.LastLoginAt.Should().BeNull();
    }

    [Fact]
    public async Task GetById_ShouldReturnNull_WhenNotFound()
    {
        var result = await _sut.GetByIdAsync(Guid.NewGuid().ToString());
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByUsername_ShouldReturn()
    {
        var id       = NewId();
        var username = $"user_{id[..8]}";
        await _sut.CreateAsync(BuildUser(id, username));

        var result = await _sut.GetByUsernameAsync(_fixture.StudioId, username);

        result.Should().NotBeNull();
        result!.UserId.Should().Be(id);
        result.Username.Should().Be(username);
    }

    [Fact]
    public async Task GetByUsername_ShouldReturnNull_WhenNotFound()
    {
        var result = await _sut.GetByUsernameAsync(_fixture.StudioId, "nonexistent_xyz");
        result.Should().BeNull();
    }

    [Fact]
    public async Task Update_ShouldModify()
    {
        var id      = NewId();
        var loginAt = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc);
        await _sut.CreateAsync(BuildUser(id, $"user_{id[..8]}"));

        await _sut.UpdateLastLoginAsync(id, loginAt);

        var result = await _sut.GetByIdAsync(id);
        result!.LastLoginAt.Should().BeCloseTo(loginAt, precision: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetByStudio_ShouldReturnAll()
    {
        var id1 = NewId();
        var id2 = NewId();
        await _sut.CreateAsync(BuildUser(id1, $"studio_a_{id1[..8]}"));
        await _sut.CreateAsync(BuildUser(id2, $"studio_a_{id2[..8]}"));

        var result = await _sut.GetByStudioAsync(_fixture.StudioId);

        result.Select(u => u.UserId).Should().Contain([id1, id2]);
    }

    [Fact]
    public async Task UpdateRole_ShouldModify()
    {
        var id = NewId();
        await _sut.CreateAsync(BuildUser(id, $"user_{id[..8]}", role: UserRole.Operator));

        await _sut.UpdateRoleAsync(id, UserRole.Admin);

        var result = await _sut.GetByIdAsync(id);
        result!.Role.Should().Be(UserRole.Admin);
    }

    [Fact]
    public async Task UpdateEnabled_ShouldModify()
    {
        var id = NewId();
        await _sut.CreateAsync(BuildUser(id, $"user_{id[..8]}", enabled: true));

        await _sut.UpdateEnabledAsync(id, false);

        var result = await _sut.GetByIdAsync(id);
        result!.Enabled.Should().BeFalse();
    }

    private string NewId()
    {
        var id = Guid.NewGuid().ToString();
        _userIds.Add(id);
        return id;
    }

    private User BuildUser(
        string   id,
        string   username,
        UserRole role    = UserRole.Operator,
        bool     enabled = true) => new()
    {
        UserId       = id,
        StudioId     = _fixture.StudioId,
        Username     = username,
        PasswordHash = "$2a$12$placeholderHashForTestingOnly000000000000000000000000000",
        Role         = role,
        Enabled      = enabled,
        CreatedAt    = DateTime.UtcNow
    };
}
