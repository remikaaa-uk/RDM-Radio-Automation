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
using RDM.Core.Models;
using RDM.Shared.DTOs;
using RDM.Shared.Enums;
using Xunit;

namespace RDM.API.Tests;

public class MicFxControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private const string OperatorUser = "micoperator";
    private const string OperatorPass = "micpassword";

    public MicFxControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;

        _factory.UserRepositoryMock
            .Setup(r => r.GetByUsernameAsync("test-studio-id", OperatorUser, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new User
            {
                UserId       = "mic-user-id",
                StudioId     = "test-studio-id",
                Username     = OperatorUser,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(OperatorPass, workFactor: 4),
                Role         = UserRole.Operator,
                Enabled      = true
            });
    }

    private HttpClient Client() => _factory.CreateClient().WithBasicAuth(OperatorUser, OperatorPass);

    [Fact]
    public async Task GetFx_returns_slots_with_their_parameters()
    {
        var slot = new MicFxSlot(1, MicFxType.Compressor);
        slot.Parameters["threshold"] = -18f;
        _factory.AudioEngineMock.Setup(e => e.GetMicFxList()).Returns(new[] { slot });

        var dto = await Client().GetFromJsonAsync<MicFxDto[]>("/api/v1/mic/fx");

        dto.Should().NotBeNull();
        dto![0].FxType.Should().Be("Compressor");
        dto[0].Parameters["threshold"].Should().Be(-18f);
        // The whole set travels, not only what was changed — the editor needs every value.
        dto[0].Parameters.Should().ContainKeys("ratio", "attack", "release", "gain");
    }

    [Fact]
    public async Task GetFxParams_exposes_ranges_for_a_known_type()
    {
        var dto = await Client().GetFromJsonAsync<MicFxParamDto[]>("/api/v1/mic/fx/params/PeakEq");

        dto.Should().NotBeNull();
        dto!.Select(p => p.Key).Should().BeEquivalentTo("center", "bandwidth", "gain");

        var center = dto!.First(p => p.Key == "center");
        center.Min.Should().Be(20f);
        center.Max.Should().Be(20000f);
        center.Unit.Should().Be("Hz");
    }

    [Fact]
    public async Task GetFxParams_rejects_an_unknown_type()
    {
        var resp = await Client().GetAsync("/api/v1/mic/fx/params/NoSuchEffect");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateFx_passes_the_parameters_to_the_engine()
    {
        Dictionary<string, float>? captured = null;
        _factory.AudioEngineMock
            .Setup(e => e.UpdateMicFxAsync(7, It.IsAny<IReadOnlyDictionary<string, float>>(), It.IsAny<CancellationToken>()))
            .Callback<int, IReadOnlyDictionary<string, float>, CancellationToken>(
                (_, p, _) => captured = new Dictionary<string, float>(p))
            .Returns(Task.CompletedTask);

        var body = new UpdateMicFxRequestDto(new Dictionary<string, float> { ["threshold"] = -24f, ["ratio"] = 8f });
        var resp = await Client().PutAsJsonAsync("/api/v1/mic/fx/7", body);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        captured.Should().NotBeNull();
        captured!["threshold"].Should().Be(-24f);
        captured["ratio"].Should().Be(8f);
    }

    [Fact]
    public async Task UpdateFx_returns_404_for_a_slot_that_does_not_exist()
    {
        _factory.AudioEngineMock
            .Setup(e => e.UpdateMicFxAsync(999, It.IsAny<IReadOnlyDictionary<string, float>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("No mic FX slot with id 999."));

        var body = new UpdateMicFxRequestDto(new Dictionary<string, float> { ["gain"] = 1f });
        var resp = await Client().PutAsJsonAsync("/api/v1/mic/fx/999", body);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
