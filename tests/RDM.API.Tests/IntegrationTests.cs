using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Shared.DTOs;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.API.Tests;

public static class HttpClientExtensions
{
    public static HttpClient WithBasicAuth(this HttpClient client, string username, string password)
    {
        var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authHeader);
        return client;
    }
}

public class IntegrationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private const string AdminUser = "testadmin";
    private const string AdminPass = "adminpassword";
    private const string OperatorUser = "testoperator";
    private const string OperatorPass = "oppassword";

    public IntegrationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;

        // Setup test users in mock UserRepository
        var adminPasswordHash = BCrypt.Net.BCrypt.HashPassword(AdminPass, workFactor: 4);
        var opPasswordHash = BCrypt.Net.BCrypt.HashPassword(OperatorPass, workFactor: 4);

        _factory.UserRepositoryMock.Setup(r => r.GetByUsernameAsync("test-studio-id", AdminUser, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User
            {
                UserId = "admin-user-id",
                StudioId = "test-studio-id",
                Username = AdminUser,
                PasswordHash = adminPasswordHash,
                Role = UserRole.Admin,
                Enabled = true
            });

        _factory.UserRepositoryMock.Setup(r => r.GetByUsernameAsync("test-studio-id", OperatorUser, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User
            {
                UserId = "op-user-id",
                StudioId = "test-studio-id",
                Username = OperatorUser,
                PasswordHash = opPasswordHash,
                Role = UserRole.Operator,
                Enabled = true
            });
    }

    [Fact]
    public async Task GetNowPlaying_Idle_ShouldReturnIdle()
    {
        // Arrange
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
        var idleInfo = new NowPlayingInfo(
            CurrentAsset: null,
            PositionMs: 0,
            OutroCueMs: null,
            TrackStartedAt: null,
            NextItem: null,
            Mode: PlaylistMode.Auto,
            State: SessionState.Idle,
            CurrentItemId: null
        );
        _factory.PlaylistControllerMock.Setup(p => p.GetNowPlayingInfo()).Returns(idleInfo);

        // Act
        var response = await client.GetAsync("/api/v1/nowplaying");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<NowPlayingDto>();
        body.Should().NotBeNull();
        body!.NowPlaying.Should().BeNull();
        body.State.Should().Be("IDLE");
    }

    [Fact]
    public async Task GetNowPlaying_Playing_ShouldReturnPlayingInfo()
    {
        // Arrange
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
        var playingAsset = new Asset { AssetId = "asset-1", Title = "Track 1", Artist = "Artist 1", DurationMs = 180000 };
        var nextItem = new PlaylistItem { ItemId = "item-2", ItemType = PlaylistItemType.Asset, AssetId = "asset-2" };
        var playingInfo = new NowPlayingInfo(
            CurrentAsset: playingAsset,
            PositionMs: 30000,
            OutroCueMs: 170000,
            TrackStartedAt: DateTime.UtcNow.AddSeconds(-30),
            NextItem: nextItem,
            Mode: PlaylistMode.Auto,
            State: SessionState.Playing,
            CurrentItemId: "item-1"
        );
        _factory.PlaylistControllerMock.Setup(p => p.GetNowPlayingInfo()).Returns(playingInfo);

        var nextAsset = new Asset { AssetId = "asset-2", Title = "Track 2", Artist = "Artist 2", DurationMs = 200000 };
        _factory.AssetRepositoryMock.Setup(r => r.GetByIdAsync("asset-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(nextAsset);

        // Act
        var response = await client.GetAsync("/api/v1/nowplaying");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<NowPlayingDto>();
        body.Should().NotBeNull();
        body!.NowPlaying.Should().NotBeNull();
        body.NowPlaying!.AssetId.Should().Be("asset-1");
        body.NowPlaying!.PositionMs.Should().Be(30000);
        body.NowPlaying!.RemainingMs.Should().Be(140000); // OutroCueMs (170000) - PositionMs (30000)
        body.NextTrack.Should().NotBeNull();
        body.NextTrack!.AssetId.Should().Be("asset-2");
        body.State.Should().Be("PLAYING");
    }

    [Fact]
    public async Task PostPlaylistPlay_ShouldReturnPlaying()
    {
        // Arrange
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
        _factory.PlaylistControllerMock.Setup(p => p.PlayAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        
        var playingInfo = new NowPlayingInfo(
            CurrentAsset: null,
            PositionMs: 0,
            OutroCueMs: null,
            TrackStartedAt: null,
            NextItem: null,
            Mode: PlaylistMode.Auto,
            State: SessionState.Playing,
            CurrentItemId: null
        );
        _factory.PlaylistControllerMock.Setup(p => p.GetNowPlayingInfo()).Returns(playingInfo);

        // Act
        var response = await client.PostAsync("/api/v1/playlist/play", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<PlaybackStatusResponseDto>();
        body.Should().NotBeNull();
        body!.State.Should().Be("PLAYING");
        _factory.PlaylistControllerMock.Verify(p => p.PlayAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchAssets_ShouldReturnEnvelopedItems()
    {
        // Arrange
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
        var items = new List<Asset>
        {
            new Asset { AssetId = "asset-1", Title = "Test Track", Artist = "Test Artist", AssetType = AssetType.Track, Status = AssetStatus.Active }
        };
        _factory.AssetRepositoryMock.Setup(r => r.SearchAsync(It.IsAny<AssetSearchParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((items, 1));

        // Act
        var response = await client.GetAsync("/api/v1/assets?q=test&asset_type=TRACK&limit=10&offset=0");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AssetSearchEnvelopeDto>();
        body.Should().NotBeNull();
        body!.Total.Should().Be(1);
        body.Limit.Should().Be(10);
        body.Offset.Should().Be(0);
        body.Items.Should().ContainSingle();
        body.Items[0].AssetId.Should().Be("asset-1");
    }

    [Fact]
    public async Task ImportAsset_ShouldReturnAcceptedAndQueued()
    {
        // Arrange
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
        var dto = new ImportRequestDto("C:\\temp\\nonexistent.mp3", "TRACK", "Music", null, false, false, false, false);

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/assets/import", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadFromJsonAsync<ImportResponseDto>();
        body.Should().NotBeNull();
        body!.ImportId.Should().NotBeNullOrWhiteSpace();
        body.Status.Should().Be("QUEUED");
    }

    [Fact]
    public async Task GetImportStatus_ShouldReturnStatusOrNotFound()
    {
        // Arrange
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);

        // Act 1: Get random non-existent import ID
        var notFoundResponse = await client.GetAsync($"/api/v1/assets/import/{Guid.NewGuid()}");
        notFoundResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Act 2: Create a job first and then query its status
        var importDto = new ImportRequestDto("C:\\temp\\nonexistent2.mp3", "TRACK", "Music", null, false, false, false, false);
        var postResponse = await client.PostAsJsonAsync("/api/v1/assets/import", importDto);
        postResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var postBody = await postResponse.Content.ReadFromJsonAsync<ImportResponseDto>();

        var foundResponse = await client.GetAsync($"/api/v1/assets/import/{postBody!.ImportId}");
        
        // Assert 2
        foundResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var foundBody = await foundResponse.Content.ReadFromJsonAsync<ImportStatusDto>();
        foundBody.Should().NotBeNull();
        foundBody!.ImportId.Should().Be(postBody.ImportId);
    }

    [Fact]
    public async Task GetEventsNext_ShouldReturnNextEventOrNoContent()
    {
        // Arrange
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);

        // Scenario A: No enabled events
        _factory.ScheduledEventRepositoryMock.Setup(r => r.GetEnabledAsync("test-studio-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScheduledEvent>());

        var responseA = await client.GetAsync("/api/v1/events/next");
        responseA.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Scenario B: Has next firing event
        var futureEvent = new ScheduledEvent
        {
            EventId = "event-future",
            StudioId = "test-studio-id",
            Name = "Future Event",
            EventType = ScheduledEventType.OneTime,
            OnlyOnDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)),
            EventHour = new TimeSpan(12, 0, 0),
            Enabled = true,
            Days = "MON",
            Hours = "12",
            Actions = "[]"
        };
        _factory.ScheduledEventRepositoryMock.Setup(r => r.GetEnabledAsync("test-studio-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScheduledEvent> { futureEvent });

        var responseB = await client.GetAsync("/api/v1/events/next");
        responseB.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await responseB.Content.ReadFromJsonAsync<NextScheduledEventDto>();
        body.Should().NotBeNull();
        body!.EventId.Should().Be("event-future");
        body.RemainingMs.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetEvents_Admin_ShouldReturnOk()
    {
        // Arrange
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
        _factory.ScheduledEventRepositoryMock.Setup(r => r.GetByStudioAsync("test-studio-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScheduledEvent>());

        // Act
        var response = await client.GetAsync("/api/v1/events");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetEvents_Operator_ShouldReturnOk()
    {
        // Arrange
        var client = _factory.CreateClient().WithBasicAuth(OperatorUser, OperatorPass);
        _factory.ScheduledEventRepositoryMock.Setup(r => r.GetByStudioAsync("test-studio-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScheduledEvent>());

        // Act
        var response = await client.GetAsync("/api/v1/events");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostEvents_Admin_ShouldCreateAndReturnCreated()
    {
        // Arrange
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
        var dto = new ScheduledEventCreateDto(
            Name: "New Event",
            EventType: "OneTime",
            Category: "Event",
            Enabled: true,
            EventHour: "14:30:00",
            Days: new[] { "MON" },
            Hours: new[] { 14 },
            SmartTiming: true,
            Actions: new List<ScheduledEventActionDto>(),
            OnlyOnDate: "2026-07-10"
        );
        _factory.ScheduledEventRepositoryMock.Setup(r => r.CreateAsync(It.IsAny<ScheduledEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/events", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        var body = await response.Content.ReadFromJsonAsync<ScheduledEventCreatedResponseDto>();
        body.Should().NotBeNull();
        body!.Name.Should().Be("New Event");
        _factory.ScheduledEventRepositoryMock.Verify(r => r.CreateAsync(It.IsAny<ScheduledEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostEvents_OneTimeWithoutDate_ShouldReturnBadRequest()
    {
        // Arrange — ONE_TIME events require only_on_date; omitting it is invalid.
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
        var dto = new ScheduledEventCreateDto(
            Name: "No Date Event",
            EventType: "OneTime",
            Category: "Event",
            Enabled: true,
            EventHour: "14:30:00",
            Days: Array.Empty<string>(),
            Hours: Array.Empty<int>(),
            SmartTiming: false,
            Actions: new List<ScheduledEventActionDto>()
        );

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/events", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostEvents_Operator_ShouldReturnForbidden()
    {
        // Arrange
        var client = _factory.CreateClient().WithBasicAuth(OperatorUser, OperatorPass);
        var dto = new ScheduledEventCreateDto("Operator Event", "REPEAT", "Event", true, "12:00:00", new[] { "MON" }, new[] { 12 }, false, new List<ScheduledEventActionDto>());

        // Act
        var response = await client.PostAsJsonAsync("/api/v1/events", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DeleteEvent_Operator_ShouldReturnForbidden()
    {
        // Arrange
        var client = _factory.CreateClient().WithBasicAuth(OperatorUser, OperatorPass);

        // Act
        var response = await client.DeleteAsync("/api/v1/events/event-1");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PatchEvent_Operator_ValidFields_ShouldReturnOk()
    {
        // Arrange
        var client = _factory.CreateClient().WithBasicAuth(OperatorUser, OperatorPass);
        _factory.ScheduledEventRepositoryMock.Setup(r => r.GetByIdAsync("event-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScheduledEvent { EventId = "event-1", Name = "Ev 1" });
        _factory.ScheduledEventRepositoryMock.Setup(r => r.UpdateSkipNextAsync("event-1", true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var dto = new ScheduledEventPatchDto(Enabled: null, SkipNext: true);

        // Act
        var response = await client.PatchAsJsonAsync("/api/v1/events/event-1", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.ScheduledEventRepositoryMock.Verify(r => r.UpdateSkipNextAsync("event-1", true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PatchEvent_Operator_InvalidFields_ShouldReturnForbidden()
    {
        // Arrange
        var client = _factory.CreateClient().WithBasicAuth(OperatorUser, OperatorPass);
        var dto = new ScheduledEventPatchDto(Enabled: false, SkipNext: true);

        // Act
        var response = await client.PatchAsJsonAsync("/api/v1/events/event-1", dto);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task RequestWithoutAuthHeader_ShouldReturnUnauthorized()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync("/api/v1/events");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Contains("WWW-Authenticate").Should().BeTrue();
    }

    [Fact]
    public async Task RequestWithInvalidCredentials_ShouldReturnUnauthorized()
    {
        // Arrange
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, "wrongpassword");

        // Act
        var response = await client.GetAsync("/api/v1/events");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnhandledException_ShouldReturnInternalServerError()
    {
        // Arrange
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
        _factory.AssetRepositoryMock.Setup(r => r.GetByIdAsync("throw-id", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Simulated database collapse."));

        // Act
        var response = await client.GetAsync("/api/v1/assets/throw-id");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        body.Should().NotBeNull();
        body!.ErrorCode.Should().Be("INTERNAL_ERROR");
        body.Message.Should().Be("An unexpected internal error occurred.");
        body.TraceId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetAssetById_NotFound_ShouldReturnNotFound()
    {
        // Arrange
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
        _factory.AssetRepositoryMock.Setup(r => r.GetByIdAsync("missing-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Asset)null!);

        // Act
        var response = await client.GetAsync("/api/v1/assets/missing-id");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        body.Should().NotBeNull();
        body!.ErrorCode.Should().Be("ASSET_NOT_FOUND");
    }

    [Fact]
    public async Task RequestWithoutAuthHeader_WithLanBypassEnabled_ShouldAuthenticateAsOperator()
    {
        // Arrange: Recreate host with AnonymousLocal enabled
        _factory.ScheduledEventRepositoryMock.Setup(r => r.GetByStudioAsync("test-studio-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScheduledEvent>());

        var factoryWithBypass = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                var customSettings = new AudioSettings
                {
                    SettingsId = "test-settings-id",
                    StudioId = "test-studio-id",
                    ApiAuthEnabled = true,
                    ApiAnonymousLocal = true // LAN IP Bypass enabled
                };
                
                var mockSettingsRepo = new Mock<IAudioSettingsRepository>();
                mockSettingsRepo.Setup(r => r.GetByStudioAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(customSettings);
                
                services.AddScoped(_ => mockSettingsRepo.Object);
                services.AddSingleton<IStartupFilter, TestIpStartupFilter>();
            });
        });

        var client = factoryWithBypass.CreateClient(); // No basic auth header!

        // Act: Operator can GET events, but cannot POST events
        var getResponse = await client.GetAsync("/api/v1/events");
        var postResponse = await client.PostAsJsonAsync("/api/v1/events", new ScheduledEventCreateDto("Op Event", "REPEAT", "Event", true, "12:00:00", new[] { "MON" }, new[] { 12 }, false, new List<ScheduledEventActionDto>()));

        // Assert
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        postResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── PlaylistItems — IsDamaged detection ───────────────────────────────────

    private static readonly NowPlayingInfo IdleInfo = new(
        CurrentAsset: null, PositionMs: 0, OutroCueMs: null, TrackStartedAt: null,
        NextItem: null, Mode: PlaylistMode.Auto, State: SessionState.Idle, CurrentItemId: null);

    private static PlaylistItem MakeAssetItem(string itemId, string assetId) => new()
    {
        ItemId     = itemId,
        AssetId    = assetId,
        Position   = 0,
        ItemType   = PlaylistItemType.Asset,
        SegueType  = SegueType.Auto,
        PlaylistId = "pl-1"
    };

    [Fact]
    public async Task GetPlaylistItems_AssetNotInDb_IsDamagedTrue()
    {
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
        _factory.PlaylistControllerMock
            .Setup(p => p.GetCurrentItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeAssetItem("item-1", "missing-asset") });
        _factory.PlaylistControllerMock.Setup(p => p.GetNowPlayingInfo()).Returns(IdleInfo);
        _factory.AssetRepositoryMock
            .Setup(r => r.GetByIdAsync("missing-asset", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Asset?)null);

        var response = await client.GetAsync("/api/v1/playlist/items");
        var body = await response.Content.ReadFromJsonAsync<PlaylistItemsEnvelopeDto>();

        body!.Items.Should().ContainSingle();
        body.Items[0].IsDamaged.Should().BeTrue();
    }

    [Fact]
    public async Task GetPlaylistItems_AssetHasComments_CommentsExposedForTooltip()
    {
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
        _factory.PlaylistControllerMock
            .Setup(p => p.GetCurrentItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeAssetItem("item-1", "asset-noted") });
        _factory.PlaylistControllerMock.Setup(p => p.GetNowPlayingInfo()).Returns(IdleInfo);
        _factory.AssetRepositoryMock
            .Setup(r => r.GetByIdAsync("asset-noted", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Asset { AssetId = "asset-noted", Title = "Noted", DurationMs = 180_000,
                Comments = "Fade out early — talk over intro" });

        var response = await client.GetAsync("/api/v1/playlist/items");
        var body = await response.Content.ReadFromJsonAsync<PlaylistItemsEnvelopeDto>();

        body!.Items[0].Comments.Should().Be("Fade out early — talk over intro");
    }

    [Fact]
    public async Task GetPlaylistItems_AssetWithoutComments_CommentsNull()
    {
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
        _factory.PlaylistControllerMock
            .Setup(p => p.GetCurrentItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeAssetItem("item-1", "asset-plain") });
        _factory.PlaylistControllerMock.Setup(p => p.GetNowPlayingInfo()).Returns(IdleInfo);
        _factory.AssetRepositoryMock
            .Setup(r => r.GetByIdAsync("asset-plain", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Asset { AssetId = "asset-plain", Title = "Plain", DurationMs = 180_000 });

        var response = await client.GetAsync("/api/v1/playlist/items");
        var body = await response.Content.ReadFromJsonAsync<PlaylistItemsEnvelopeDto>();

        body!.Items[0].Comments.Should().BeNull();
    }

    [Fact]
    public async Task GetPlaylistItems_AssetHasNoFilePath_IsDamagedTrue()
    {
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
        _factory.PlaylistControllerMock
            .Setup(p => p.GetCurrentItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeAssetItem("item-1", "asset-no-path") });
        _factory.PlaylistControllerMock.Setup(p => p.GetNowPlayingInfo()).Returns(IdleInfo);
        _factory.AssetRepositoryMock
            .Setup(r => r.GetByIdAsync("asset-no-path", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Asset { AssetId = "asset-no-path", Title = "No Path", DurationMs = 180_000, RdmFilePath = null });

        var response = await client.GetAsync("/api/v1/playlist/items");
        var body = await response.Content.ReadFromJsonAsync<PlaylistItemsEnvelopeDto>();

        body!.Items[0].IsDamaged.Should().BeTrue();
    }

    [Fact]
    public async Task GetPlaylistItems_AssetFileNotOnDisk_IsDamagedTrue()
    {
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
        _factory.PlaylistControllerMock
            .Setup(p => p.GetCurrentItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeAssetItem("item-1", "asset-ghost") });
        _factory.PlaylistControllerMock.Setup(p => p.GetNowPlayingInfo()).Returns(IdleInfo);
        _factory.AssetRepositoryMock
            .Setup(r => r.GetByIdAsync("asset-ghost", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Asset { AssetId = "asset-ghost", Title = "Ghost", DurationMs = 180_000,
                RdmFilePath = @"Z:\nonexistent\ghost_file_rdm_test.mp3" });

        var response = await client.GetAsync("/api/v1/playlist/items");
        var body = await response.Content.ReadFromJsonAsync<PlaylistItemsEnvelopeDto>();

        body!.Items[0].IsDamaged.Should().BeTrue();
    }

    [Fact]
    public async Task GetPlaylistItems_AssetMarkedDamagedInDb_IsDamagedTrue()
    {
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
        _factory.PlaylistControllerMock
            .Setup(p => p.GetCurrentItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeAssetItem("item-1", "asset-dmg") });
        _factory.PlaylistControllerMock.Setup(p => p.GetNowPlayingInfo()).Returns(IdleInfo);
        _factory.AssetRepositoryMock
            .Setup(r => r.GetByIdAsync("asset-dmg", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Asset { AssetId = "asset-dmg", Title = "Damaged", DurationMs = 0,
                RdmFilePath = @"Z:\nonexistent\damaged.mp3", IsDamaged = true });

        var response = await client.GetAsync("/api/v1/playlist/items");
        var body = await response.Content.ReadFromJsonAsync<PlaylistItemsEnvelopeDto>();

        body!.Items[0].IsDamaged.Should().BeTrue();
    }

    // ── AUX — Stop applies the configured global fade-out ─────────────────────

    [Fact]
    public async Task PostAuxStop_UsesConfiguredAuxFadeoutMs()
    {
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
        _factory.AudioSettingsRepositoryMock
            .Setup(r => r.GetByStudioAsync("test-studio-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AudioSettings { SettingsId = "s1", StudioId = "test-studio-id", AuxFadeoutMs = 1500 });

        var response = await client.PostAsync("/api/v1/aux/0/stop", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.AudioEngineMock.Verify(e => e.StopAuxAsync(0, 1500u, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PostAuxStop_NoConfiguredSettings_DefaultsToZeroFadeout()
    {
        var client = _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
        _factory.AudioSettingsRepositoryMock
            .Setup(r => r.GetByStudioAsync("test-studio-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync((AudioSettings?)null);

        var response = await client.PostAsync("/api/v1/aux/1/stop", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.AudioEngineMock.Verify(e => e.StopAuxAsync(1, 0u, It.IsAny<CancellationToken>()), Times.Once);
    }

    private class TestIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use((context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
                    return nextMiddleware();
                });
                next(app);
            };
        }
    }
}
