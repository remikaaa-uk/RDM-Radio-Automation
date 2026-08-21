using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using RDM.Core.Entities;
using RDM.Core.Events;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;
using RDM.Infrastructure.Hardware;
using Xunit;

namespace RDM.Infrastructure.Tests;

/// <summary>
/// Unit tests for <see cref="MacroEngine"/> — no database. Uses the real <see cref="ActionRegistry"/>
/// (the engine's only public surface is the constructor, which registers the trigger delegate) and
/// Moq for the repository / event bus.
/// </summary>
public sealed class MacroEngineTests
{
    private readonly ActionRegistry         _registry = new();
    private readonly Mock<IMacroRepository> _repo     = new();
    private readonly Mock<IEventBus>        _bus      = new();

    private MacroEngine CreateEngine() =>
        new(_registry, _repo.Object, _bus.Object, Mock.Of<ILogger<MacroEngine>>());

    // Original payload handed to the trigger; also carries the macro GUID for lookup.
    private static ParameterizedPayload TriggerPayload(Guid macroId) =>
        new(new KeyboardPayload("Macro", false, false, false), macroId.ToString());

    private Task InvokeTrigger(IHardwarePayload payload) =>
        _registry.GetActionDelegate(ActionId.AutomationTriggerMacro)!(payload);

    private static Macro MacroWith(Guid id, params MacroStep[] steps) =>
        new() { Id = id, Name = "Test", IsEnabled = true, Steps = steps };

    // ── Rejestracja ────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_RegistersTriggerMacroAction()
    {
        CreateEngine();

        _registry.GetActionDelegate(ActionId.AutomationTriggerMacro).Should().NotBeNull();
    }

    // ── Ścieżka A: akcje ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Trigger_ExecutesActionSteps_InStepOrder()
    {
        var order = new List<string>();
        _registry.RegisterAction(ActionId.PlayerPlay, _ => { order.Add("play"); return Task.CompletedTask; });
        _registry.RegisterAction(ActionId.MicOn,      _ => { order.Add("mic");  return Task.CompletedTask; });

        var id = Guid.NewGuid();
        // Deliberately declared out of order — engine must sort by StepOrder.
        _repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(MacroWith(id,
            new MacroStep { StepOrder = 1, ActionId = ActionId.MicOn },
            new MacroStep { StepOrder = 0, ActionId = ActionId.PlayerPlay }));

        CreateEngine();
        await InvokeTrigger(TriggerPayload(id));

        order.Should().Equal("play", "mic");
    }

    [Fact]
    public async Task Trigger_ActionStep_WithParameter_WrapsPayload()
    {
        IHardwarePayload? captured = null;
        _registry.RegisterAction(ActionId.PlayerPlay, p => { captured = p; return Task.CompletedTask; });

        var id = Guid.NewGuid();
        _repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(MacroWith(id,
            new MacroStep { StepOrder = 0, ActionId = ActionId.PlayerPlay, Parameter = "42" }));

        CreateEngine();
        var original = TriggerPayload(id);
        await InvokeTrigger(original);

        captured.Should().BeOfType<ParameterizedPayload>();
        var wrapped = (ParameterizedPayload)captured!;
        wrapped.Parameter.Should().Be("42");
        wrapped.Inner.Should().BeSameAs(original); // wraps the original payload
    }

    [Fact]
    public async Task Trigger_ActionStep_WithoutParameter_PassesOriginalPayload()
    {
        IHardwarePayload? captured = null;
        _registry.RegisterAction(ActionId.PlayerPlay, p => { captured = p; return Task.CompletedTask; });

        var id = Guid.NewGuid();
        _repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(MacroWith(id,
            new MacroStep { StepOrder = 0, ActionId = ActionId.PlayerPlay }));

        CreateEngine();
        var original = TriggerPayload(id);
        await InvokeTrigger(original);

        captured.Should().BeSameAs(original); // no wrapping when Parameter is empty
    }

    [Fact]
    public async Task Trigger_MissingActionDelegate_SkipsStep_ContinuesOthers()
    {
        // MicOn is intentionally NOT registered → its step is skipped.
        var id = Guid.NewGuid();
        _repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(MacroWith(id,
            new MacroStep { StepOrder = 0, ActionId = ActionId.MicOn },
            new MacroStep { StepOrder = 1, OutputDeviceType = "SERIAL", OutputCommand = "GPO01 ON", OutputDeviceId = "dev1" }));

        CreateEngine();
        await InvokeTrigger(TriggerPayload(id));

        // Step 1 (output) still ran despite the missing delegate on step 0.
        _bus.Verify(b => b.PublishAsync(It.IsAny<HardwareOutputCommand>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Trigger_StepThrows_IsIsolated_AndNextStepsRun()
    {
        var secondRan = false;
        _registry.RegisterAction(ActionId.PlayerPlay, _ => throw new InvalidOperationException("boom"));
        _registry.RegisterAction(ActionId.MicOn,      _ => { secondRan = true; return Task.CompletedTask; });

        var id = Guid.NewGuid();
        _repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(MacroWith(id,
            new MacroStep { StepOrder = 0, ActionId = ActionId.PlayerPlay },
            new MacroStep { StepOrder = 1, ActionId = ActionId.MicOn }));

        CreateEngine();
        var act = async () => await InvokeTrigger(TriggerPayload(id));

        await act.Should().NotThrowAsync();      // exception swallowed & logged
        secondRan.Should().BeTrue();             // subsequent step still executed
    }

    // ── Ścieżka B: komendy wyjściowe ─────────────────────────────────────────────

    [Fact]
    public async Task Trigger_SerialOutputStep_PublishesHardwareCommand()
    {
        HardwareOutputCommand? published = null;
        _bus.Setup(b => b.PublishAsync(It.IsAny<HardwareOutputCommand>(), It.IsAny<CancellationToken>()))
            .Callback<HardwareOutputCommand, CancellationToken>((c, _) => published = c)
            .Returns(Task.CompletedTask);

        var id = Guid.NewGuid();
        _repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(MacroWith(id,
            new MacroStep { StepOrder = 0, OutputDeviceType = "SERIAL", OutputCommand = "GPO01 ON", OutputDeviceId = "dev1" }));

        CreateEngine();
        await InvokeTrigger(TriggerPayload(id));

        published.Should().NotBeNull();
        published!.DeviceType.Should().Be("SERIAL");
        published.TargetDeviceId.Should().Be("dev1");
        published.Payload.Should().BeOfType<SerialCommandPayload>()
                 .Which.Command.Should().Be("GPO01 ON");
    }

    [Fact]
    public async Task Trigger_DrMixerOutputStep_PublishesDrMixerPayload()
    {
        HardwareOutputCommand? published = null;
        _bus.Setup(b => b.PublishAsync(It.IsAny<HardwareOutputCommand>(), It.IsAny<CancellationToken>()))
            .Callback<HardwareOutputCommand, CancellationToken>((c, _) => published = c)
            .Returns(Task.CompletedTask);

        var id = Guid.NewGuid();
        _repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(MacroWith(id,
            new MacroStep { StepOrder = 0, OutputDeviceType = "DR_MIXER", OutputCommand = "GPO 1", OutputDeviceId = "mix" }));

        CreateEngine();
        await InvokeTrigger(TriggerPayload(id));

        published!.Payload.Should().BeOfType<DrMixerPayload>();
    }

    [Fact]
    public async Task Trigger_UnknownOutputDeviceType_FallsBackToSerialPayload()
    {
        HardwareOutputCommand? published = null;
        _bus.Setup(b => b.PublishAsync(It.IsAny<HardwareOutputCommand>(), It.IsAny<CancellationToken>()))
            .Callback<HardwareOutputCommand, CancellationToken>((c, _) => published = c)
            .Returns(Task.CompletedTask);

        var id = Guid.NewGuid();
        _repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(MacroWith(id,
            new MacroStep { StepOrder = 0, OutputDeviceType = "MIDI", OutputCommand = "X", OutputDeviceId = "d" }));

        CreateEngine();
        await InvokeTrigger(TriggerPayload(id));

        published!.Payload.Should().BeOfType<SerialCommandPayload>(); // default branch
    }

    [Fact]
    public async Task Trigger_OutputStep_MissingCommand_PublishesNothing()
    {
        var id = Guid.NewGuid();
        _repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(MacroWith(id,
            new MacroStep { StepOrder = 0, OutputDeviceType = "SERIAL", OutputCommand = null }));

        CreateEngine();
        await InvokeTrigger(TriggerPayload(id));

        _bus.Verify(b => b.PublishAsync(It.IsAny<HardwareOutputCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Wejście / walidacja ──────────────────────────────────────────────────────

    [Fact]
    public async Task Trigger_InvalidGuid_DoesNotQueryRepository()
    {
        CreateEngine();
        var payload = new ParameterizedPayload(new KeyboardPayload("Macro", false, false, false), "not-a-guid");

        await InvokeTrigger(payload);

        _repo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Trigger_NonParameterizedPayload_DoesNotQueryRepository()
    {
        CreateEngine();

        await InvokeTrigger(new KeyboardPayload("Macro", false, false, false)); // no GUID carrier

        _repo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Trigger_MacroNotFound_DoesNothing()
    {
        var id = Guid.NewGuid();
        _repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync((Macro?)null);

        CreateEngine();
        var act = async () => await InvokeTrigger(TriggerPayload(id));

        await act.Should().NotThrowAsync();
        _bus.Verify(b => b.PublishAsync(It.IsAny<HardwareOutputCommand>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
