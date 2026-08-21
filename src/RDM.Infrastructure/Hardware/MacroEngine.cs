using Microsoft.Extensions.Logging;
using RDM.Core.Entities;
using RDM.Core.Events;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Hardware;

/// <summary>
/// Silnik makr: rejestruje obsługę AutomationTriggerMacro i wykonuje kroki makra sekwencyjnie.
/// Krok może wywoływać zarejestrowaną akcję IActionRegistry lub publikować HardwareOutputCommand.
/// </summary>
public sealed class MacroEngine
{
    private readonly IActionRegistry   _actionRegistry;
    private readonly IMacroRepository  _macroRepository;
    private readonly IEventBus         _eventBus;
    private readonly ILogger<MacroEngine> _logger;

    public MacroEngine(
        IActionRegistry      actionRegistry,
        IMacroRepository     macroRepository,
        IEventBus            eventBus,
        ILogger<MacroEngine> logger)
    {
        _actionRegistry  = actionRegistry;
        _macroRepository = macroRepository;
        _eventBus        = eventBus;
        _logger          = logger;

        _actionRegistry.RegisterAction(ActionId.AutomationTriggerMacro, OnTriggerMacro);
    }

    private async Task OnTriggerMacro(IHardwarePayload payload)
    {
        var macroIdStr = (payload is ParameterizedPayload p) ? p.Parameter : null;

        if (string.IsNullOrWhiteSpace(macroIdStr) || !Guid.TryParse(macroIdStr, out var macroId))
        {
            _logger.LogWarning("MacroEngine: brak prawidłowego Guid makra w TargetParameter");
            return;
        }

        var macro = await _macroRepository.GetByIdAsync(macroId);
        if (macro is null)
        {
            _logger.LogWarning("MacroEngine: nie znaleziono makra {MacroId}", macroId);
            return;
        }

        _logger.LogInformation("MacroEngine: uruchamiam makro '{Name}' ({Steps} kroków)", macro.Name, macro.Steps.Count);
        await ExecuteMacroAsync(macro, payload);
    }

    private async Task ExecuteMacroAsync(Macro macro, IHardwarePayload originalPayload)
    {
        foreach (var step in macro.Steps.OrderBy(s => s.StepOrder))
        {
            try
            {
                await ExecuteStepAsync(step, originalPayload);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MacroEngine: błąd w kroku {Order} makra '{Name}'", step.StepOrder, macro.Name);
            }

            if (step.DelayAfterMs > 0)
                await Task.Delay(step.DelayAfterMs);
        }
    }

    private async Task ExecuteStepAsync(MacroStep step, IHardwarePayload originalPayload)
    {
        // Ścieżka A: wywołanie zarejestrowanej akcji
        if (step.ActionId.HasValue)
        {
            var actionDelegate = _actionRegistry.GetActionDelegate(step.ActionId.Value);
            if (actionDelegate is null)
            {
                _logger.LogWarning("MacroEngine: brak delegata dla akcji {ActionId}", step.ActionId);
                return;
            }

            IHardwarePayload stepPayload = string.IsNullOrEmpty(step.Parameter)
                ? originalPayload
                : new ParameterizedPayload(originalPayload, step.Parameter);

            await actionDelegate(stepPayload);
            return;
        }

        // Ścieżka B: bezpośrednia komenda wyjściowa
        if (!string.IsNullOrEmpty(step.OutputDeviceType) && !string.IsNullOrEmpty(step.OutputCommand))
        {
            IHardwarePayload outputPayload = step.OutputDeviceType.ToUpperInvariant() switch
            {
                "SERIAL" => new SerialCommandPayload(step.OutputCommand),
                "DR_MIXER" => new DrMixerPayload("Gpo", 1, 1f, true),
                _ => new SerialCommandPayload(step.OutputCommand)
            };

            var cmd = new HardwareOutputCommand(
                TargetDeviceId: step.OutputDeviceId ?? string.Empty,
                DeviceType:     step.OutputDeviceType,
                Payload:        outputPayload,
                Timestamp:      DateTime.UtcNow);

            await _eventBus.PublishAsync(cmd);
        }
    }
}
