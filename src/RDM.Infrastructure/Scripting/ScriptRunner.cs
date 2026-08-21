using Microsoft.Extensions.Logging;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Scripting;

/// <summary>
/// Rejestruje obsługę AutomationRunScript w IActionRegistry.
/// TargetParameter w TriggerActionMapping = GUID skryptu z tabeli scripts.
/// Skrypty omijają kolejkę ActionRouter (jak EmergencyPanic) — mają własny timeout 5s.
/// </summary>
public sealed class ScriptRunner
{
    private readonly IActionRegistry  _actionRegistry;
    private readonly IScriptRepository _scriptRepository;
    private readonly IScriptEngine    _scriptEngine;
    private readonly ILogger<ScriptRunner> _logger;

    public ScriptRunner(
        IActionRegistry       actionRegistry,
        IScriptRepository     scriptRepository,
        IScriptEngine         scriptEngine,
        ILogger<ScriptRunner> logger)
    {
        _actionRegistry   = actionRegistry;
        _scriptRepository = scriptRepository;
        _scriptEngine     = scriptEngine;
        _logger           = logger;

        _actionRegistry.RegisterAction(ActionId.AutomationRunScript, OnRunScript);
    }

    private async Task OnRunScript(IHardwarePayload payload)
    {
        var scriptIdStr = (payload is ParameterizedPayload p) ? p.Parameter : null;

        if (string.IsNullOrWhiteSpace(scriptIdStr) || !Guid.TryParse(scriptIdStr, out var scriptId))
        {
            _logger.LogWarning("ScriptRunner: brak prawidłowego Guid skryptu w TargetParameter");
            return;
        }

        var script = await _scriptRepository.GetByIdAsync(scriptId);
        if (script is null)
        {
            _logger.LogWarning("ScriptRunner: nie znaleziono skryptu {ScriptId}", scriptId);
            return;
        }

        _logger.LogInformation("ScriptRunner: uruchamiam skrypt '{Name}' ({Language})", script.Name, script.Language);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await _scriptEngine.RunAsync(script.ScriptBody, cts.Token);

        if (result.Success)
            _logger.LogInformation("ScriptRunner: skrypt '{Name}' zakończony w {Ms}ms", script.Name, result.ElapsedMs);
        else
            _logger.LogWarning("ScriptRunner: skrypt '{Name}' zakończony błędem: {Error}", script.Name, result.Error);
    }
}
