using Dapper;
using FluentAssertions;
using RDM.Core.Entities;
using RDM.Infrastructure.Repositories;
using RDM.Infrastructure.Tests.Infrastructure;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Infrastructure.Tests;

[Collection("MariaDb")]
public sealed class PlaybackSessionRepositoryTests : IAsyncLifetime
{
    private readonly MariaDbTestFixture _fixture;
    private readonly PlaybackSessionRepository _sut;
    private readonly List<string> _studioIds = new();

    public PlaybackSessionRepositoryTests(MariaDbTestFixture fixture)
    {
        _fixture = fixture;
        _sut = new PlaybackSessionRepository(fixture.Factory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_studioIds.Count == 0) return;
        using var conn = _fixture.Factory.CreateConnection();
        // playback_sessions cascade-deletes with studio
        await conn.ExecuteAsync(
            "DELETE FROM studios WHERE studio_id IN @Ids", new { Ids = _studioIds });
    }

    [Fact]
    public async Task Create_ShouldInsert()
    {
        var studioId = await CreateTempStudioAsync();
        var session = BuildSession(Guid.NewGuid().ToString(), studioId);

        await _sut.UpsertAsync(session);

        var result = await _sut.GetByStudioAsync(studioId);
        result.Should().NotBeNull();
        result!.SessionId.Should().Be(session.SessionId);
        result.StudioId.Should().Be(studioId);
        result.State.Should().Be(SessionState.Idle);
        result.Mode.Should().Be(PlaylistMode.LiveAssist);
        result.CurrentPositionMs.Should().Be(0u);
    }

    [Fact]
    public async Task GetByStudio_ShouldReturn()
    {
        var studioId = await CreateTempStudioAsync();
        var assetId  = Guid.NewGuid().ToString();
        var session  = new PlaybackSession
        {
            SessionId         = Guid.NewGuid().ToString(),
            StudioId          = studioId,
            CurrentAssetId    = assetId,
            CurrentPositionMs = 42000,
            State             = SessionState.Playing,
            Mode              = PlaylistMode.Auto,
            SnapshotAt        = DateTime.UtcNow
        };
        await _sut.UpsertAsync(session);

        var result = await _sut.GetByStudioAsync(studioId);

        result.Should().NotBeNull();
        result!.CurrentAssetId.Should().Be(assetId);
        result.CurrentPositionMs.Should().Be(42000u);
        result.State.Should().Be(SessionState.Playing);
        result.Mode.Should().Be(PlaylistMode.Auto);
    }

    [Fact]
    public async Task GetByStudio_ShouldReturnNull_WhenNotFound()
    {
        var result = await _sut.GetByStudioAsync(Guid.NewGuid().ToString());

        result.Should().BeNull();
    }

    [Fact]
    public async Task Update_ShouldModify()
    {
        var studioId  = await CreateTempStudioAsync();
        var sessionId = Guid.NewGuid().ToString();
        await _sut.UpsertAsync(BuildSession(sessionId, studioId));

        var updated = new PlaybackSession
        {
            SessionId         = sessionId,
            StudioId          = studioId,
            CurrentPositionMs = 90000,
            State             = SessionState.Playing,
            Mode              = PlaylistMode.Auto,
            SnapshotAt        = DateTime.UtcNow
        };
        await _sut.UpsertAsync(updated);

        var result = await _sut.GetByStudioAsync(studioId);
        result!.CurrentPositionMs.Should().Be(90000u);
        result.State.Should().Be(SessionState.Playing);
        result.Mode.Should().Be(PlaylistMode.Auto);
    }

    [Fact]
    public async Task Upsert_ShouldReplaceSession_WhenNewSessionId()
    {
        var studioId    = await CreateTempStudioAsync();
        var oldSessionId = Guid.NewGuid().ToString();
        var newSessionId = Guid.NewGuid().ToString();

        await _sut.UpsertAsync(BuildSession(oldSessionId, studioId));
        await _sut.UpsertAsync(BuildSession(newSessionId, studioId, state: SessionState.Playing));

        var result = await _sut.GetByStudioAsync(studioId);
        result!.SessionId.Should().Be(newSessionId);
        result.State.Should().Be(SessionState.Playing);
    }

    private async Task<string> CreateTempStudioAsync()
    {
        var studioId = Guid.NewGuid().ToString();
        using var conn = _fixture.Factory.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO studios (studio_id, name) VALUES (@StudioId, 'Temp')",
            new { StudioId = studioId });
        _studioIds.Add(studioId);
        return studioId;
    }

    private static PlaybackSession BuildSession(
        string sessionId,
        string studioId,
        SessionState state = SessionState.Idle) => new()
    {
        SessionId         = sessionId,
        StudioId          = studioId,
        CurrentPositionMs = 0,
        State             = state,
        Mode              = PlaylistMode.LiveAssist,
        SnapshotAt        = DateTime.UtcNow
    };
}
