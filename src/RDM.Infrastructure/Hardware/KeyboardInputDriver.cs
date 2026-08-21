using RDM.Core.Events;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Hardware;

public sealed class KeyboardInputDriver : IKeyboardInputDriver
{
    private readonly IEventBus _eventBus;

    public KeyboardInputDriver(IEventBus eventBus)
    {
        _eventBus = eventBus;
    }

    public void HandleKeyDown(string keyCode, bool altPressed, bool ctrlPressed, bool shiftPressed)
    {
        var payload = new KeyboardPayload(keyCode, altPressed, ctrlPressed, shiftPressed);
        var evt = new HardwareInputEvent(
            DeviceId:   "keyboard_main",
            DeviceType: "KEYBOARD",
            Payload:    payload,
            Timestamp:  DateTime.UtcNow);

        _ = _eventBus.PublishAsync(evt);
    }
}
