using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using RDM.Core.Entities;
using RDM.Core.Models;
using RDM.Shared.DTOs;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.API.Tests;

public class EncoderControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private const string AdminUser = "testadmin";
    private const string AdminPass = "adminpassword";
    private const string OperatorUser = "testoperator";
    private const string OperatorPass = "oppassword";

    public EncoderControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;

        _factory.UserRepositoryMock
            .Setup(r => r.GetByUsernameAsync("test-studio-id", AdminUser, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User
            {
                UserId = "admin-user-id",
                StudioId = "test-studio-id",
                Username = AdminUser,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(AdminPass, workFactor: 4),
                Role = UserRole.Admin,
                Enabled = true
            });

        _factory.UserRepositoryMock
            .Setup(r => r.GetByUsernameAsync("test-studio-id", OperatorUser, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User
            {
                UserId = "op-user-id",
                StudioId = "test-studio-id",
                Username = OperatorUser,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(OperatorPass, workFactor: 4),
                Role = UserRole.Operator,
                Enabled = true
            });

        _factory.AudioEngineMock
            .Setup(e => e.IsEncoderFormatAvailable(It.IsAny<EncoderFormat>()))
            .Returns(true);
    }

    private HttpClient Admin()    => _factory.CreateClient().WithBasicAuth(AdminUser, AdminPass);
    private HttpClient Operator() => _factory.CreateClient().WithBasicAuth(OperatorUser, OperatorPass);

    private static EncoderProfileCreateDto ValidCreate(
        string format = "MP3", string serverType = "ICECAST", int bitrate = 128,
        string? mount = "/live", int port = 8000, string host = "stream.example.org",
        string name = "Main", byte channels = 2)
        => new(name, format, bitrate, serverType, host, port, mount, Password: "hunter2",
               Channels: channels);

    private static EncoderProfile StoredProfile(
        string id = "p1", bool enabled = true, byte[]? password = null,
        EncoderFormat format = EncoderFormat.Mp3,
        CastServerType serverType = CastServerType.Icecast)
        => new()
        {
            ProfileId         = id,
            StudioId          = "test-studio-id",
            Name              = "Main",
            Format            = format,
            BitrateKbps       = 128,
            Channels          = 2,
            ServerType        = serverType,
            Host              = "stream.example.org",
            Port              = 8000,
            Mount             = "/live",
            PasswordEncrypted = password,
            Enabled           = enabled,
            CreatedAt         = DateTime.Now
        };

    // ── Authorisation ────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_AsOperator_IsForbidden()
    {
        var response = await Operator().PostAsJsonAsync("/api/v1/encoder", ValidCreate());
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Delete_AsOperator_IsForbidden()
    {
        _factory.EncoderProfileRepositoryMock
            .Setup(r => r.GetByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(StoredProfile());

        var response = await Operator().DeleteAsync("/api/v1/encoder/p1");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // Starting a stream is an operational act, not an administrative one — a presenter must be
    // able to go on air without an Admin account.
    [Fact]
    public async Task Start_AsOperator_IsAllowed()
    {
        _factory.EncoderProfileRepositoryMock
            .Setup(r => r.GetByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(StoredProfile());
        _factory.AudioEngineMock
            .Setup(e => e.GetEncoderStatus("p1"))
            .Returns(new EncoderStatus("p1", "Main", EncoderSessionState.Connecting));

        var response = await Operator().PostAsync("/api/v1/encoder/p1/start", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Validation ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_IcecastWithoutMount_IsRejected()
    {
        var response = await Admin().PostAsJsonAsync(
            "/api/v1/encoder", ValidCreate(serverType: "ICECAST", mount: null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        error!.Message.Should().Contain("mount");
    }

    // SHOUTcast has no mount point, so its absence must not be an error there.
    [Fact]
    public async Task Create_ShoutcastWithoutMount_IsAccepted()
    {
        var response = await Admin().PostAsJsonAsync(
            "/api/v1/encoder", ValidCreate(serverType: "SHOUTCAST", mount: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(321)]
    public async Task Create_BitrateOutOfRange_IsRejected(int bitrate)
    {
        var response = await Admin().PostAsJsonAsync("/api/v1/encoder", ValidCreate(bitrate: bitrate));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public async Task Create_PortOutOfRange_IsRejected(int port)
    {
        var response = await Admin().PostAsJsonAsync("/api/v1/encoder", ValidCreate(port: port));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(1801)]
    public async Task Create_ReconnectDelayOutOfRange_IsRejected(int delay)
    {
        var dto = new EncoderProfileCreateDto(
            "Main", "MP3", 128, "ICECAST", "stream.example.org", 8000, "/live",
            Password: "hunter2", ReconnectDelaySeconds: delay);

        var response = await Admin().PostAsJsonAsync("/api/v1/encoder", dto);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        error!.Message.Should().Contain("Reconnect delay");
    }

    [Fact]
    public async Task Create_StoresTheReconnectDelay()
    {
        EncoderProfile? captured = null;
        _factory.EncoderProfileRepositoryMock
            .Setup(r => r.CreateAsync(It.IsAny<EncoderProfile>(), It.IsAny<CancellationToken>()))
            .Callback<EncoderProfile, CancellationToken>((p, _) => captured = p)
            .Returns(Task.CompletedTask);

        var dto = new EncoderProfileCreateDto(
            "Main", "MP3", 128, "ICECAST", "stream.example.org", 8000, "/live",
            Password: "hunter2", ReconnectDelaySeconds: 20);

        var response = await Admin().PostAsJsonAsync("/api/v1/encoder", dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured!.ReconnectDelaySeconds.Should().Be(20);
    }

    // A client that never sends the field gets the sensible default rather than a zero that the
    // engine would have to clamp anyway.
    [Fact]
    public async Task Create_DefaultsTheReconnectDelayToTen()
    {
        EncoderProfile? captured = null;
        _factory.EncoderProfileRepositoryMock
            .Setup(r => r.CreateAsync(It.IsAny<EncoderProfile>(), It.IsAny<CancellationToken>()))
            .Callback<EncoderProfile, CancellationToken>((p, _) => captured = p)
            .Returns(Task.CompletedTask);

        await Admin().PostAsJsonAsync("/api/v1/encoder", ValidCreate());

        captured!.ReconnectDelaySeconds.Should().Be(10);
    }

    // SHOUTcast 2 is a distinct wire format, not a label — it must survive validation and be
    // stored as its own server type rather than collapsing into SHOUTcast 1.
    [Fact]
    public async Task Create_ShoutcastV2_IsAcceptedAndStoredWithItsStreamId()
    {
        EncoderProfile? captured = null;
        _factory.EncoderProfileRepositoryMock
            .Setup(r => r.CreateAsync(It.IsAny<EncoderProfile>(), It.IsAny<CancellationToken>()))
            .Callback<EncoderProfile, CancellationToken>((p, _) => captured = p)
            .Returns(Task.CompletedTask);

        var dto = new EncoderProfileCreateDto(
            "V2", "MP3", 128, "SHOUTCAST2", "dnas.example.org", 8000,
            Mount: null, Username: "dj", StreamId: "2", Password: "hunter2");

        var response = await Admin().PostAsJsonAsync("/api/v1/encoder", dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured!.ServerType.Should().Be(CastServerType.ShoutcastV2);
        captured.StreamId.Should().Be("2");
        captured.Username.Should().Be("dj");
    }

    // A SHOUTcast 2 profile has no mount point, so its absence must not be an error there either.
    [Fact]
    public async Task Create_ShoutcastV2WithoutMount_IsAccepted()
    {
        var response = await Admin().PostAsJsonAsync(
            "/api/v1/encoder", ValidCreate(serverType: "SHOUTCAST2", mount: null));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetAll_ReportsShoutcastV2AsItsOwnServerType()
    {
        _factory.EncoderProfileRepositoryMock
            .Setup(r => r.GetByStudioAsync("test-studio-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EncoderProfile>
            {
                StoredProfile("v2", serverType: CastServerType.ShoutcastV2)
            });

        var response = await Admin().GetAsync("/api/v1/encoder");
        var body = await response.Content.ReadFromJsonAsync<EncoderProfileEnvelopeDto>();

        body!.Profiles.Should().ContainSingle()
             .Which.ServerType.Should().Be("SHOUTCAST2");
    }

    [Fact]
    public async Task Create_UnknownFormat_IsRejected()
    {
        var response = await Admin().PostAsJsonAsync("/api/v1/encoder", ValidCreate(format: "AAC"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        error!.Message.Should().Contain("Mp3");   // the message lists what is supported
    }

    // A missing add-on DLL is a configuration fault, not a bad request and not a server error.
    [Fact]
    public async Task Create_FormatWithoutItsDll_Is422()
    {
        _factory.AudioEngineMock
            .Setup(e => e.IsEncoderFormatAvailable(EncoderFormat.Opus))
            .Returns(false);

        var response = await Admin().PostAsJsonAsync("/api/v1/encoder", ValidCreate(format: "OPUS"));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        error!.ErrorCode.Should().Be("FORMAT_UNAVAILABLE");

        _factory.AudioEngineMock
            .Setup(e => e.IsEncoderFormatAvailable(It.IsAny<EncoderFormat>()))
            .Returns(true);
    }

    [Fact]
    public async Task Create_EmptyName_IsRejected()
    {
        var response = await Admin().PostAsJsonAsync("/api/v1/encoder", ValidCreate(name: "  "));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_ThreeChannels_IsRejected()
    {
        var response = await Admin().PostAsJsonAsync("/api/v1/encoder", ValidCreate(channels: 3));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Passwords never travel back out ──────────────────────────────────────

    [Fact]
    public async Task GetAll_NeverReturnsThePassword()
    {
        _factory.EncoderProfileRepositoryMock
            .Setup(r => r.GetByStudioAsync("test-studio-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EncoderProfile>
            {
                StoredProfile(password: System.Text.Encoding.UTF8.GetBytes("hunter2"))
            });

        var response = await Admin().GetAsync("/api/v1/encoder");
        var raw = await response.Content.ReadAsStringAsync();

        raw.Should().NotContain("hunter2");
        raw.Should().NotContain("assword\":\"");   // no password field of any kind

        var body = await response.Content.ReadFromJsonAsync<EncoderProfileEnvelopeDto>();
        body!.Profiles.Should().ContainSingle().Which.HasPassword.Should().BeTrue();
    }

    [Fact]
    public async Task Create_EncryptsThePasswordBeforeStoring()
    {
        EncoderProfile? captured = null;
        _factory.EncoderProfileRepositoryMock
            .Setup(r => r.CreateAsync(It.IsAny<EncoderProfile>(), It.IsAny<CancellationToken>()))
            .Callback<EncoderProfile, CancellationToken>((p, _) => captured = p)
            .Returns(Task.CompletedTask);

        await Admin().PostAsJsonAsync("/api/v1/encoder", ValidCreate());

        captured.Should().NotBeNull();
        captured!.PasswordEncrypted.Should().NotBeNull();
        _factory.SecretProtectorMock.Verify(s => s.Protect("hunter2"), Times.AtLeastOnce);
    }

    // Omitting the password on update must not wipe a working credential.
    [Fact]
    public async Task Update_WithNullPassword_KeepsTheStoredOne()
    {
        var stored = StoredProfile(password: System.Text.Encoding.UTF8.GetBytes("original"));
        _factory.EncoderProfileRepositoryMock
            .Setup(r => r.GetByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(stored);

        EncoderProfile? captured = null;
        _factory.EncoderProfileRepositoryMock
            .Setup(r => r.UpdateAsync(It.IsAny<EncoderProfile>(), It.IsAny<CancellationToken>()))
            .Callback<EncoderProfile, CancellationToken>((p, _) => captured = p)
            .Returns(Task.CompletedTask);

        var dto = new EncoderProfileUpdateDto(
            "Renamed", "MP3", 128, "ICECAST", "stream.example.org", 8000, "/live", Password: null);

        var response = await Admin().PutAsJsonAsync("/api/v1/encoder/p1", dto);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        captured!.PasswordEncrypted.Should().BeEquivalentTo(stored.PasswordEncrypted);
        captured.Name.Should().Be("Renamed");
    }

    // ── Sessions ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_PassesTheDecryptedPasswordToTheEngine()
    {
        _factory.EncoderProfileRepositoryMock
            .Setup(r => r.GetByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(StoredProfile(password: System.Text.Encoding.UTF8.GetBytes("hunter2")));
        _factory.AudioEngineMock
            .Setup(e => e.GetEncoderStatus("p1"))
            .Returns(new EncoderStatus("p1", "Main", EncoderSessionState.Connecting));

        await Admin().PostAsync("/api/v1/encoder/p1/start", null);

        _factory.AudioEngineMock.Verify(e => e.StartEncoderAsync(
            It.Is<EncoderProfile>(p => p.ProfileId == "p1"),
            "hunter2",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Start_DisabledProfile_Is422()
    {
        _factory.EncoderProfileRepositoryMock
            .Setup(r => r.GetByIdAsync("off", It.IsAny<CancellationToken>()))
            .ReturnsAsync(StoredProfile("off", enabled: false));

        var response = await Admin().PostAsync("/api/v1/encoder/off/start", null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        error!.ErrorCode.Should().Be("PROFILE_DISABLED");
    }

    [Fact]
    public async Task Start_UnknownProfile_Is404()
    {
        _factory.EncoderProfileRepositoryMock
            .Setup(r => r.GetByIdAsync("nope", It.IsAny<CancellationToken>()))
            .ReturnsAsync((EncoderProfile?)null);

        var response = await Admin().PostAsync("/api/v1/encoder/nope/start", null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // A blob from another machine cannot be decrypted here; that must read as a fixable
    // configuration fault, not a 500.
    [Fact]
    public async Task Start_WithUndecryptablePassword_Is422()
    {
        _factory.EncoderProfileRepositoryMock
            .Setup(r => r.GetByIdAsync("bad", It.IsAny<CancellationToken>()))
            .ReturnsAsync(StoredProfile("bad", password: [1, 2, 3]));
        // Null-safe matcher: profiles without a password call Unprotect(null), and a predicate
        // that dereferenced it would throw inside Moq — which the controller would then report as
        // an undecryptable password, breaking unrelated tests.
        _factory.SecretProtectorMock
            .Setup(s => s.Unprotect(It.Is<byte[]?>(b => b != null && b.Length == 3)))
            .Throws(new System.Security.Cryptography.CryptographicException("wrong machine"));

        var response = await Admin().PostAsync("/api/v1/encoder/bad/start", null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        error!.ErrorCode.Should().Be("PASSWORD_UNREADABLE");
    }

    // Deleting a streaming profile must take it off air first.
    [Fact]
    public async Task Delete_StopsTheSessionBeforeRemovingTheProfile()
    {
        _factory.EncoderProfileRepositoryMock
            .Setup(r => r.GetByIdAsync("p1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(StoredProfile());

        var response = await Admin().DeleteAsync("/api/v1/encoder/p1");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.AudioEngineMock.Verify(
            e => e.StopEncoderAsync("p1", It.IsAny<CancellationToken>()), Times.Once);
        _factory.EncoderProfileRepositoryMock.Verify(
            r => r.DeleteAsync("p1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Stop_OnAProfileThatNeverRan_StillReportsStopped()
    {
        _factory.AudioEngineMock.Setup(e => e.GetEncoderStatus("ghost")).Returns((EncoderStatus?)null);

        var response = await Admin().PostAsync("/api/v1/encoder/ghost/stop", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<EncoderStatusDto>();
        body!.State.Should().Be("STOPPED");
    }

    // ── The bottom-bar toggle ────────────────────────────────────────────────

    [Fact]
    public async Task StartArmed_StartsOnlyTheProfilesFlaggedReady()
    {
        // The repository query already filters on auto_start; the controller must use that one
        // rather than fetching everything and deciding for itself.
        _factory.EncoderProfileRepositoryMock
            .Setup(r => r.GetArmedAsync("test-studio-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EncoderProfile> { StoredProfile("a"), StoredProfile("b") });
        _factory.AudioEngineMock.Setup(e => e.GetAllEncoderStatuses())
            .Returns(new List<EncoderStatus> { new("a", "A", EncoderSessionState.Connecting) });

        var response = await Operator().PostAsync("/api/v1/encoder/start-armed", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // Matched per profile id, not It.IsAny: the engine mock lives on the class fixture and
        // accumulates every call made by every test in this class.
        _factory.AudioEngineMock.Verify(e => e.StartEncoderAsync(
            It.Is<EncoderProfile>(p => p.ProfileId == "a"),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        _factory.AudioEngineMock.Verify(e => e.StartEncoderAsync(
            It.Is<EncoderProfile>(p => p.ProfileId == "b"),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // One unstartable profile must not abort the ones queued behind it.
    [Fact]
    public async Task StartArmed_KeepsGoingWhenOneProfileFails()
    {
        _factory.EncoderProfileRepositoryMock
            .Setup(r => r.GetArmedAsync("test-studio-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EncoderProfile> { StoredProfile("bad"), StoredProfile("good") });
        _factory.AudioEngineMock
            .Setup(e => e.StartEncoderAsync(
                It.Is<EncoderProfile>(p => p.ProfileId == "bad"),
                It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no mixer"));
        _factory.AudioEngineMock.Setup(e => e.GetAllEncoderStatuses())
            .Returns(new List<EncoderStatus>());

        var response = await Operator().PostAsync("/api/v1/encoder/start-armed", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.AudioEngineMock.Verify(e => e.StartEncoderAsync(
            It.Is<EncoderProfile>(p => p.ProfileId == "good"),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartArmed_SkipsAProfileWhoseFormatHasNoAddOn()
    {
        var opus = StoredProfile("opus", format: EncoderFormat.Opus);

        _factory.EncoderProfileRepositoryMock
            .Setup(r => r.GetArmedAsync("test-studio-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EncoderProfile> { opus });
        _factory.AudioEngineMock
            .Setup(e => e.IsEncoderFormatAvailable(EncoderFormat.Opus)).Returns(false);
        _factory.AudioEngineMock.Setup(e => e.GetAllEncoderStatuses())
            .Returns(new List<EncoderStatus>());

        await Operator().PostAsync("/api/v1/encoder/start-armed", null);

        _factory.AudioEngineMock.Verify(e => e.StartEncoderAsync(
            It.Is<EncoderProfile>(p => p.ProfileId == "opus"),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        _factory.AudioEngineMock
            .Setup(e => e.IsEncoderFormatAvailable(It.IsAny<EncoderFormat>())).Returns(true);
    }

    [Fact]
    public async Task StopAll_StopsEverySession()
    {
        _factory.AudioEngineMock.Setup(e => e.GetAllEncoderStatuses())
            .Returns(new List<EncoderStatus>
            {
                new("a", "A", EncoderSessionState.Streaming),
                new("b", "B", EncoderSessionState.Streaming)
            });

        var response = await Operator().PostAsync("/api/v1/encoder/stop-all", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.AudioEngineMock.Verify(
            e => e.StopEncoderAsync("a", It.IsAny<CancellationToken>()), Times.Once);
        _factory.AudioEngineMock.Verify(
            e => e.StopEncoderAsync("b", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAllStatuses_ReturnsEverySession()
    {
        _factory.AudioEngineMock
            .Setup(e => e.GetAllEncoderStatuses())
            .Returns(new List<EncoderStatus>
            {
                new("p1", "Main",   EncoderSessionState.Streaming),
                new("p2", "Backup", EncoderSessionState.RetryWaiting, "gone", null, 2)
            });

        var response = await Admin().GetAsync("/api/v1/encoder/status");
        var body = await response.Content.ReadFromJsonAsync<EncoderStatusEnvelopeDto>();

        body!.Statuses.Should().HaveCount(2);
        body.Statuses[0].State.Should().Be("STREAMING");
        body.Statuses[1].RetryAttempt.Should().Be(2);
    }
}
