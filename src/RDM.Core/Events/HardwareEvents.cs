using RDM.Core.Hardware;

namespace RDM.Core.Events;

public record HardwareInputEvent(
    string DeviceId,
    string DeviceType,
    IHardwarePayload Payload,
    DateTime Timestamp);

public record HardwareOutputCommand(
    string TargetDeviceId,
    string DeviceType,
    IHardwarePayload Payload,
    DateTime Timestamp);
