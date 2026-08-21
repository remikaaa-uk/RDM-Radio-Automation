using RDM.Shared.Enums;

namespace RDM.Core.Entities;

public sealed class AudioDevice
{
    public string DeviceId { get; init; } = string.Empty;
    public string StudioId { get; init; } = string.Empty;
    public string SystemDeviceId { get; init; } = string.Empty;
    public string FriendlyName { get; init; } = string.Empty;
    public DriverType DriverType { get; init; }
    public bool IsAvailable { get; init; }
    public DateTime? LastSeenAt { get; init; }
}
