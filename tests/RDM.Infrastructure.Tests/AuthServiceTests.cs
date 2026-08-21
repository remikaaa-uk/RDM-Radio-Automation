using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RDM.Infrastructure.Repositories;
using RDM.Infrastructure.Tests.Infrastructure;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Infrastructure.Tests;

[Collection("MariaDb")]
public sealed class AuthServiceTests : IAsyncLifetime
{
    private readonly MariaDbTestFixture _fixture;
    private readonly AuthService        _sut;
    private readonly UserRepository     _userRepo;
    private readonly List<string>       _userIds = new();

    public AuthServiceTests(MariaDbTestFixture fixture)
    {
        _fixture  = fixture;
        _userRepo = new UserRepository(fixture.Factory);
        _sut = new AuthService(
            _userRepo,
            new LoginThrottle(),
            Mock.Of<ILogger<AuthService>>());
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
    public async Task CreateUser_ShouldHashPasswordAndPersist()
    {
        var username = $"newop_{Guid.NewGuid():N}"[..16];

        var created = await _sut.CreateUserAsync(_fixture.StudioId, username, "P@ssw0rd!", UserRole.Operator);
        Track(created.UserId);

        created.PasswordHash.Should().NotBe("P@ssw0rd!");

        var stored = await _userRepo.GetByUsernameAsync(_fixture.StudioId, username);
        stored.Should().NotBeNull();
        stored!.Role.Should().Be(UserRole.Operator);
        stored.Enabled.Should().BeTrue();

        var authenticated = await _sut.AuthenticateAsync(_fixture.StudioId, username, "P@ssw0rd!");
        authenticated.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateUser_ShouldReject_DuplicateUsername()
    {
        var username = $"dupop_{Guid.NewGuid():N}"[..16];
        var first = await _sut.CreateUserAsync(_fixture.StudioId, username, "P@ssw0rd!", UserRole.Operator);
        Track(first.UserId);

        var act = () => _sut.CreateUserAsync(_fixture.StudioId, username, "Different1!", UserRole.Operator);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ResetPassword_ShouldChangeHash_WithoutOldPassword()
    {
        var username = $"resetop_{Guid.NewGuid():N}"[..16];
        var created = await _sut.CreateUserAsync(_fixture.StudioId, username, "OldPassword1!", UserRole.Operator);
        Track(created.UserId);

        await _sut.ResetPasswordAsync(created.UserId, "BrandNewPassword1!");

        var authOld = await _sut.AuthenticateAsync(_fixture.StudioId, username, "OldPassword1!");
        authOld.Should().BeNull();

        var authNew = await _sut.AuthenticateAsync(_fixture.StudioId, username, "BrandNewPassword1!");
        authNew.Should().NotBeNull();
    }

    private void Track(string userId) => _userIds.Add(userId);
}
