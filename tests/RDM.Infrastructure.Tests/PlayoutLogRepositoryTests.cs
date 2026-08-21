using Dapper;
using FluentAssertions;
using RDM.Core.Entities;
using RDM.Infrastructure.Repositories;
using RDM.Infrastructure.Tests.Infrastructure;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Infrastructure.Tests;

[Collection("MariaDb")]
public sealed class PlayoutLogRepositoryTests : IAsyncLifetime
{
    private readonly MariaDbTestFixture _fixture;
    private readonly PlayoutLogRepository _sut;
    private readonly List<string> _logIds = new();

    public PlayoutLogRepositoryTests(MariaDbTestFixture fixture)
    {
        _fixture = fixture;
        _sut = new PlayoutLogRepository(fixture.Factory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_logIds.Count == 0) return;
        using var conn = _fixture.Factory.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM playout_log WHERE log_id IN @Ids", new { Ids = _logIds });
    }

    [Fact]
    public async Task Create_ShouldInsert()
    {
        var id  = NewId();
        var now = DateTime.UtcNow;
        var log = BuildLog(id, now);

        await _sut.CreateAsync(log);

        var results = await _sut.GetByStudioAsync(
            _fixture.StudioId, now.AddSeconds(-5), now.AddSeconds(5));
        results.Should().Contain(l => l.LogId == id);

        var result = results.Single(l => l.LogId == id);
        result.TempTitle.Should().Be("Test Track");
        result.TempArtist.Should().Be("Test Artist");
        result.SourceType.Should().Be(SourceType.Playlist);
        result.EndedAt.Should().BeNull();
    }

    [Fact]
    public async Task GetByStudio_ShouldReturn()
    {
        var id  = NewId();
        var now = DateTime.UtcNow;
        await _sut.CreateAsync(BuildLog(id, now, sourceType: SourceType.Cart));

        var results = await _sut.GetByStudioAsync(
            _fixture.StudioId, now.AddSeconds(-5), now.AddSeconds(5));

        var result = results.SingleOrDefault(l => l.LogId == id);
        result.Should().NotBeNull();
        result!.SourceType.Should().Be(SourceType.Cart);
        result.StudioId.Should().Be(_fixture.StudioId);
    }

    [Fact]
    public async Task GetByStudio_ShouldReturnEmpty_OutsideDateRange()
    {
        var id  = NewId();
        var now = DateTime.UtcNow;
        await _sut.CreateAsync(BuildLog(id, now));

        var results = await _sut.GetByStudioAsync(
            _fixture.StudioId,
            now.AddHours(-2),
            now.AddHours(-1));

        results.Should().NotContain(l => l.LogId == id);
    }

    [Fact]
    public async Task Update_ShouldModify()
    {
        var id     = NewId();
        var now    = DateTime.UtcNow;
        var ended  = now.AddMinutes(3);
        await _sut.CreateAsync(BuildLog(id, now));

        await _sut.UpdateEndedAtAsync(id, ended);

        var results = await _sut.GetByStudioAsync(
            _fixture.StudioId, now.AddSeconds(-5), now.AddSeconds(5));
        var result = results.Single(l => l.LogId == id);
        result.EndedAt.Should().BeCloseTo(ended, precision: TimeSpan.FromSeconds(1));
    }

    private string NewId()
    {
        var id = Guid.NewGuid().ToString();
        _logIds.Add(id);
        return id;
    }

    private PlayoutLog BuildLog(
        string   id,
        DateTime startedAt,
        SourceType sourceType = SourceType.Playlist) => new()
    {
        LogId      = id,
        StudioId   = _fixture.StudioId,
        AssetId    = null,
        TempTitle  = "Test Track",
        TempArtist = "Test Artist",
        StartedAt  = startedAt,
        SourceType = sourceType
    };
}
