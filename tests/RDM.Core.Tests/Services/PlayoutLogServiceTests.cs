using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RDM.Core.Entities;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Core.Tests.Services;

public sealed class PlayoutLogServiceTests : IAsyncDisposable
{
    // ── Fixtures ──────────────────────────────────────────────────────────────

    private readonly InMemoryEventBus               _eventBus  = new();
    private readonly Mock<IPlayoutLogRepository>    _logRepo   = new();
    private readonly StudioContext                  _studioCtx = new();
    private readonly PlayoutLogService              _service;

    private const string StudioId = "studio-1";

    public PlayoutLogServiceTests()
    {
        _studioCtx.Initialize(StudioId);

        _logRepo
            .Setup(r => r.CreateAsync(It.IsAny<PlayoutLog>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _logRepo
            .Setup(r => r.UpdateEndedAtAsync(
                It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _service = new PlayoutLogService(
            _eventBus, _logRepo.Object, _studioCtx,
            NullLogger<PlayoutLogService>.Instance);
    }

    public async ValueTask DisposeAsync() => await _service.DisposeAsync();

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PlayoutLogService_InsertsOnStart_UpdatesOnEnd()
    {
        // Capture the logId written to DB so we can verify the UPDATE uses it
        string? capturedLogId = null;
        _logRepo
            .Setup(r => r.CreateAsync(It.IsAny<PlayoutLog>(), It.IsAny<CancellationToken>()))
            .Callback<PlayoutLog, CancellationToken>((log, _) => capturedLogId = log.LogId)
            .Returns(Task.CompletedTask);

        // START
        await _eventBus.PublishAsync(
            new PlaylistItemStartedEvent("item-1", "asset-A", "Track", "Artist", 240_000, AssetType.Track, null));
        await Task.Delay(300);

        _logRepo.Verify(r => r.CreateAsync(
            It.Is<PlayoutLog>(l =>
                l.StudioId   == StudioId &&
                l.AssetId    == "asset-A" &&
                l.SourceType == SourceType.Playlist &&
                l.EndedAt    == null),
            It.IsAny<CancellationToken>()), Times.Once);
        capturedLogId.Should().NotBeNullOrEmpty();

        // END
        await _eventBus.PublishAsync(
            new PlaylistItemEndedEvent("item-1", "asset-A", "NATURAL"));
        await Task.Delay(300);

        _logRepo.Verify(r => r.UpdateEndedAtAsync(
            capturedLogId!, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PlayoutLogService_HandlesDuplicateAsset_ByItemId()
    {
        // Same asset appears TWICE in the playlist — different ItemIds
        // Each play must create a SEPARATE log entry with its own logId

        var logIds = new List<string>();
        _logRepo
            .Setup(r => r.CreateAsync(It.IsAny<PlayoutLog>(), It.IsAny<CancellationToken>()))
            .Callback<PlayoutLog, CancellationToken>((log, _) => logIds.Add(log.LogId))
            .Returns(Task.CompletedTask);

        // First occurrence: item-1 plays "asset-A"
        await _eventBus.PublishAsync(
            new PlaylistItemStartedEvent("item-1", "asset-A", "Song", "Band", 180_000, AssetType.Track, null));
        await Task.Delay(200);

        await _eventBus.PublishAsync(
            new PlaylistItemEndedEvent("item-1", "asset-A", "NATURAL"));
        await Task.Delay(200);

        // Second occurrence: item-3 also plays "asset-A"
        await _eventBus.PublishAsync(
            new PlaylistItemStartedEvent("item-3", "asset-A", "Song", "Band", 180_000, AssetType.Track, null));
        await Task.Delay(200);

        await _eventBus.PublishAsync(
            new PlaylistItemEndedEvent("item-3", "asset-A", "NATURAL"));
        await Task.Delay(200);

        // Two separate inserts and two separate updates
        _logRepo.Verify(r => r.CreateAsync(
            It.IsAny<PlayoutLog>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _logRepo.Verify(r => r.UpdateEndedAtAsync(
            It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        logIds.Should().HaveCount(2);
        logIds[0].Should().NotBe(logIds[1], "each play gets a distinct log ID");
    }

    [Fact]
    public async Task PlayoutLogService_HandlesEndWithoutStart_Gracefully()
    {
        // End event with no matching start → must not throw, must not call UpdateEndedAtAsync
        var act = async () =>
        {
            await _eventBus.PublishAsync(
                new PlaylistItemEndedEvent("orphan-item", "asset-X", "STOPPED"));
            await Task.Delay(200);
        };

        await act.Should().NotThrowAsync();

        _logRepo.Verify(r => r.UpdateEndedAtAsync(
            It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PlayoutLogService_ConcurrentItems_TrackIndependently()
    {
        // Two different items can be "active" simultaneously (e.g., crossfade)
        var logIds = new List<string>();
        _logRepo
            .Setup(r => r.CreateAsync(It.IsAny<PlayoutLog>(), It.IsAny<CancellationToken>()))
            .Callback<PlayoutLog, CancellationToken>((log, _) => logIds.Add(log.LogId))
            .Returns(Task.CompletedTask);

        await _eventBus.PublishAsync(
            new PlaylistItemStartedEvent("item-A", "asset-1", "Track 1", null, 0, AssetType.Track, null));
        await _eventBus.PublishAsync(
            new PlaylistItemStartedEvent("item-B", "asset-2", "Track 2", null, 0, AssetType.Track, null));
        await Task.Delay(300);

        _logRepo.Verify(r => r.CreateAsync(
            It.IsAny<PlayoutLog>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        logIds.Should().HaveCount(2).And.OnlyHaveUniqueItems();

        await _eventBus.PublishAsync(
            new PlaylistItemEndedEvent("item-A", "asset-1", "NATURAL"));
        await Task.Delay(200);

        // Only item-A's log updated
        _logRepo.Verify(r => r.UpdateEndedAtAsync(
            logIds[0], It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        _logRepo.Verify(r => r.UpdateEndedAtAsync(
            logIds[1], It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
