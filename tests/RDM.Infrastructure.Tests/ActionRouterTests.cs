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
/// Unit tests for <see cref="ActionRouter"/> — no database. The router subscribes to
/// <see cref="HardwareInputEvent"/> and dispatches actions asynchronously (immediate Task.Run for
/// critical/playback actions, a background channel for the rest), so execution is observed via a
/// signalling spy delegate; the throttle/dedup counters are synchronous and asserted directly.
/// </summary>
public sealed class ActionRouterTests
{
    private readonly TestBus                        _bus      = new();
    private readonly ActionRegistry                 _registry = new();
    private readonly Mock<ITriggerMappingCache>     _cache    = new();
    private readonly Mock<IHardwareLearnService>    _learn    = new();

    private readonly SemaphoreSlim _executed = new(0);
    private IHardwarePayload? _captured;

    private ActionRouter CreateRouter() =>
        new(_bus, _registry, _cache.Object, _learn.Object, Mock.Of<ILogger<ActionRouter>>());

    private void RegisterSpy(ActionId id) =>
        _registry.RegisterAction(id, p => { _captured = p; _executed.Release(); return Task.CompletedTask; });

    private Task<bool> WasExecuted(int timeoutMs = 2000) => _executed.WaitAsync(timeoutMs);

    private void SetupMappings(params TriggerActionMapping[] maps)
    {
        IReadOnlyList<TriggerActionMapping> list = maps;
        _cache.Setup(c => c.TryGetMappings(It.IsAny<TriggerLookupKey>(), out list)).Returns(true);
    }

    private static TriggerActionMapping Map(
        ActionId action, bool enabled = true, string? sourceDeviceId = null, string? param = null) =>
        new() { TargetActionId = action, IsEnabled = enabled, SourceDeviceId = sourceDeviceId, TargetParameter = param };

    private static HardwareInputEvent KeyEvent(string deviceId = "kb1") =>
        new(deviceId, "Keyboard", new KeyboardPayload("A", false, false, false), DateTime.UtcNow);

    private static HardwareInputEvent CcEvent(byte value = 127) =>
        new("midi1", "MIDI", new MidiCcPayload(0, 1, value), DateTime.UtcNow);

    // ── Subskrypcja ──────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_SubscribesToHardwareInput()
    {
        CreateRouter();
        _bus.HasHardwareSubscriber.Should().BeTrue();
    }

    // ── Routing podstawowy ───────────────────────────────────────────────────────

    [Fact]
    public async Task Raise_MappedAction_ExecutesDelegate()
    {
        SetupMappings(Map(ActionId.PlayerPlay));
        RegisterSpy(ActionId.PlayerPlay);
        CreateRouter();

        _bus.Raise(KeyEvent());

        (await WasExecuted()).Should().BeTrue();
    }

    [Fact]
    public async Task Raise_NoMappings_DoesNotExecute()
    {
        // cache left unset → TryGetMappings returns false → router returns early.
        RegisterSpy(ActionId.PlayerPlay);
        CreateRouter();

        _bus.Raise(KeyEvent());

        (await WasExecuted(200)).Should().BeFalse();
    }

    [Fact]
    public async Task Raise_DisabledMapping_Skipped()
    {
        SetupMappings(Map(ActionId.PlayerPlay, enabled: false));
        RegisterSpy(ActionId.PlayerPlay);
        CreateRouter();

        _bus.Raise(KeyEvent());

        (await WasExecuted(200)).Should().BeFalse();
    }

    [Fact]
    public async Task Raise_SourceDeviceIdMismatch_Skipped()
    {
        SetupMappings(Map(ActionId.PlayerPlay, sourceDeviceId: "device-A"));
        RegisterSpy(ActionId.PlayerPlay);
        CreateRouter();

        _bus.Raise(KeyEvent(deviceId: "device-B"));

        (await WasExecuted(200)).Should().BeFalse();
    }

    [Fact]
    public async Task Raise_SourceDeviceIdMatch_Executes()
    {
        SetupMappings(Map(ActionId.PlayerPlay, sourceDeviceId: "DEVICE-A")); // case-insensitive match
        RegisterSpy(ActionId.PlayerPlay);
        CreateRouter();

        _bus.Raise(KeyEvent(deviceId: "device-a"));

        (await WasExecuted()).Should().BeTrue();
    }

    // ── Tryb nauki ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Raise_LearningActive_ForwardsToLearnService_AndDoesNotRoute()
    {
        _learn.SetupGet(l => l.IsLearningActive).Returns(true);
        SetupMappings(Map(ActionId.PlayerPlay));
        RegisterSpy(ActionId.PlayerPlay);
        CreateRouter();

        var evt = KeyEvent();
        _bus.Raise(evt);

        _learn.Verify(l => l.HandleEvent(evt), Times.Once);
        (await WasExecuted(200)).Should().BeFalse();
    }

    // ── Throttling / deduplikacja (synchroniczne liczniki) ───────────────────────

    [Fact]
    public void Raise_MidiCcTwiceWithinThrottleWindow_SecondIsThrottled()
    {
        var router = CreateRouter();

        _bus.Raise(CcEvent(value: 10)); // same Ch/Controller ⇒ same signature
        _bus.Raise(CcEvent(value: 20));

        router.ThrottledEventsCount.Should().Be(1);
    }

    [Fact]
    public void Raise_PlayerActionTwiceWithinDedupWindow_SecondIsDeduplicated()
    {
        SetupMappings(Map(ActionId.PlayerPlay));
        RegisterSpy(ActionId.PlayerPlay);
        var router = CreateRouter();

        _bus.Raise(KeyEvent());
        _bus.Raise(KeyEvent());

        router.DeduplicatedEventsCount.Should().Be(1);
    }

    // ── Normalizacja / parametry payloadu ────────────────────────────────────────

    [Fact]
    public async Task Raise_MidiCcMapped_NormalizesToAnalogPayload()
    {
        SetupMappings(Map(ActionId.PlayerPlay));
        RegisterSpy(ActionId.PlayerPlay);
        CreateRouter();

        _bus.Raise(CcEvent(value: 127));

        (await WasExecuted()).Should().BeTrue();
        _captured.Should().BeOfType<NormalizedAnalogPayload>()
                 .Which.Value.Should().BeApproximately(1.0f, 0.001f);
    }

    [Fact]
    public async Task Raise_MappingWithTargetParameter_WrapsPayload()
    {
        SetupMappings(Map(ActionId.PlayerPlay, param: "cart-3"));
        RegisterSpy(ActionId.PlayerPlay);
        CreateRouter();

        _bus.Raise(KeyEvent());

        (await WasExecuted()).Should().BeTrue();
        _captured.Should().BeOfType<ParameterizedPayload>()
                 .Which.Parameter.Should().Be("cart-3");
    }

    // ── Ścieżka kolejki (akcje niekrytyczne) ─────────────────────────────────────

    [Fact]
    public async Task Raise_NonCriticalAction_ExecutedViaBackgroundQueue()
    {
        // MicOn is neither in the immediate-execution nor the dedup list → routed through the channel.
        SetupMappings(Map(ActionId.MicOn));
        RegisterSpy(ActionId.MicOn);
        CreateRouter();

        _bus.Raise(KeyEvent());

        (await WasExecuted()).Should().BeTrue();
    }

    // ── Sterowalny test double dla szyny zdarzeń ─────────────────────────────────

    private sealed class TestBus : IEventBus
    {
        private Action<HardwareInputEvent>? _handler;

        public bool HasHardwareSubscriber => _handler is not null;

        public void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class
        {
            if (typeof(TEvent) == typeof(HardwareInputEvent))
                _handler = (Action<HardwareInputEvent>)(object)handler;
        }

        public void Unsubscribe<TEvent>(Action<TEvent> handler) where TEvent : class { }

        public Task PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default) where TEvent : class =>
            Task.CompletedTask;

        /// <summary>Invoke the router's subscribed handler directly, as a real publish would.</summary>
        public void Raise(HardwareInputEvent evt) => _handler?.Invoke(evt);
    }
}
