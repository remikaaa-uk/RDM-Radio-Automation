using Microsoft.Extensions.Hosting;

namespace RDM.UI.Services;

/// <summary>
/// Bridges the DI container with the IHost reference built by WebApplication.
/// Registered as singleton before host.Build(); Host is set immediately after
/// Build() returns so MainViewModel can call IHost.StopAsync during shutdown.
///
/// IHost itself is not auto-registered in the WebApplication DI container,
/// hence this indirection.
/// </summary>
public sealed class HostAccessor
{
    public IHost? Host { get; set; }
}
