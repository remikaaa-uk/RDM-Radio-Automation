using RDM.Core.Events;

namespace RDM.Core.Interfaces;

public interface IHardwareLearnService
{
    bool IsLearningActive { get; }
    void StartLearning(Guid mappingId, Action<Guid, HardwareInputEvent> onCompleted);
    void CancelLearning();
    void HandleEvent(HardwareInputEvent evt);
}
