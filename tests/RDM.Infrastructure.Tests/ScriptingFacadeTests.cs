using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RDM.Core.Events;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;
using RDM.Infrastructure.Hardware;
using RDM.Infrastructure.Scripting;
using Xunit;

namespace RDM.Infrastructure.Tests;

/// <summary>
/// Unit tests for <see cref="ScriptingFacade"/> — the only API surface exposed to user scripts.
/// Uses the real <see cref="ActionRegistry"/> with spy delegates and Moq for the audio engine /
/// event bus. No database.
/// </summary>
public sealed class ScriptingFacadeTests
{
    private readonly ActionRegistry     _registry = new();
    private readonly Mock<IEventBus>    _bus      = new();
    private readonly Mock<IAudioEngine> _audio    = new();

    private readonly List<ActionId>  _invoked = new();
    private IHardwarePayload? _capturedPayload;

    private ScriptingFacade Create() =>
        new(_registry, _bus.Object, _audio.Object, Mock.Of<ILogger<ScriptingFacade>>());

    private void SpyOn(params ActionId[] ids)
    {
        foreach (var id in ids)
        {
            var captured = id;
            _registry.RegisterAction(captured, p =>
            {
                _invoked.Add(captured);
                _capturedPayload = p;
                return Task.CompletedTask;
            });
        }
    }

    // ── Player / Mic ─────────────────────────────────────────────────────────────

    [Fact]
    public void PlayerMethods_InvokeMatchingActions()
    {
        SpyOn(ActionId.PlayerPlay, ActionId.PlayerStop, ActionId.PlayerNext, ActionId.PlayerPause);
        var f = Create();

        f.PlayerPlay();
        f.PlayerStop();
        f.PlayerNext();
        f.PlayerPause();

        _invoked.Should().Equal(
            ActionId.PlayerPlay, ActionId.PlayerStop, ActionId.PlayerNext, ActionId.PlayerPause);
    }

    [Fact]
    public void MicMethods_InvokeMatchingActions()
    {
        SpyOn(ActionId.MicOn, ActionId.MicOff, ActionId.MicToggle);
        var f = Create();

        f.MicOn();
        f.MicOff();
        f.MicToggle();

        _invoked.Should().Equal(ActionId.MicOn, ActionId.MicOff, ActionId.MicToggle);
    }

    // ── Cartwall ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, ActionId.CartwallTab1)]
    [InlineData(6, ActionId.CartwallTab7)]
    [InlineData(99, ActionId.CartwallTab1)] // out of range → default Tab1
    public void CartSelectTab_MapsIndexToAction(int index, ActionId expected)
    {
        SpyOn(expected);
        var f = Create();

        f.CartSelectTab(index);

        _invoked.Should().Equal(expected);
    }

    [Theory]
    [InlineData(0, ActionId.CartwallTriggerSlot1)]
    [InlineData(15, ActionId.CartwallTriggerSlot16)]
    [InlineData(99, ActionId.CartwallTriggerSlot1)] // out of range → default Slot1
    public void CartTriggerSlot_MapsIndexToAction(int index, ActionId expected)
    {
        SpyOn(expected);
        var f = Create();

        f.CartTriggerSlot(index);

        _invoked.Should().Equal(expected);
    }

    // ── Integracje zewnętrzne ────────────────────────────────────────────────────

    [Fact]
    public void SendHttp_InvokesActionWithUrlParameter()
    {
        SpyOn(ActionId.AutomationSendHttp);
        var f = Create();

        f.SendHttp("http://example.test/hook");

        _invoked.Should().Equal(ActionId.AutomationSendHttp);
        _capturedPayload.Should().BeOfType<ParameterizedPayload>()
                        .Which.Parameter.Should().Be("http://example.test/hook");
    }

    [Fact]
    public void SendSerial_PublishesSerialHardwareCommand()
    {
        HardwareOutputCommand? published = null;
        _bus.Setup(b => b.PublishAsync(It.IsAny<HardwareOutputCommand>(), It.IsAny<CancellationToken>()))
            .Callback<HardwareOutputCommand, CancellationToken>((c, _) => published = c)
            .Returns(Task.CompletedTask);
        var f = Create();

        f.SendSerial("dev1", "GPO01 ON");

        published.Should().NotBeNull();
        published!.DeviceType.Should().Be("SERIAL");
        published.TargetDeviceId.Should().Be("dev1");
        published.Payload.Should().BeOfType<SerialCommandPayload>()
                 .Which.Command.Should().Be("GPO01 ON");
    }

    // ── AUX ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void AuxMethods_DelegateToAudioEngine()
    {
        var f = Create();

        f.AuxLoad(2, "file.wav");
        f.AuxPlay(1);
        f.AuxStop(3);
        f.AuxSetLoop(0, true);
        f.AuxSetVolume(1, 0.5f);

        _audio.Verify(a => a.LoadAuxAsync(2, "file.wav", It.IsAny<CancellationToken>()), Times.Once);
        _audio.Verify(a => a.PlayAuxAsync(1, It.IsAny<CancellationToken>()), Times.Once);
        _audio.Verify(a => a.StopAuxAsync(3, 0, It.IsAny<CancellationToken>()), Times.Once);
        _audio.Verify(a => a.SetAuxLoopAsync(0, true, It.IsAny<CancellationToken>()), Times.Once);
        _audio.Verify(a => a.SetAuxVolumeAsync(1, 0.5f, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void AuxStop_WithFadeMs_ForwardsFadeoutToAudioEngine()
    {
        var f = Create();

        f.AuxStop(2, 1500);

        _audio.Verify(a => a.StopAuxAsync(2, 1500u, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void AuxPlay_WhenEngineThrows_DoesNotPropagate()
    {
        _audio.Setup(a => a.PlayAuxAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("device gone"));
        var f = Create();

        var act = () => f.AuxPlay(1);

        act.Should().NotThrow(); // failures are logged, not surfaced to the script
    }

    // ── Odporność / narzędzia ────────────────────────────────────────────────────

    [Fact]
    public void Invoke_MissingActionDelegate_DoesNotThrow()
    {
        var f = Create(); // nothing registered

        var act = () => f.PlayerPlay();

        act.Should().NotThrow();
        _bus.Verify(b => b.PublishAsync(It.IsAny<HardwareOutputCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Delay_NegativeValue_IsClampedAndReturnsPromptly()
    {
        var f = Create();

        var act = () => f.Delay(-100); // clamped to 0

        act.Should().NotThrow();
    }

    [Fact]
    public void Log_DoesNotThrow()
    {
        var f = Create();

        var act = () => f.Log("hello from script");

        act.Should().NotThrow();
    }
}
