using RDM.Core.Scripting;

namespace RDM.Core.Interfaces;

public interface IScriptEngine
{
    /// <summary>Uruchamia skrypt w izolowanym środowisku z timeout 5s.</summary>
    Task<ScriptResult> RunAsync(string scriptBody, CancellationToken ct = default);
}
