using Dapper;
using FluentAssertions;
using RDM.Core.Entities;
using RDM.Infrastructure.Repositories;
using RDM.Infrastructure.Tests.Infrastructure;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Infrastructure.Tests;

[Collection("MariaDb")]
public sealed class ScheduledEventRepositoryTests : IAsyncLifetime
{
    private readonly MariaDbTestFixture _fixture;
    private readonly ScheduledEventRepository _sut;
    private readonly List<string> _eventIds = new();

    public ScheduledEventRepositoryTests(MariaDbTestFixture fixture)
    {
        _fixture = fixture;
        _sut = new ScheduledEventRepository(fixture.Factory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_eventIds.Count == 0) return;
        using var conn = _fixture.Factory.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM scheduled_events WHERE event_id IN @Ids", new { Ids = _eventIds });
    }

    [Fact]
    public async Task Create_ShouldInsert()
    {
        var id    = NewId();
        var ev    = BuildEvent(id, "Morning Playlist");

        await _sut.CreateAsync(ev);

        var result = await _sut.GetByIdAsync(id);
        result.Should().NotBeNull();
        result!.EventId.Should().Be(id);
        result.Name.Should().Be("Morning Playlist");
        result.EventType.Should().Be(ScheduledEventType.Repeat);
        result.Category.Should().Be("Default");
        result.Enabled.Should().BeTrue();
        result.Days.Should().Be("MON,TUE,WED,THU,FRI,SAT,SUN");
        result.Actions.Should().Be("[]");
        result.SkipNext.Should().BeFalse();
    }

    [Fact]
    public async Task GetById_ShouldReturn()
    {
        var id    = NewId();
        var hour  = new TimeSpan(8, 0, 0);
        var ev    = BuildEvent(id, "News", eventHour: hour);

        await _sut.CreateAsync(ev);

        var result = await _sut.GetByIdAsync(id);
        result.Should().NotBeNull();
        result!.Name.Should().Be("News");
        result.EventHour.Should().Be(hour);
        result.EventType.Should().Be(ScheduledEventType.Repeat);
    }

    [Fact]
    public async Task GetById_ShouldReturnNull_WhenNotFound()
    {
        var result = await _sut.GetByIdAsync(Guid.NewGuid().ToString());
        result.Should().BeNull();
    }

    [Fact]
    public async Task Update_ShouldModify()
    {
        var id = NewId();
        await _sut.CreateAsync(BuildEvent(id, "Original Name"));

        var updated = BuildEvent(id, "Updated Name",
            enabled: false,
            eventType: ScheduledEventType.OneTime,
            actions: "[{\"type\":\"PLAY\"}]");
        await _sut.UpdateAsync(updated);

        var result = await _sut.GetByIdAsync(id);
        result!.Name.Should().Be("Updated Name");
        result.Enabled.Should().BeFalse();
        result.EventType.Should().Be(ScheduledEventType.OneTime);
        result.Actions.Should().Be("[{\"type\":\"PLAY\"}]");
    }

    [Fact]
    public async Task GetByStudio_ShouldReturnAll()
    {
        var id1 = NewId();
        var id2 = NewId();
        await _sut.CreateAsync(BuildEvent(id1, "Event A", eventHour: new TimeSpan(9, 0, 0)));
        await _sut.CreateAsync(BuildEvent(id2, "Event B", eventHour: new TimeSpan(10, 0, 0)));

        var result = await _sut.GetByStudioAsync(_fixture.StudioId);

        result.Should().Contain(e => e.EventId == id1)
              .And.Contain(e => e.EventId == id2);
    }

    [Fact]
    public async Task GetEnabled_ShouldReturnOnlyEnabled()
    {
        var idEnabled  = NewId();
        var idDisabled = NewId();
        await _sut.CreateAsync(BuildEvent(idEnabled,  "On",  enabled: true));
        await _sut.CreateAsync(BuildEvent(idDisabled, "Off", enabled: false));

        var result = await _sut.GetEnabledAsync(_fixture.StudioId);

        result.Should().Contain(e => e.EventId == idEnabled);
        result.Should().NotContain(e => e.EventId == idDisabled);
    }

    [Fact]
    public async Task UpdateSkipNext_ShouldSetFlag()
    {
        var id = NewId();
        await _sut.CreateAsync(BuildEvent(id, "Skippable"));

        await _sut.UpdateSkipNextAsync(id, skipNext: true);

        var result = await _sut.GetByIdAsync(id);
        result!.SkipNext.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateLastFired_ShouldPersistTimestamp()
    {
        var id     = NewId();
        var firedAt = new DateTime(2026, 5, 31, 8, 0, 0, DateTimeKind.Utc);
        await _sut.CreateAsync(BuildEvent(id, "Fired Event"));

        await _sut.UpdateLastFiredAsync(id, firedAt);

        var result = await _sut.GetByIdAsync(id);
        result!.LastFiredAt.Should().BeCloseTo(firedAt, precision: TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Delete_ShouldRemoveEvent()
    {
        var id = NewId();
        await _sut.CreateAsync(BuildEvent(id, "To Delete"));

        await _sut.DeleteAsync(id);
        _eventIds.Remove(id);

        var result = await _sut.GetByIdAsync(id);
        result.Should().BeNull();
    }

    private string NewId()
    {
        var id = Guid.NewGuid().ToString();
        _eventIds.Add(id);
        return id;
    }

    private ScheduledEvent BuildEvent(
        string   id,
        string   name,
        bool     enabled   = true,
        TimeSpan? eventHour = null,
        ScheduledEventType eventType = ScheduledEventType.Repeat,
        string   actions  = "[]") => new()
    {
        EventId     = id,
        StudioId    = _fixture.StudioId,
        Name        = name,
        EventType   = eventType,
        Category    = "Default",
        Enabled     = enabled,
        EventHour   = eventHour,
        Days        = "MON,TUE,WED,THU,FRI,SAT,SUN",
        Hours       = "0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23",
        SmartTiming = false,
        Actions     = actions,
        SkipNext    = false,
        CreatedAt   = DateTime.UtcNow
    };
}
