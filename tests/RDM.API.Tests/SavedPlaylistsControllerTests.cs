using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using RDM.Core.Entities;
using RDM.Shared.DTOs;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.API.Tests;

/// Covers PUT /api/v1/playlists/{id} — the overwrite path used by "save playlist"
/// in playout and in the playlist builder.
public class SavedPlaylistsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private const string OperatorUser = "playlistoperator";
    private const string OperatorPass = "oppassword";

    private const string ExistingId = "playlist-1";

    public SavedPlaylistsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;

        _factory.UserRepositoryMock
            .Setup(r => r.GetByUsernameAsync("test-studio-id", OperatorUser, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User
            {
                UserId       = "playlist-op-id",
                StudioId     = "test-studio-id",
                Username     = OperatorUser,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(OperatorPass, workFactor: 4),
                Role         = UserRole.Operator,
                Enabled      = true
            });

        _factory.PlaylistRepositoryMock.Reset();
        _factory.PlaylistRepositoryMock
            .Setup(r => r.GetByIdAsync(ExistingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ExistingPlaylist);
    }

    private static Playlist ExistingPlaylist => new()
    {
        PlaylistId   = ExistingId,
        StudioId     = "test-studio-id",
        Name         = "Poranek",
        PlaylistType = PlaylistType.Saved,
        CreatedAt    = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)
    };

    private HttpClient Client() => _factory.CreateClient().WithBasicAuth(OperatorUser, OperatorPass);

    private static PlaylistItemSaveDto Item(string assetId) => new(
        AssetId:         assetId,
        ItemType:        "ASSET",
        DummyLabel:      null,
        DummyNote:       null,
        DummyDurationMs: null,
        CrossfadeMs:     null,
        TrimStartMs:     null,
        TrimEndMs:       null,
        SegueType:       "AUTO",
        AutoLinkNext:    false);

    [Fact]
    public async Task Update_UnknownPlaylist_ReturnsNotFound()
    {
        _factory.PlaylistRepositoryMock
            .Setup(r => r.GetByIdAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Playlist?)null);

        var resp = await Client().PutAsJsonAsync("/api/v1/playlists/missing",
            new SavePlaylistRequestDto("Whatever", new[] { Item("asset-1") }));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.PlaylistRepositoryMock.Verify(
            r => r.ClearItemsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_WithBlankName_ReturnsBadRequestAndKeepsItems()
    {
        var resp = await Client().PutAsJsonAsync($"/api/v1/playlists/{ExistingId}",
            new SavePlaylistRequestDto("   ", new[] { Item("asset-1") }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.PlaylistRepositoryMock.Verify(
            r => r.ClearItemsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_ReplacesItemsInOrderAndRenamesPlaylist()
    {
        var added = new List<PlaylistItem>();
        _factory.PlaylistRepositoryMock
            .Setup(r => r.AddItemAsync(It.IsAny<PlaylistItem>(), It.IsAny<CancellationToken>()))
            .Callback<PlaylistItem, CancellationToken>((item, _) => added.Add(item))
            .Returns(Task.CompletedTask);

        var resp = await Client().PutAsJsonAsync($"/api/v1/playlists/{ExistingId}",
            new SavePlaylistRequestDto("Poranek v2", new[] { Item("asset-a"), Item("asset-b"), Item("asset-c") }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.PlaylistRepositoryMock.Verify(
            r => r.UpdateNameAsync(ExistingId, "Poranek v2", It.IsAny<CancellationToken>()), Times.Once);
        _factory.PlaylistRepositoryMock.Verify(
            r => r.ClearItemsAsync(ExistingId, It.IsAny<CancellationToken>()), Times.Once);

        added.Should().HaveCount(3);
        added.Select(i => i.AssetId).Should().Equal("asset-a", "asset-b", "asset-c");
        added.Select(i => i.Position).Should().Equal(0u, 1u, 2u);
        added.Should().OnlyContain(i => i.PlaylistId == ExistingId);
    }

    [Fact]
    public async Task Update_KeepsIdAndCreationDate()
    {
        var resp = await Client().PutAsJsonAsync($"/api/v1/playlists/{ExistingId}",
            new SavePlaylistRequestDto("Poranek v2", new[] { Item("asset-a") }));

        var body = await resp.Content.ReadFromJsonAsync<SavePlaylistResponseDto>();

        body!.PlaylistId.Should().Be(ExistingId);
        body.Name.Should().Be("Poranek v2");
        body.CreatedAt.Should().Be(ExistingPlaylist.CreatedAt);
    }

    [Fact]
    public async Task Update_WithEmptyItemList_ClearsThePlaylist()
    {
        var resp = await Client().PutAsJsonAsync($"/api/v1/playlists/{ExistingId}",
            new SavePlaylistRequestDto("Pusta", Array.Empty<PlaylistItemSaveDto>()));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.PlaylistRepositoryMock.Verify(
            r => r.ClearItemsAsync(ExistingId, It.IsAny<CancellationToken>()), Times.Once);
        _factory.PlaylistRepositoryMock.Verify(
            r => r.AddItemAsync(It.IsAny<PlaylistItem>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
