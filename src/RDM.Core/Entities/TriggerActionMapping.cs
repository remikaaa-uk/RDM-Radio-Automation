using RDM.Core.Hardware;

namespace RDM.Core.Entities;

public class TriggerActionMapping
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SourceDeviceType { get; set; } = string.Empty;
    public string? SourceDeviceId { get; set; }
    public string TargetSignature { get; set; } = string.Empty;
    public ActionId TargetActionId { get; set; }
    public string? TargetParameter { get; set; }
    public bool IsEnabled { get; set; } = true;
}
