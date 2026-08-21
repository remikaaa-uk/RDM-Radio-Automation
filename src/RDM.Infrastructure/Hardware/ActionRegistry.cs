using System.Collections.Concurrent;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Hardware;

public sealed class ActionRegistry : IActionRegistry
{
    private readonly ConcurrentDictionary<ActionId, Func<IHardwarePayload, Task>> _registry = new();

    public void RegisterAction(ActionId actionId, Func<IHardwarePayload, Task> executionDelegate)
    {
        _registry[actionId] = executionDelegate;
    }

    public Func<IHardwarePayload, Task>? GetActionDelegate(ActionId actionId)
    {
        return _registry.TryGetValue(actionId, out var action) ? action : null;
    }
}
