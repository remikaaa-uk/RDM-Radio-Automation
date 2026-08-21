using Dapper;
using FluentAssertions;
using RDM.Core.Entities;
using RDM.Infrastructure.Repositories;
using RDM.Infrastructure.Tests.Infrastructure;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Infrastructure.Tests;

[Collection("MariaDb")]
public sealed class AudioSettingsRepositoryTests : IAsyncLifetime
{
    private readonly MariaDbTestFixture _fixture;
    private readonly AudioSettingsRepository _sut;
    private readonly List<string> _studioIds = new();

    public AudioSettingsRepositoryTests(MariaDbTestFixture fixture)
    {
        _fixture = fixture;
        _sut = new AudioSettingsRepository(fixture.Factory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_studioIds.Count == 0) return;
        using var conn = _fixture.Factory.CreateConnection();
        // audio_settings cascade-deletes with studio
        await conn.ExecuteAsync(
            "DELETE FROM studios WHERE studio_id IN @Ids", new { Ids = _studioIds });
    }

    [Fact]
    public async Task Create_ShouldInsert()
    {
        var studioId = await CreateTempStudioAsync();
        var settings = BuildSettings(Guid.NewGuid().ToString(), studioId);

        await _sut.CreateAsync(settings);

        var result = await _sut.GetByStudioAsync(studioId);
        result.Should().NotBeNull();
        result!.SettingsId.Should().Be(settings.SettingsId);
        result.StudioId.Should().Be(studioId);
        result.SampleRate.Should().Be(AudioSampleRate.Hz48000);
        result.BufferSize.Should().Be(AudioBufferSize.Samples512);
        result.OutputMode.Should().Be(DriverType.WasapiExclusive);
        result.Theme.Should().Be(AppTheme.Dark);
        result.DefaultMode.Should().Be(PlaylistMode.LiveAssist);
        result.ApiPort.Should().Be((ushort)9300);
        result.CartwallSlotsPerPage.Should().Be((byte)16);
        result.BackupIntervalH.Should().Be((byte)24);
        result.EmergencyPlaylistId.Should().Be("emergency-playlist-1");
        result.AuxFadeoutMs.Should().Be(1500u);
    }

    [Fact]
    public async Task GetByStudio_ShouldReturn()
    {
        var studioId = await CreateTempStudioAsync();
        var settingsId = Guid.NewGuid().ToString();
        var settings   = BuildSettings(settingsId, studioId,
            loudnessTargetLufs: -23.0m,
            crossfadeEnabled: false,
            sweeperEnabled: false);
        await _sut.CreateAsync(settings);

        var result = await _sut.GetByStudioAsync(studioId);

        result.Should().NotBeNull();
        result!.LoudnessTargetLufs.Should().Be(-23.0m);
        result.CrossfadeEnabled.Should().BeFalse();
        result.SweeperEnabled.Should().BeFalse();
        result.DuckingLevelDb.Should().Be(-12.0m);
        result.LoudnessNormalization.Should().BeTrue();
    }

    [Fact]
    public async Task GetByStudio_ShouldReturnNull_WhenNotFound()
    {
        var result = await _sut.GetByStudioAsync(Guid.NewGuid().ToString());

        result.Should().BeNull();
    }

    [Fact]
    public async Task Update_ShouldModify()
    {
        var studioId = await CreateTempStudioAsync();
        var settingsId = Guid.NewGuid().ToString();
        await _sut.CreateAsync(BuildSettings(settingsId, studioId));

        var updated = BuildSettings(settingsId, studioId,
            theme: AppTheme.Light,
            crossfadeEnabled: false,
            loudnessTargetLufs: -16.0m,
            apiPort: 9301);
        await _sut.UpdateAsync(updated);

        var result = await _sut.GetByStudioAsync(studioId);
        result!.Theme.Should().Be(AppTheme.Light);
        result.CrossfadeEnabled.Should().BeFalse();
        result.LoudnessTargetLufs.Should().Be(-16.0m);
        result.ApiPort.Should().Be((ushort)9301);
    }

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

    private static AudioSettings BuildSettings(
        string   settingsId,
        string   studioId,
        AppTheme theme              = AppTheme.Dark,
        bool     crossfadeEnabled   = true,
        bool     sweeperEnabled     = true,
        decimal  loudnessTargetLufs = -23.0m,
        int      apiPort            = 9300) => new()
    {
        SettingsId               = settingsId,
        StudioId                 = studioId,
        SampleRate               = AudioSampleRate.Hz48000,
        BufferSize               = AudioBufferSize.Samples512,
        OutputMode               = DriverType.WasapiExclusive,
        CrossfadeEnabled         = crossfadeEnabled,
        CrossfadeDurationMs      = 2000,
        DuckingLevelDb           = -12.0m,
        DuckingAttackMs          = 200,
        DuckingReleaseMs         = 500,
        StopFadeoutMs            = 1250,
        AuxFadeoutMs             = 1500,
        SilenceRemoverEnabled    = false,
        SilenceStartThresholdDb  = -25.0m,
        SilenceMixThresholdDb    = -15.0m,
        SilenceEndThresholdDb    = -28.0m,
        LoudnessTargetLufs       = loudnessTargetLufs,
        LoudnessNormalization    = true,
        SweeperEnabled           = sweeperEnabled,
        SweeperMinIntroMs        = 5000,
        DefaultMode              = PlaylistMode.LiveAssist,
        CountdownRedEnabled      = true,
        CountdownRedThresholdS   = 30,
        CountdownGreenEnabled    = true,
        DeadAirEnabled           = true,
        DeadAirThresholdS        = 5,
        EmergencyPlaylistId      = "emergency-playlist-1",
        CartwallSlotsPerPage     = 16,
        CartwallFadeoutMs        = 1000,
        CartwallSeparateWindow   = false,
        BackupIntervalH          = 24,
        BackupKeepCount          = 7,
        BackupOnClose            = true,
        ApiEnabled               = true,
        ApiPort                  = (ushort)apiPort,
        ApiAuthEnabled           = true,
        ApiAnonymousLocal        = false,
        Theme                    = theme,
        UpdatedAt                = DateTime.UtcNow
    };
}
