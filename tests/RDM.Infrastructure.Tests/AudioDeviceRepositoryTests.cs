using Dapper;
using FluentAssertions;
using RDM.Core.Entities;
using RDM.Infrastructure.Repositories;
using RDM.Infrastructure.Tests.Infrastructure;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Infrastructure.Tests;

[Collection("MariaDb")]
public sealed class AudioDeviceRepositoryTests : IAsyncLifetime
{
    private readonly MariaDbTestFixture _fixture;
    private readonly AudioDeviceRepository _sut;
    private readonly List<string> _deviceIds = new();

    public AudioDeviceRepositoryTests(MariaDbTestFixture fixture)
    {
        _fixture = fixture;
        _sut = new AudioDeviceRepository(fixture.Factory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_deviceIds.Count == 0) return;
        using var conn = _fixture.Factory.CreateConnection();
        await conn.ExecuteAsync(
            "DELETE FROM audio_devices WHERE device_id IN @Ids", new { Ids = _deviceIds });
    }

    [Fact]
    public async Task Create_ShouldInsert()
    {
        var id     = NewId();
        var device = BuildDevice(id, "WASAPI Primary Out", DriverType.WasapiExclusive);

        await _sut.UpsertAsync(device);

        var result = await _sut.GetByIdAsync(id);
        result.Should().NotBeNull();
        result!.DeviceId.Should().Be(id);
        result.FriendlyName.Should().Be("WASAPI Primary Out");
        result.DriverType.Should().Be(DriverType.WasapiExclusive);
        result.StudioId.Should().Be(_fixture.StudioId);
        result.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task GetById_ShouldReturn()
    {
        var id     = NewId();
        var sysId  = Guid.NewGuid().ToString();
        var device = BuildDevice(id, "ASIO Device", DriverType.Asio, systemDeviceId: sysId);
        await _sut.UpsertAsync(device);

        var result = await _sut.GetByIdAsync(id);

        result.Should().NotBeNull();
        result!.SystemDeviceId.Should().Be(sysId);
        result.DriverType.Should().Be(DriverType.Asio);
        result.FriendlyName.Should().Be("ASIO Device");
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
        await _sut.UpsertAsync(BuildDevice(id, "Old Name", DriverType.WasapiShared));

        var updated = BuildDevice(id, "New Name", DriverType.DirectSound, isAvailable: false);
        await _sut.UpsertAsync(updated);

        var result = await _sut.GetByIdAsync(id);
        result!.FriendlyName.Should().Be("New Name");
        result.DriverType.Should().Be(DriverType.DirectSound);
        result.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAvailability_ShouldChangeState()
    {
        var id     = NewId();
        var seenAt = DateTime.UtcNow;
        await _sut.UpsertAsync(BuildDevice(id, "Toggleable", DriverType.WasapiExclusive, isAvailable: false));

        await _sut.UpdateAvailabilityAsync(id, isAvailable: true, lastSeenAt: seenAt);

        var result = await _sut.GetByIdAsync(id);
        result!.IsAvailable.Should().BeTrue();
        result.LastSeenAt.Should().BeCloseTo(seenAt, precision: TimeSpan.FromSeconds(1));
    }

    private string NewId()
    {
        var id = Guid.NewGuid().ToString();
        _deviceIds.Add(id);
        return id;
    }

    private AudioDevice BuildDevice(
        string     id,
        string     friendlyName,
        DriverType driverType,
        string?    systemDeviceId = null,
        bool       isAvailable    = true) => new()
    {
        DeviceId       = id,
        StudioId       = _fixture.StudioId,
        SystemDeviceId = systemDeviceId ?? Guid.NewGuid().ToString(),
        FriendlyName   = friendlyName,
        DriverType     = driverType,
        IsAvailable    = isAvailable
    };
}
