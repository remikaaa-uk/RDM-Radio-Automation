using Dapper;
using FluentAssertions;
using RDM.Core.Entities;
using RDM.Infrastructure.Repositories;
using RDM.Infrastructure.Tests.Infrastructure;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Infrastructure.Tests;

[Collection("MariaDb")]
public sealed class AssetRepositoryTests : IAsyncLifetime
{
    private readonly MariaDbTestFixture _fixture;
    private readonly AssetRepository _sut;
    private readonly List<string> _assetIds = new();

    public AssetRepositoryTests(MariaDbTestFixture fixture)
    {
        _fixture = fixture;
        _sut = new AssetRepository(fixture.Factory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_assetIds.Count == 0) return;
        using var conn = _fixture.Factory.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM assets WHERE asset_id IN @Ids", new { Ids = _assetIds });
    }

    [Fact]
    public async Task Create_ShouldInsert()
    {
        var id = NewId();
        var asset = BuildAsset(id, "Never Gonna Give You Up", "Rick Astley");

        await _sut.CreateAsync(asset);

        var result = await _sut.GetByIdAsync(id);
        result.Should().NotBeNull();
        result!.AssetId.Should().Be(id);
        result.Title.Should().Be("Never Gonna Give You Up");
        result.Artist.Should().Be("Rick Astley");
        result.AssetType.Should().Be(AssetType.Track);
        result.Status.Should().Be(AssetStatus.Active);
        result.DurationMs.Should().Be(213000u);
        result.Checksum.Should().Be(asset.Checksum);
    }

    [Fact]
    public async Task GetById_ShouldReturn()
    {
        var id = NewId();
        var asset = BuildAsset(id, "Bohemian Rhapsody", "Queen");
        await _sut.CreateAsync(asset);

        var result = await _sut.GetByIdAsync(id);

        result.Should().NotBeNull();
        result!.Title.Should().Be("Bohemian Rhapsody");
        result.Artist.Should().Be("Queen");
        result.AssetType.Should().Be(AssetType.Track);
        result.DurationMs.Should().Be(213000u);
        result.IsDamaged.Should().BeFalse();
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
        await _sut.CreateAsync(BuildAsset(id, "Original Title", "Original Artist"));

        var updated = BuildAsset(id, "Updated Title", "Updated Artist",
            status: AssetStatus.Disabled, checksum: Guid.NewGuid().ToString("N"));
        await _sut.UpdateAsync(updated);

        var result = await _sut.GetByIdAsync(id);
        result!.Title.Should().Be("Updated Title");
        result.Artist.Should().Be("Updated Artist");
        result.Status.Should().Be(AssetStatus.Disabled);
    }

    [Fact]
    public async Task GetByChecksum_ShouldReturn()
    {
        var id = NewId();
        var checksum = Guid.NewGuid().ToString("N");
        await _sut.CreateAsync(BuildAsset(id, "Track X", checksum: checksum));

        var result = await _sut.GetByChecksumAsync(checksum);

        result.Should().NotBeNull();
        result!.AssetId.Should().Be(id);
    }

    [Fact]
    public async Task UpdateStatus_ShouldChangeStatus()
    {
        var id = NewId();
        await _sut.CreateAsync(BuildAsset(id, "Active Track"));

        await _sut.UpdateStatusAsync(id, AssetStatus.Disabled);

        var result = await _sut.GetByIdAsync(id);
        result!.Status.Should().Be(AssetStatus.Disabled);
    }

    private string NewId()
    {
        var id = Guid.NewGuid().ToString();
        _assetIds.Add(id);
        return id;
    }

    private static Asset BuildAsset(
        string id,
        string title,
        string? artist  = null,
        AssetStatus status   = AssetStatus.Active,
        string? checksum = null) => new()
    {
        AssetId    = id,
        AssetType  = AssetType.Track,
        Title      = title,
        Artist     = artist,
        DurationMs = 213000,
        Checksum   = checksum ?? Guid.NewGuid().ToString("N"),
        Status     = status,
        CreatedAt  = DateTime.UtcNow,
        UpdatedAt  = DateTime.UtcNow
    };
}
