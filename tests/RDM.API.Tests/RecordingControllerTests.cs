using System;
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

public class RecordingControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private const string OperatorUser = "testoperator";
    private const string OperatorPass = "oppassword";

    public RecordingControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;

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

    private HttpClient Client() => _factory.CreateClient().WithBasicAuth(OperatorUser, OperatorPass);

    private static RecordingStartRequestDto ValidStart(
        string directory = @"D:\nagrania", string format = "MP3", int bitrate = 192, byte channels = 2)
        => new(directory, format, bitrate, Channels: channels);

    // Recording the station's own output needs no Admin role.
    [Fact]
    public async Task Start_AsOperator_IsAllowed()
    {
        _factory.AudioEngineMock
            .Setup(e => e.StartRecordingAsync(It.IsAny<RecordingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecordingStatus(RecordingState.Recording, @"D:\nagrania\rec.mp3", DateTime.Now));

        var response = await Client().PostAsJsonAsync("/api/v1/recording/start", ValidStart());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<RecordingStatusDto>();
        body!.State.Should().Be("RECORDING");
        body.FilePath.Should().Be(@"D:\nagrania\rec.mp3");
    }

    [Fact]
    public async Task Start_PassesTheRequestThroughToTheEngine()
    {
        RecordingRequest? captured = null;
        _factory.AudioEngineMock
            .Setup(e => e.StartRecordingAsync(It.IsAny<RecordingRequest>(), It.IsAny<CancellationToken>()))
            .Callback<RecordingRequest, CancellationToken>((r, _) => captured = r)
            .ReturnsAsync(new RecordingStatus(RecordingState.Recording, "x", DateTime.Now));

        await Client().PostAsJsonAsync("/api/v1/recording/start",
            new RecordingStartRequestDto(@"E:\audycje", "OPUS", 96, 48000, 2, "poranek"));

        captured.Should().NotBeNull();
        captured!.Directory.Should().Be(@"E:\audycje");
        captured.Format.Should().Be(EncoderFormat.Opus);
        captured.BitrateKbps.Should().Be(96);
        captured.SampleRateHz.Should().Be(48000);
        captured.NamePrefix.Should().Be("poranek");
    }

    [Fact]
    public async Task Start_WithoutDirectory_IsRejected()
    {
        var response = await Client().PostAsJsonAsync("/api/v1/recording/start", ValidStart(directory: "  "));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Start_UnknownFormat_IsRejected()
    {
        var response = await Client().PostAsJsonAsync("/api/v1/recording/start", ValidStart(format: "WMA"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(400)]
    public async Task Start_BitrateOutOfRange_IsRejected(int bitrate)
    {
        var response = await Client().PostAsJsonAsync("/api/v1/recording/start", ValidStart(bitrate: bitrate));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Start_FormatWithoutItsDll_Is422()
    {
        _factory.AudioEngineMock
            .Setup(e => e.IsEncoderFormatAvailable(EncoderFormat.Ogg))
            .Returns(false);

        var response = await Client().PostAsJsonAsync("/api/v1/recording/start", ValidStart(format: "OGG"));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        error!.ErrorCode.Should().Be("FORMAT_UNAVAILABLE");

        _factory.AudioEngineMock
            .Setup(e => e.IsEncoderFormatAvailable(It.IsAny<EncoderFormat>()))
            .Returns(true);
    }

    // An unwritable folder is the operator's problem to fix — it must surface as a 422 carrying
    // the engine's own message, never as a 500.
    [Fact]
    public async Task Start_WhenTheEngineRefuses_Is422WithItsMessage()
    {
        _factory.AudioEngineMock
            .Setup(e => e.StartRecordingAsync(It.IsAny<RecordingRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecordingStatus(
                RecordingState.Error, null, null, "Recording folder is not usable: access denied"));

        var response = await Client().PostAsJsonAsync("/api/v1/recording/start", ValidStart());

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        error!.ErrorCode.Should().Be("RECORDING_FAILED");
        error.Message.Should().Contain("access denied");
    }

    [Fact]
    public async Task Stop_ReturnsTheClosingStatusWithItsPath()
    {
        _factory.AudioEngineMock
            .Setup(e => e.StopRecordingAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RecordingStatus(RecordingState.Stopped, @"D:\nagrania\rec.mp3"));

        var response = await Client().PostAsync("/api/v1/recording/stop", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<RecordingStatusDto>();
        body!.State.Should().Be("STOPPED");
        body.FilePath.Should().Be(@"D:\nagrania\rec.mp3");
    }

    [Fact]
    public async Task Status_WhenIdle_ReportsStopped()
    {
        _factory.AudioEngineMock
            .Setup(e => e.GetRecordingStatus())
            .Returns(new RecordingStatus(RecordingState.Stopped));

        var response = await Client().GetAsync("/api/v1/recording/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<RecordingStatusDto>();
        body!.State.Should().Be("STOPPED");
        body.FilePath.Should().BeNull();
    }
}
