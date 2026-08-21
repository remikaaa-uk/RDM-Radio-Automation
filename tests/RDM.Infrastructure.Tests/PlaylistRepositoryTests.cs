using Dapper;
using FluentAssertions;
using RDM.Core.Entities;
using RDM.Infrastructure.Repositories;
using RDM.Infrastructure.Tests.Infrastructure;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Infrastructure.Tests;

[Collection("MariaDb")]
public sealed class PlaylistRepositoryTests : IAsyncLifetime
{
    private readonly MariaDbTestFixture _fixture;
    private readonly PlaylistRepository _sut;
    private readonly List<string> _playlistIds = new();

    public PlaylistRepositoryTests(MariaDbTestFixture fixture)
    {
        _fixture = fixture;
        _sut = new PlaylistRepository(fixture.Factory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_playlistIds.Count == 0) return;
        using var conn = _fixture.Factory.CreateConnection();
        // playlist_items are cascade-deleted when playlist is deleted
        await conn.ExecuteAsync(
            "DELETE FROM playlists WHERE playlist_id IN @Ids", new { Ids = _playlistIds });
    }

    [Fact]
    public async Task Create_ShouldInsert()
    {
        var id = NewId();
        var playlist = BuildPlaylist(id, "Morning Show");

        await _sut.CreateAsync(playlist);

        var result = await _sut.GetByIdAsync(id);
        result.Should().NotBeNull();
        result!.PlaylistId.Should().Be(id);
        result.Name.Should().Be("Morning Show");
        result.StudioId.Should().Be(_fixture.StudioId);
    }

    [Fact]
    public async Task GetById_ShouldReturn()
    {
        var id = NewId();
        await _sut.CreateAsync(BuildPlaylist(id, "Evening Show"));

        var result = await _sut.GetByIdAsync(id);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Evening Show");
        result.StudioId.Should().Be(_fixture.StudioId);
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
        // Playlist has no UpdateAsync in IPlaylistRepository — items are updated via UpdateItemAsync.
        // This test verifies AddItem + UpdateItem instead.
        var playlistId = NewId();
        await _sut.CreateAsync(BuildPlaylist(playlistId, "Original Playlist"));

        var item = BuildItem(playlistId, position: 1, title: "First Item");
        await _sut.AddItemAsync(item);

        var updated = new PlaylistItem
        {
            ItemId       = item.ItemId,
            PlaylistId   = item.PlaylistId,
            Position     = item.Position,
            ItemType     = item.ItemType,
            DummyLabel   = "Renamed Item",
            SegueType    = item.SegueType,
            AutoLinkNext = item.AutoLinkNext
        };
        await _sut.UpdateItemAsync(updated);

        var items = await _sut.GetItemsAsync(playlistId);
        items.Should().HaveCount(1);
        items[0].DummyLabel.Should().Be("Renamed Item");
    }

    [Fact]
    public async Task GetByStudio_ShouldReturnAllPlaylists()
    {
        var id1 = NewId();
        var id2 = NewId();
        await _sut.CreateAsync(BuildPlaylist(id1, "Playlist A"));
        await _sut.CreateAsync(BuildPlaylist(id2, "Playlist B"));

        var result = await _sut.GetByStudioAsync(_fixture.StudioId);

        result.Should().Contain(p => p.PlaylistId == id1)
              .And.Contain(p => p.PlaylistId == id2);
    }

    [Fact]
    public async Task AddItem_ShouldInsertItem()
    {
        var playlistId = NewId();
        await _sut.CreateAsync(BuildPlaylist(playlistId, "With Items"));

        var item = BuildItem(playlistId, position: 1);
        await _sut.AddItemAsync(item);

        var items = await _sut.GetItemsAsync(playlistId);
        items.Should().HaveCount(1);
        items[0].ItemId.Should().Be(item.ItemId);
        items[0].Position.Should().Be(1u);
        items[0].ItemType.Should().Be(PlaylistItemType.Dummy);
        items[0].SegueType.Should().Be(SegueType.Auto);
    }

    [Fact]
    public async Task RemoveItem_ShouldDeleteItem()
    {
        var playlistId = NewId();
        await _sut.CreateAsync(BuildPlaylist(playlistId, "Remove Test"));
        var item = BuildItem(playlistId, position: 1);
        await _sut.AddItemAsync(item);

        await _sut.RemoveItemAsync(item.ItemId);

        var items = await _sut.GetItemsAsync(playlistId);
        items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetItems_ShouldReturnOrderedByPosition()
    {
        var playlistId = NewId();
        await _sut.CreateAsync(BuildPlaylist(playlistId, "Ordered"));
        await _sut.AddItemAsync(BuildItem(playlistId, position: 3, title: "Third"));
        await _sut.AddItemAsync(BuildItem(playlistId, position: 1, title: "First"));
        await _sut.AddItemAsync(BuildItem(playlistId, position: 2, title: "Second"));

        var items = await _sut.GetItemsAsync(playlistId);

        items.Should().HaveCount(3);
        items[0].Position.Should().Be(1u);
        items[1].Position.Should().Be(2u);
        items[2].Position.Should().Be(3u);
    }

    [Fact]
    public async Task Delete_ShouldCascadeItems()
    {
        var playlistId = NewId();
        await _sut.CreateAsync(BuildPlaylist(playlistId, "To Delete"));
        await _sut.AddItemAsync(BuildItem(playlistId, position: 1));

        await _sut.DeleteAsync(playlistId);

        var result = await _sut.GetByIdAsync(playlistId);
        result.Should().BeNull();

        var items = await _sut.GetItemsAsync(playlistId);
        items.Should().BeEmpty();
    }

    private string NewId()
    {
        var id = Guid.NewGuid().ToString();
        _playlistIds.Add(id);
        return id;
    }

    private Playlist BuildPlaylist(string id, string name) => new()
    {
        PlaylistId = id,
        StudioId   = _fixture.StudioId,
        Name       = name,
        CreatedAt  = DateTime.UtcNow
    };

    private static PlaylistItem BuildItem(string playlistId, uint position, string? title = null) => new()
    {
        ItemId     = Guid.NewGuid().ToString(),
        PlaylistId = playlistId,
        AssetId    = null,
        Position   = position,
        ItemType   = PlaylistItemType.Dummy,
        DummyLabel = title ?? "Dummy",
        SegueType  = SegueType.Auto,
        AutoLinkNext = false
    };
}
