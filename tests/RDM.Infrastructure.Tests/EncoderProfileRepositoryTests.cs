using Dapper;
using FluentAssertions;
using RDM.Core.Entities;
using RDM.Infrastructure.Repositories;
using RDM.Infrastructure.Tests.Infrastructure;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Infrastructure.Tests;

[Collection("MariaDb")]
public sealed class EncoderProfileRepositoryTests : IAsyncLifetime
{
    private readonly MariaDbTestFixture _fixture;
    private readonly EncoderProfileRepository _sut;
    private readonly List<string> _studioIds = new();

    public EncoderProfileRepositoryTests(MariaDbTestFixture fixture)
    {
        _fixture = fixture;
        _sut = new EncoderProfileRepository(fixture.Factory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_studioIds.Count == 0) return;
        using var conn = _fixture.Factory.CreateConnection();
        // encoder_profiles cascade-deletes with studio
        await conn.ExecuteAsync(
            "DELETE FROM studios WHERE studio_id IN @Ids", new { Ids = _studioIds });
    }

    [Fact]
    public async Task Create_ShouldRoundTripEveryField()
    {
        var studioId = await CreateTempStudioAsync();
        var profile  = BuildProfile(studioId);

        await _sut.CreateAsync(profile);
        var result = await _sut.GetByIdAsync(profile.ProfileId);

        result.Should().NotBeNull();
        result!.Name.Should().Be(profile.Name);
        result.Format.Should().Be(EncoderFormat.Mp3);
        result.BitrateKbps.Should().Be(128);
        result.SampleRateHz.Should().Be(44100);
        result.Channels.Should().Be((byte)2);
        result.ServerType.Should().Be(CastServerType.Icecast);
        result.Host.Should().Be("stream.example.org");
        result.Port.Should().Be(8000);
        result.Mount.Should().Be("/live");
        result.UseSsl.Should().BeTrue();
        result.StreamName.Should().Be("Test Stream");
        result.Genre.Should().Be("Talk");
        result.StreamUrl.Should().Be("https://example.org");
        result.Description.Should().Be("Opis testowy");
        result.IsPublic.Should().BeTrue();
        result.Enabled.Should().BeTrue();
        result.AutoStart.Should().BeFalse();
    }

    /// <summary>
    /// The password is a DPAPI blob in a VARBINARY column — the one field where a silent
    /// mapping bug would only show up as a failed connection much later.
    /// </summary>
    [Fact]
    public async Task Create_ShouldRoundTripPasswordBlobByteForByte()
    {
        var studioId = await CreateTempStudioAsync();
        var blob     = new byte[] { 0x00, 0x01, 0xFF, 0x7F, 0x80, 0x00, 0xAB };
        var profile  = BuildProfile(studioId, passwordEncrypted: blob);

        await _sut.CreateAsync(profile);
        var result = await _sut.GetByIdAsync(profile.ProfileId);

        result!.PasswordEncrypted.Should().Equal(blob);
    }

    [Fact]
    public async Task Create_ShouldAllowNullOptionalFields()
    {
        var studioId = await CreateTempStudioAsync();
        var profile  = BuildProfile(studioId,
            sampleRateHz: null, mount: null, passwordEncrypted: null);

        await _sut.CreateAsync(profile);
        var result = await _sut.GetByIdAsync(profile.ProfileId);

        result!.SampleRateHz.Should().BeNull();
        result.Mount.Should().BeNull();
        result.PasswordEncrypted.Should().BeNull();
        result.UpdatedAt.Should().BeNull();
    }

    [Fact]
    public async Task GetByStudio_ShouldReturnAllOrderedByName()
    {
        var studioId = await CreateTempStudioAsync();
        await _sut.CreateAsync(BuildProfile(studioId, name: "Zulu"));
        await _sut.CreateAsync(BuildProfile(studioId, name: "Alfa"));

        var result = await _sut.GetByStudioAsync(studioId);

        result.Should().HaveCount(2);
        result.Select(p => p.Name).Should().ContainInOrder("Alfa", "Zulu");
    }

    [Fact]
    public async Task GetAutoStart_ShouldReturnOnlyEnabledAutoStartProfiles()
    {
        var studioId = await CreateTempStudioAsync();
        await _sut.CreateAsync(BuildProfile(studioId, name: "on",       enabled: true,  autoStart: true));
        await _sut.CreateAsync(BuildProfile(studioId, name: "manual",   enabled: true,  autoStart: false));
        await _sut.CreateAsync(BuildProfile(studioId, name: "disabled", enabled: false, autoStart: true));

        var result = await _sut.GetAutoStartAsync(studioId);

        result.Should().ContainSingle();
        result[0].Name.Should().Be("on");
    }

    // Armed and auto_start answer different questions; the queries behind them must not overlap.
    [Fact]
    public async Task GetArmed_ShouldReturnOnlyEnabledArmedProfiles()
    {
        var studioId = await CreateTempStudioAsync();
        await _sut.CreateAsync(BuildProfile(studioId, name: "armed",    enabled: true,  armed: true));
        await _sut.CreateAsync(BuildProfile(studioId, name: "idle",     enabled: true,  armed: false));
        await _sut.CreateAsync(BuildProfile(studioId, name: "disabled", enabled: false, armed: true));

        var result = await _sut.GetArmedAsync(studioId);

        result.Should().ContainSingle();
        result[0].Name.Should().Be("armed");
    }

    // A profile can be armed without auto-starting — that is the whole point of the split.
    [Fact]
    public async Task GetArmed_ShouldIncludeProfilesThatDoNotAutoStart()
    {
        var studioId = await CreateTempStudioAsync();
        await _sut.CreateAsync(BuildProfile(studioId, name: "manual", armed: true, autoStart: false));

        var armed     = await _sut.GetArmedAsync(studioId);
        var autoStart = await _sut.GetAutoStartAsync(studioId);

        armed.Should().ContainSingle();
        autoStart.Should().BeEmpty();
    }

    [Theory]
    [InlineData(StreamTitleMode.NowPlaying, null)]
    [InlineData(StreamTitleMode.Static, "Radio 40 Plus")]
    [InlineData(StreamTitleMode.None, null)]
    public async Task Create_ShouldRoundTripTitleSettings(StreamTitleMode mode, string? text)
    {
        var studioId = await CreateTempStudioAsync();
        var profile  = BuildProfile(studioId, titleMode: mode, titleText: text);
        await _sut.CreateAsync(profile);

        var result = await _sut.GetByIdAsync(profile.ProfileId);

        result!.TitleMode.Should().Be(mode);
        result.TitleText.Should().Be(text);
    }

    // SHOUTcast 2 addresses a stream by id and takes a username; Icecast takes a username and a
    // source method. All three have to survive the round trip or the connection string is built
    // from something the operator never entered.
    [Theory]
    [InlineData(CastServerType.Shoutcast,   null,     null, false)]
    [InlineData(CastServerType.ShoutcastV2, "dj",     "2",  false)]
    [InlineData(CastServerType.Icecast,     "source", null, true)]
    public async Task Create_ShouldRoundTripServerDetails(
        CastServerType serverType, string? username, string? streamId, bool usePut)
    {
        var studioId = await CreateTempStudioAsync();
        var profile  = BuildProfile(studioId,
            serverType: serverType, username: username, streamId: streamId, usePut: usePut);
        await _sut.CreateAsync(profile);

        var result = await _sut.GetByIdAsync(profile.ProfileId);

        result!.ServerType.Should().Be(serverType);
        result.Username.Should().Be(username);
        result.StreamId.Should().Be(streamId);
        result.UsePut.Should().Be(usePut);
    }

    // The reconnect interval is a plain INT column with a default; a non-default value must survive
    // the round trip, or an operator's chosen delay would silently revert on the next read.
    [Fact]
    public async Task Create_ShouldRoundTripReconnectDelay()
    {
        var studioId = await CreateTempStudioAsync();
        var profile  = BuildProfile(studioId, reconnectDelaySeconds: 25);
        await _sut.CreateAsync(profile);

        var result = await _sut.GetByIdAsync(profile.ProfileId);

        result!.ReconnectDelaySeconds.Should().Be(25);
    }

    [Fact]
    public async Task Update_ShouldPersistChanges()
    {
        var studioId = await CreateTempStudioAsync();
        var profile  = BuildProfile(studioId);
        await _sut.CreateAsync(profile);

        var updated = BuildProfile(studioId,
            profileId:  profile.ProfileId,
            name:       "Zmieniony",
            format:     EncoderFormat.Ogg,
            serverType: CastServerType.Shoutcast,
            updatedAt:  new DateTime(2026, 7, 20, 12, 0, 0));
        await _sut.UpdateAsync(updated);

        var result = await _sut.GetByIdAsync(profile.ProfileId);
        result!.Name.Should().Be("Zmieniony");
        result.Format.Should().Be(EncoderFormat.Ogg);
        result.ServerType.Should().Be(CastServerType.Shoutcast);
        result.UpdatedAt.Should().Be(new DateTime(2026, 7, 20, 12, 0, 0));
    }

    [Fact]
    public async Task Delete_ShouldRemoveProfile()
    {
        var studioId = await CreateTempStudioAsync();
        var profile  = BuildProfile(studioId);
        await _sut.CreateAsync(profile);

        await _sut.DeleteAsync(profile.ProfileId);

        (await _sut.GetByIdAsync(profile.ProfileId)).Should().BeNull();
    }

    [Fact]
    public async Task GetById_ShouldReturnNullWhenMissing()
        => (await _sut.GetByIdAsync(Guid.NewGuid().ToString())).Should().BeNull();

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

    private static EncoderProfile BuildProfile(
        string          studioId,
        string?         profileId         = null,
        string          name              = "Test MP3",
        EncoderFormat   format            = EncoderFormat.Mp3,
        CastServerType  serverType        = CastServerType.Icecast,
        int?            sampleRateHz      = 44100,
        string?         mount             = "/live",
        string?         username          = null,
        string?         streamId          = null,
        bool            usePut            = false,
        byte[]?         passwordEncrypted = null,
        bool            enabled           = true,
        bool            armed             = false,
        bool            autoStart         = false,
        int             reconnectDelaySeconds = 10,
        StreamTitleMode titleMode         = StreamTitleMode.NowPlaying,
        string?         titleText         = null,
        DateTime?       updatedAt         = null) => new()
    {
        ProfileId         = profileId ?? Guid.NewGuid().ToString(),
        StudioId          = studioId,
        Name              = name,
        Format            = format,
        BitrateKbps       = 128,
        SampleRateHz      = sampleRateHz,
        Channels          = 2,
        ServerType        = serverType,
        Host              = "stream.example.org",
        Port              = 8000,
        Mount             = mount,
        Username          = username,
        StreamId          = streamId,
        UsePut            = usePut,
        PasswordEncrypted = passwordEncrypted,
        UseSsl            = true,
        StreamName        = "Test Stream",
        Genre             = "Talk",
        StreamUrl         = "https://example.org",
        Description       = "Opis testowy",
        IsPublic          = true,
        Armed             = armed,
        TitleMode         = titleMode,
        TitleText         = titleText,
        Enabled           = enabled,
        AutoStart         = autoStart,
        ReconnectDelaySeconds = reconnectDelaySeconds,
        CreatedAt         = new DateTime(2026, 7, 20, 10, 0, 0),
        UpdatedAt         = updatedAt
    };
}
