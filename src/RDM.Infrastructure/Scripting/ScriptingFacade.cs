using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Scripting;

/// <summary>
/// Implementacja IScriptingFacade — jedyne API wystawiane skryptom.
/// Deleguje do IActionRegistry i IEventBus; nigdy nie ujawnia wewnętrznych serwisów.
/// </summary>
public sealed class ScriptingFacade : IScriptingFacade
{
    private readonly IActionRegistry _actionRegistry;
    private readonly IEventBus       _eventBus;
    private readonly IAudioEngine    _audioEngine;
    private readonly ILogger<ScriptingFacade> _logger;

    private static readonly KeyboardPayload DummyPayload =
        new("Script", altPressed: false, ctrlPressed: false, shiftPressed: false);

    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(4000);

    public ScriptingFacade(IActionRegistry actionRegistry, IEventBus eventBus, IAudioEngine audioEngine, ILogger<ScriptingFacade> logger)
    {
        _actionRegistry = actionRegistry;
        _eventBus       = eventBus;
        _audioEngine    = audioEngine;
        _logger         = logger;
    }

    // ── Player ────────────────────────────────────────────────────────────────

    public void PlayerPlay()  => Invoke(ActionId.PlayerPlay);
    public void PlayerStop()  => Invoke(ActionId.PlayerStop);
    public void PlayerNext()  => Invoke(ActionId.PlayerNext);
    public void PlayerPause() => Invoke(ActionId.PlayerPause);

    // ── Mic ───────────────────────────────────────────────────────────────────

    public void MicOn()     => Invoke(ActionId.MicOn);
    public void MicOff()    => Invoke(ActionId.MicOff);
    public void MicToggle() => Invoke(ActionId.MicToggle);

    // ── Cartwall ──────────────────────────────────────────────────────────────

    public void CartSelectTab(int tabIndex)
    {
        var id = tabIndex switch
        {
            0 => ActionId.CartwallTab1,
            1 => ActionId.CartwallTab2,
            2 => ActionId.CartwallTab3,
            3 => ActionId.CartwallTab4,
            4 => ActionId.CartwallTab5,
            5 => ActionId.CartwallTab6,
            6 => ActionId.CartwallTab7,
            _ => ActionId.CartwallTab1
        };
        Invoke(id);
    }

    public void CartTriggerSlot(int slotIndex)
    {
        var id = slotIndex switch
        {
            0  => ActionId.CartwallTriggerSlot1,
            1  => ActionId.CartwallTriggerSlot2,
            2  => ActionId.CartwallTriggerSlot3,
            3  => ActionId.CartwallTriggerSlot4,
            4  => ActionId.CartwallTriggerSlot5,
            5  => ActionId.CartwallTriggerSlot6,
            6  => ActionId.CartwallTriggerSlot7,
            7  => ActionId.CartwallTriggerSlot8,
            8  => ActionId.CartwallTriggerSlot9,
            9  => ActionId.CartwallTriggerSlot10,
            10 => ActionId.CartwallTriggerSlot11,
            11 => ActionId.CartwallTriggerSlot12,
            12 => ActionId.CartwallTriggerSlot13,
            13 => ActionId.CartwallTriggerSlot14,
            14 => ActionId.CartwallTriggerSlot15,
            15 => ActionId.CartwallTriggerSlot16,
            _  => ActionId.CartwallTriggerSlot1
        };
        Invoke(id);
    }

    // ── AUX players ───────────────────────────────────────────────────────────

    public void AuxLoad(int index, string filePath)
    {
        try { _audioEngine.LoadAuxAsync(index, filePath).GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogWarning(ex, "ScriptingFacade: AuxLoad({Index}, '{Path}') failed", index, filePath); }
    }

    public void AuxPlay(int index)
    {
        try { _audioEngine.PlayAuxAsync(index).GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogWarning(ex, "ScriptingFacade: AuxPlay({Index}) failed", index); }
    }

    public void AuxStop(int index, int fadeMs = 0)
    {
        try { _audioEngine.StopAuxAsync(index, (uint)Math.Max(0, fadeMs)).GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogWarning(ex, "ScriptingFacade: AuxStop({Index}, {FadeMs}) failed", index, fadeMs); }
    }

    public void AuxSetLoop(int index, bool enabled)
    {
        try { _audioEngine.SetAuxLoopAsync(index, enabled).GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogWarning(ex, "ScriptingFacade: AuxSetLoop({Index}, {Enabled}) failed", index, enabled); }
    }

    public void AuxSetVolume(int index, float gain)
    {
        try { _audioEngine.SetAuxVolumeAsync(index, gain).GetAwaiter().GetResult(); }
        catch (Exception ex) { _logger.LogWarning(ex, "ScriptingFacade: AuxSetVolume({Index}, {Gain}) failed", index, gain); }
    }

    // ── Integracje zewnętrzne ─────────────────────────────────────────────────

    public void SendHttp(string url) => Invoke(ActionId.AutomationSendHttp, url);

    public void SendSerial(string deviceId, string command)
    {
        var cmd = new HardwareOutputCommand(
            TargetDeviceId: deviceId,
            DeviceType:     "SERIAL",
            Payload:        new SerialCommandPayload(command),
            Timestamp:      DateTime.UtcNow);

        _eventBus.PublishAsync(cmd).GetAwaiter().GetResult();
    }

    // ── Narzędzia skryptowe ───────────────────────────────────────────────────

    public void Log(string message) =>
        _logger.LogInformation("[Script] {Message}", message);

    public void Delay(int milliseconds)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Clamp(milliseconds, 0, (int)MaxDelay.TotalMilliseconds));
        Thread.Sleep(delay);
    }

    // ── Prywatne helpery ──────────────────────────────────────────────────────

    private void Invoke(ActionId id, string? parameter = null)
    {
        var del = _actionRegistry.GetActionDelegate(id);
        if (del is null)
        {
            _logger.LogWarning("ScriptingFacade: brak delegata dla akcji {ActionId}", id);
            return;
        }

        IHardwarePayload payload = parameter is not null
            ? new ParameterizedPayload(DummyPayload, parameter)
            : DummyPayload;

        del(payload).GetAwaiter().GetResult();
    }
}
