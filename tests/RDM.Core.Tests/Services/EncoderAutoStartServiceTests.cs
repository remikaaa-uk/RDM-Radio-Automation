using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RDM.Core.Entities;
using RDM.Core.Interfaces;
using RDM.Core.Services;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.Core.Tests.Services;

public sealed class EncoderAutoStartServiceTests
{
    private const string StudioId = "studio-1";

    private readonly Mock<IEncoderProfileRepository> _profiles = new();
    private readonly Mock<IAudioEngine>              _engine   = new();
    private readonly Mock<ISecretProtector>          _secrets  = new();
    private readonly StudioContext                   _studio   = new();

    public EncoderAutoStartServiceTests()
    {
        _studio.Initialize(StudioId);
        _engine.SetupGet(e => e.IsInitialized).Returns(true);
        _engine.Setup(e => e.IsEncoderFormatAvailable(It.IsAny<EncoderFormat>())).Returns(true);
        _secrets.Setup(s => s.Unprotect(It.IsAny<byte[]?>())).Returns("secret");
        _profiles.Setup(p => p.GetAutoStartAsync(StudioId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(Array.Empty<EncoderProfile>());
    }

    private EncoderAutoStartService Service() => new(
        _profiles.Object, _engine.Object, _secrets.Object, _studio,
        NullLogger<EncoderAutoStartService>.Instance);

    private static EncoderProfile Profile(
        string id = "p1",
        string name = "Main",
        EncoderFormat format = EncoderFormat.Mp3,
        byte[]? password = null)
        => new()
        {
            ProfileId         = id,
            StudioId          = StudioId,
            Name              = name,
            Format            = format,
            BitrateKbps       = 128,
            Channels          = 2,
            ServerType        = CastServerType.Icecast,
            Host              = "stream.example.org",
            Port              = 8000,
            Mount             = "/live",
            PasswordEncrypted = password,
            Enabled           = true,
            AutoStart         = true,
            CreatedAt         = DateTime.Now
        };

    private void GivenProfiles(params EncoderProfile[] profiles)
        => _profiles.Setup(p => p.GetAutoStartAsync(StudioId, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(profiles);

    // ── The happy path ───────────────────────────────────────────────────────

    [Fact]
    public async Task Starts_every_auto_start_profile()
    {
        GivenProfiles(Profile("p1", "Main"), Profile("p2", "Backup"));

        var started = await Service().StartAllAsync();

        started.Should().Be(2);
        _engine.Verify(e => e.StartEncoderAsync(
            It.IsAny<EncoderProfile>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task Passes_the_decrypted_password_to_the_engine()
    {
        GivenProfiles(Profile(password: [1, 2, 3]));
        _secrets.Setup(s => s.Unprotect(It.IsAny<byte[]?>())).Returns("hunter2");

        await Service().StartAllAsync();

        _engine.Verify(e => e.StartEncoderAsync(
            It.IsAny<EncoderProfile>(), "hunter2", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Does_nothing_when_there_are_no_auto_start_profiles()
    {
        var started = await Service().StartAllAsync();

        started.Should().Be(0);
        _engine.Verify(e => e.StartEncoderAsync(
            It.IsAny<EncoderProfile>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Nothing on this path may throw: it runs during startup ───────────────

    [Fact]
    public async Task Skips_everything_when_the_engine_is_not_running()
    {
        _engine.SetupGet(e => e.IsInitialized).Returns(false);
        GivenProfiles(Profile());

        var started = await Service().StartAllAsync();

        started.Should().Be(0);
        // The repository is not even consulted — no-audio mode has nothing to stream.
        _profiles.Verify(p => p.GetAutoStartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Survives_a_repository_that_throws()
    {
        _profiles.Setup(p => p.GetAutoStartAsync(StudioId, It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new InvalidOperationException("database is down"));

        var act = async () => await Service().StartAllAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Survives_an_engine_that_throws_and_keeps_going()
    {
        GivenProfiles(Profile("bad", "Broken"), Profile("good", "Working"));
        _engine.Setup(e => e.StartEncoderAsync(
                   It.Is<EncoderProfile>(p => p.ProfileId == "bad"),
                   It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("no mixer"));

        var started = await Service().StartAllAsync();

        // One bad profile costs itself, not the one after it.
        started.Should().Be(1);
        _engine.Verify(e => e.StartEncoderAsync(
            It.Is<EncoderProfile>(p => p.ProfileId == "good"),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Skips_a_profile_whose_format_has_no_add_on()
    {
        GivenProfiles(Profile("ogg", "No DLL", EncoderFormat.Ogg), Profile("mp3", "Fine"));
        _engine.Setup(e => e.IsEncoderFormatAvailable(EncoderFormat.Ogg)).Returns(false);

        var started = await Service().StartAllAsync();

        started.Should().Be(1);
        _engine.Verify(e => e.StartEncoderAsync(
            It.Is<EncoderProfile>(p => p.ProfileId == "ogg"),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // A DPAPI blob from another machine decrypts to nothing; starting anyway would only produce
    // a login the server rejects, with no hint as to why.
    [Fact]
    public async Task Skips_a_profile_whose_password_cannot_be_decrypted()
    {
        GivenProfiles(Profile("foreign", "From a backup", password: [9, 9, 9]));
        _secrets.Setup(s => s.Unprotect(It.IsAny<byte[]?>()))
                .Throws(new System.Security.Cryptography.CryptographicException("wrong machine"));

        var started = await Service().StartAllAsync();

        started.Should().Be(0);
        _engine.Verify(e => e.StartEncoderAsync(
            It.IsAny<EncoderProfile>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // A profile with no stored password is legitimate — some servers take a blank source password.
    [Fact]
    public async Task Starts_a_profile_that_has_no_stored_password()
    {
        GivenProfiles(Profile(password: null));
        _secrets.Setup(s => s.Unprotect(null)).Returns((string?)null);

        var started = await Service().StartAllAsync();

        started.Should().Be(1);
        _engine.Verify(e => e.StartEncoderAsync(
            It.IsAny<EncoderProfile>(), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Stops_when_cancellation_is_requested()
    {
        GivenProfiles(Profile("p1"), Profile("p2"), Profile("p3"));
        using var cts = new CancellationTokenSource();

        _engine.Setup(e => e.StartEncoderAsync(
                   It.IsAny<EncoderProfile>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .Callback(() => cts.Cancel())
               .Returns(Task.CompletedTask);

        var started = await Service().StartAllAsync(cts.Token);

        started.Should().Be(1);
    }
}
