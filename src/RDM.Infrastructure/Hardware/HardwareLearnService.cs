using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Hardware;

public sealed class HardwareLearnService : IHardwareLearnService
{
    private readonly IMidiLearnScanner _midiScanner;
    private readonly ILogger<HardwareLearnService> _logger;

    private readonly object _lock = new();
    private Guid? _activeMappingId;
    private Action<Guid, HardwareInputEvent>? _onCompletedCallback;

    public HardwareLearnService(IMidiLearnScanner midiScanner, ILogger<HardwareLearnService> logger)
    {
        _midiScanner = midiScanner;
        _logger      = logger;
    }

    public bool IsLearningActive
    {
        get { lock (_lock) return _activeMappingId.HasValue; }
    }

    public void StartLearning(Guid mappingId, Action<Guid, HardwareInputEvent> onCompleted)
    {
        lock (_lock)
        {
            _activeMappingId = mappingId;
            _onCompletedCallback = onCompleted;
        }

        _logger.LogInformation("HardwareLearnService: StartLearning {Id} — uruchamiam MIDI scan", mappingId);
        _ = _midiScanner.StartScanAsync();
    }

    public void CancelLearning()
    {
        lock (_lock)
        {
            _activeMappingId = null;
            _onCompletedCallback = null;
        }

        _logger.LogInformation("HardwareLearnService: CancelLearning — zatrzymuję MIDI scan");
        _midiScanner.StopScan();
    }

    public void HandleEvent(HardwareInputEvent evt)
    {
        Guid? mappingId;
        Action<Guid, HardwareInputEvent>? callback;

        lock (_lock)
        {
            if (!_activeMappingId.HasValue) return;

            mappingId = _activeMappingId;
            callback = _onCompletedCallback;
            _activeMappingId = null;
            _onCompletedCallback = null;
        }

        _logger.LogInformation("HardwareLearnService: wykryto zdarzenie device={Dev} type={Type} sig='{Sig}'",
            evt.DeviceId, evt.DeviceType, evt.Payload.Signature);

        if (evt.DeviceType == "MOUSE") return;

        _midiScanner.StopScan();
        callback!(mappingId!.Value, evt);
    }
}
