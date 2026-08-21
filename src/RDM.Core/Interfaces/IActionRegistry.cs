using RDM.Core.Hardware;

namespace RDM.Core.Interfaces;

public interface IActionRegistry
{
    void RegisterAction(ActionId actionId, Func<IHardwarePayload, Task> executionDelegate);
    Func<IHardwarePayload, Task>? GetActionDelegate(ActionId actionId);
}
