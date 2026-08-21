using Microsoft.Extensions.DependencyInjection;

namespace RDM.Core.Interfaces;

/// <summary>
/// Wtyczka sterownika sprzętowego. Implementacje rejestrują własne usługi
/// w kontenerze DI bez modyfikacji rdzenia systemu.
/// </summary>
public interface IDriverPlugin
{
    string PluginId     { get; }
    string Description  { get; }

    void Register(IServiceCollection services);
}

/// <summary>Wtyczka rejestrująca własny sterownik wejściowy (IHostedService).</summary>
public interface IInputDriverPlugin : IDriverPlugin { }

/// <summary>Wtyczka rejestrująca własny sterownik wyjściowy (subskrybent HardwareOutputCommand).</summary>
public interface IOutputDriverPlugin : IDriverPlugin { }
