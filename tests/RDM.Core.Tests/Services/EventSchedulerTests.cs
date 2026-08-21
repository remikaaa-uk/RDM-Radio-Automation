using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RDM.Core.Entities;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.Core.Tests.Fakes;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Core.Tests.Services;

public sealed class EventSchedulerTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private readonly Mock<IScheduledEventRepository> _eventRepo    = new();
    private readonly Mock<IAudioSettingsRepository>  _settingsRepo = new();
    private readonly Mock<IAssetFormatRepository>    _formats      = new();
    private readonly Mock<ISubcategoryRepository>    _subcategories = new();
    private readonly Mock<IExternalActionRunner>     _external     = new();
    private readonly FakeEventBus                    _eventBus     = new();   // ← Fake, not Mock
    private readonly Mock<IPlaylistController>       _playlist     = new();
    private readonly StudioContext                   _studioCtx    = new();
    private readonly EventScheduler                  _scheduler;

    private const string StudioId = "studio-1";

    // Monday 2026-06-01 10:30:15 UTC  (June 1 2026 is Monday)
    private static readonly DateTime Monday1030 = new(2026, 6, 1, 10, 30, 15, DateTimeKind.Utc);

    public EventSchedulerTests()
    {
        _studioCtx.Initialize(StudioId);

        _eventRepo
            .Setup(r => r.UpdateLastFiredAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _eventRepo
            .Setup(r => r.UpdateSkipNextAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _playlist.Setup(p => p.IsPlaying).Returns(false);
        _playlist.Setup(p => p.LoadPlaylistAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _playlist.Setup(p => p.PlayAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _playlist.Setup(p => p.StopAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _playlist.Setup(p => p.ChangeModeAsync(
            It.IsAny<PlaylistMode>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _playlist.Setup(p => p.ClearAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var executor = new ScheduledActionExecutor(
            _playlist.Object,
            _settingsRepo.Object,
            _formats.Object,
            _subcategories.Object,
            _external.Object,
            _studioCtx,
            NullLogger<ScheduledActionExecutor>.Instance);

        _scheduler = new EventScheduler(
            _eventRepo.Object,
            _eventBus,
            _playlist.Object,
            executor,
            _studioCtx,
            NullLogger<EventScheduler>.Instance);
    }

    // ── IsDue — pure static logic, no infrastructure needed ──────────────────

    [Fact]
    public void IsDue_RepeatEvent_TrueInWindow()
    {
        var evt = MakeRepeat("MON,TUE", hours: "10,12", eventHour: TimeSpan.FromMinutes(30));

        EventScheduler.IsDue(evt, Monday1030).Should().BeTrue();
    }

    [Fact]
    public void IsDue_RepeatEvent_FalseAfterWindow()
    {
        // 10:31:15 — just past the 1-minute window after 10:30
        var evt = MakeRepeat("MON", hours: "10", eventHour: TimeSpan.FromMinutes(30));
        var now = new DateTime(2026, 6, 1, 10, 31, 15, DateTimeKind.Utc);

        EventScheduler.IsDue(evt, now).Should().BeFalse();
    }

    [Fact]
    public void IsDue_RepeatEvent_FalseWrongDay()
    {
        var evt = MakeRepeat("TUE", hours: "10", eventHour: TimeSpan.FromMinutes(30));

        EventScheduler.IsDue(evt, Monday1030).Should().BeFalse();
    }

    [Fact]
    public void IsDue_RepeatEvent_FalseWrongHour()
    {
        var evt = MakeRepeat("MON", hours: "12", eventHour: TimeSpan.FromMinutes(30));

        EventScheduler.IsDue(evt, Monday1030).Should().BeFalse();
    }

    [Fact]
    public void IsDue_RepeatEvent_FalseAlreadyFiredThisWindow()
    {
        // LastFiredAt is within the current window → already handled
        var evt = MakeRepeat("MON", hours: "10", eventHour: TimeSpan.FromMinutes(30),
            lastFiredAt: new DateTime(2026, 6, 1, 10, 30, 5, DateTimeKind.Utc));

        EventScheduler.IsDue(evt, Monday1030).Should().BeFalse();
    }

    [Fact]
    public void IsDue_OneTimeEvent_TrueInWindow()
    {
        var evt = MakeOneTime(new DateOnly(2026, 6, 1), new TimeSpan(10, 30, 0));

        EventScheduler.IsDue(evt, Monday1030).Should().BeTrue();
    }

    [Fact]
    public void IsDue_OneTimeEvent_FalseWrongDate()
    {
        var evt = MakeOneTime(new DateOnly(2026, 6, 2), new TimeSpan(10, 30, 0));

        EventScheduler.IsDue(evt, Monday1030).Should().BeFalse();
    }

    [Fact]
    public void IsDue_OneTimeEvent_FalseAlreadyFired()
    {
        var evt = MakeOneTime(
            new DateOnly(2026, 6, 1), new TimeSpan(10, 30, 0),
            lastFiredAt: new DateTime(2026, 6, 1, 10, 30, 1, DateTimeKind.Utc));

        EventScheduler.IsDue(evt, Monday1030).Should().BeFalse();
    }

    // ── Integration: fires actions and publishes events ───────────────────────

    [Fact]
    public async Task EventScheduler_RepeatEvent_FiresInWindow()
    {
        var evt = MakeRepeat("MON", hours: "10", eventHour: TimeSpan.FromMinutes(30));
        SetupEvents(evt);

        await _scheduler.CheckAndFireDueEventsAsync(
            CancellationToken.None, forceNow: Monday1030);

        _eventRepo.Verify(r => r.UpdateLastFiredAsync(
            evt.EventId, Monday1030, It.IsAny<CancellationToken>()), Times.Once);

        _eventBus.CountOf<ScheduledEventFiredEvent>().Should().Be(1);
        _eventBus.Published<ScheduledEventFiredEvent>()
            .Should().ContainSingle(e => e.EventId == evt.EventId);
    }

    [Fact]
    public async Task EventScheduler_OneTimeEvent_FiresOnce()
    {
        var evt = MakeOneTime(new DateOnly(2026, 6, 1), new TimeSpan(10, 30, 0));
        SetupEvents(evt);

        await _scheduler.CheckAndFireDueEventsAsync(
            CancellationToken.None, forceNow: Monday1030);

        _eventRepo.Verify(r => r.UpdateLastFiredAsync(
            evt.EventId, Monday1030, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EventScheduler_SkipNext_EmitsSkippedEvent()
    {
        var evt = MakeRepeat("MON", hours: "10",
            eventHour: TimeSpan.FromMinutes(30), skipNext: true);
        SetupEvents(evt);

        await _scheduler.CheckAndFireDueEventsAsync(
            CancellationToken.None, forceNow: Monday1030);

        _eventRepo.Verify(r => r.UpdateSkipNextAsync(
            evt.EventId, false, It.IsAny<CancellationToken>()), Times.Once);

        _eventBus.CountOf<ScheduledEventSkippedEvent>().Should().Be(1);
        _eventBus.CountOf<ScheduledEventFiredEvent>().Should().Be(0);

        _eventRepo.Verify(r => r.UpdateLastFiredAsync(
            It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EventScheduler_SmartTiming_WaitsForTrackEnd()
    {
        // Arrange — track is playing
        _playlist.Setup(p => p.IsPlaying).Returns(true);
        var evt = MakeRepeat("MON", hours: "10",
            eventHour: TimeSpan.FromMinutes(30), smartTiming: true);
        SetupEvents(evt);

        // First tick — track playing → event deferred
        await _scheduler.CheckAndFireDueEventsAsync(
            CancellationToken.None, forceNow: Monday1030);

        _eventRepo.Verify(r => r.UpdateLastFiredAsync(
            It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _eventBus.CountOf<ScheduledEventFiredEvent>().Should().Be(0);

        // Second tick — track stopped → fires now
        _playlist.Setup(p => p.IsPlaying).Returns(false);
        SetupEvents(evt);

        await _scheduler.CheckAndFireDueEventsAsync(
            CancellationToken.None, forceNow: Monday1030);

        _eventRepo.Verify(r => r.UpdateLastFiredAsync(
            evt.EventId, Monday1030, It.IsAny<CancellationToken>()), Times.Once);
        _eventBus.CountOf<ScheduledEventFiredEvent>().Should().Be(1);
    }

    [Fact]
    public async Task EventScheduler_SmartTiming_FiresAfterTimeout()
    {
        // Arrange — track keeps playing
        _playlist.Setup(p => p.IsPlaying).Returns(true);
        var evt = MakeRepeat("MON", hours: "10",
            eventHour: TimeSpan.FromMinutes(30), smartTiming: true);
        SetupEvents(evt);

        // First tick — deferred
        await _scheduler.CheckAndFireDueEventsAsync(
            CancellationToken.None, forceNow: Monday1030);

        _eventRepo.Verify(r => r.UpdateLastFiredAsync(
            It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // 301s later — timeout reached, fire regardless of IsPlaying
        var laterNow = Monday1030.AddSeconds(301);
        SetupEvents(evt);

        await _scheduler.CheckAndFireDueEventsAsync(
            CancellationToken.None, forceNow: laterNow);

        _eventRepo.Verify(r => r.UpdateLastFiredAsync(
            evt.EventId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _eventBus.CountOf<ScheduledEventFiredEvent>().Should().Be(1);
    }

    [Fact]
    public async Task EventScheduler_ExecutesActions_ChangeModeAction()
    {
        var actionsJson = """[{"type":"CHANGE_PLAYLIST_MODE","payload":{"mode":"AUTO"}}]""";
        var evt = MakeRepeat("MON", hours: "10",
            eventHour: TimeSpan.FromMinutes(30), actions: actionsJson);
        SetupEvents(evt);

        await _scheduler.CheckAndFireDueEventsAsync(
            CancellationToken.None, forceNow: Monday1030);

        _playlist.Verify(p => p.ChangeModeAsync(
            PlaylistMode.Auto, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetupEvents(params ScheduledEvent[] events)
    {
        _eventRepo
            .Setup(r => r.GetEnabledAsync(StudioId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(events);
    }

    private static ScheduledEvent MakeRepeat(
        string days,
        string hours,
        TimeSpan eventHour,
        DateTime? lastFiredAt = null,
        bool skipNext = false,
        bool smartTiming = false,
        string actions = "[]") =>
        new()
        {
            EventId     = Guid.NewGuid().ToString(),
            StudioId    = StudioId,
            Name        = "Test Repeat Event",
            EventType   = ScheduledEventType.Repeat,
            Category    = "Default",
            Enabled     = true,
            EventHour   = eventHour,
            Days        = days,
            Hours       = hours,
            SmartTiming = smartTiming,
            Actions     = actions,
            LastFiredAt = lastFiredAt,
            SkipNext    = skipNext,
            CreatedAt   = DateTime.UtcNow
        };

    private static ScheduledEvent MakeOneTime(
        DateOnly date,
        TimeSpan eventHour,
        DateTime? lastFiredAt = null) =>
        new()
        {
            EventId     = Guid.NewGuid().ToString(),
            StudioId    = StudioId,
            Name        = "Test OneTime Event",
            EventType   = ScheduledEventType.OneTime,
            Category    = "Default",
            Enabled     = true,
            EventHour   = eventHour,
            OnlyOnDate  = date,
            Days        = "MON,TUE,WED,THU,FRI,SAT,SUN",
            Hours       = "0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23",
            SmartTiming = false,
            Actions     = "[]",
            LastFiredAt = lastFiredAt,
            SkipNext    = false,
            CreatedAt   = DateTime.UtcNow
        };
}
