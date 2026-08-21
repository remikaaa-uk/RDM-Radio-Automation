using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Hardware;

public sealed class MidiInputDriver : IHostedService, IDisposable, RDM.Core.Interfaces.IMidiLearnScanner
{
    private readonly IEventBus _eventBus;
    private readonly ILogger<MidiInputDriver> _logger;
    private readonly IReadOnlyList<string> _configuredDeviceNames;

    private readonly List<InputDevice> _openDevices = new();
    private readonly List<InputDevice> _scanDevices = new();

    public MidiInputDriver(IEventBus eventBus, IConfiguration config, ILogger<MidiInputDriver> logger)
    {
        _eventBus = eventBus;
        _logger   = logger;

        _configuredDeviceNames = config
            .GetSection("hardware:midi:input_devices")
            .GetChildren()
            .Select(s => s.Value ?? string.Empty)
            .Where(s => s.Length > 0)
            .ToArray();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_configuredDeviceNames.Count == 0)
        {
            _logger.LogInformation("MidiInputDriver: brak skonfigurowanych urządzeń — otwieram wszystkie dostępne urządzenia MIDI");
            _ = Task.Run(OpenAllDevicesAsync, cancellationToken);
            return Task.CompletedTask;
        }

        var available = InputDevice.GetAll().Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in _configuredDeviceNames)
        {
            if (!available.Contains(name))
            {
                _logger.LogWarning("MidiInputDriver: urządzenie '{Device}' nie jest dostępne", name);
                continue;
            }

            try
            {
                var device = InputDevice.GetByName(name);
                device.EventReceived += (_, e) => OnMidiEventReceived(name, e.Event);
                device.ErrorOccurred += (_, e) => _logger.LogError(e.Exception, "Błąd MIDI Input '{Device}'", name);
                device.StartEventsListening();
                _openDevices.Add(device);
                _logger.LogInformation("MidiInputDriver: otwarto urządzenie '{Device}'", name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MidiInputDriver: nie można otworzyć urządzenia '{Device}'", name);
            }
        }

        return Task.CompletedTask;
    }

    private void OpenAllDevicesAsync()
    {
        _logger.LogInformation("MidiInputDriver: skanowanie wszystkich urządzeń MIDI...");
        var available = InputDevice.GetAll().ToList();
        _logger.LogInformation("MidiInputDriver: znaleziono {Count} urządzeń MIDI", available.Count);

        foreach (var device in available)
        {
            try
            {
                device.EventReceived += (_, e) => OnMidiEventReceived(device.Name, e.Event);
                device.ErrorOccurred += (_, e) => _logger.LogWarning("MidiInputDriver error '{Device}': {Msg}", device.Name, e.Exception.Message);
                device.StartEventsListening();
                _openDevices.Add(device);
                _logger.LogInformation("MidiInputDriver: otwarto '{Device}'", device.Name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MidiInputDriver: nie można otworzyć '{Device}'", device.Name);
                try { device.Dispose(); } catch { /* best-effort */ }
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var device in _openDevices)
        {
            try
            {
                device.StopEventsListening();
                device.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MidiInputDriver: błąd podczas zamykania urządzenia '{Device}'", device.Name);
            }
        }

        _openDevices.Clear();
        return Task.CompletedTask;
    }

    private void OnMidiEventReceived(string deviceName, MidiEvent midiEvent)
    {
        IHardwarePayload? payload = midiEvent switch
        {
            NoteOnEvent noteOn when noteOn.Velocity > 0 =>
                new MidiNotePayload((byte)noteOn.Channel, (byte)noteOn.NoteNumber, (byte)noteOn.Velocity, isNoteOn: true),

            ControlChangeEvent cc =>
                new MidiCcPayload((byte)cc.Channel, (byte)cc.ControlNumber, (byte)cc.ControlValue),

            _ => null  // NoteOff i NoteOn z velocity=0 są ignorowane
        };

        if (payload is null) return;

        var evt = new HardwareInputEvent(
            DeviceId:   deviceName,
            DeviceType: "MIDI",
            Payload:    payload,
            Timestamp:  DateTime.UtcNow);

        _ = Task.Run(() => _eventBus.PublishAsync(evt));
    }

    // ── IMidiLearnScanner ─────────────────────────────────────────────────────

    public Task StartScanAsync()
    {
        return Task.Run(() =>
        {
            StopScan();

            _logger.LogInformation("MidiInputDriver: StartScanAsync — enumeruję urządzenia MIDI...");
            var available = InputDevice.GetAll().ToList();
            _logger.LogInformation("MidiInputDriver: StartScanAsync — znaleziono {Count} urządzeń MIDI", available.Count);

            foreach (var device in available)
            {
                if (_openDevices.Any(d => d.Name.Equals(device.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    device.Dispose();
                    continue;
                }

                try
                {
                    device.EventReceived += (_, e) => OnMidiEventReceived(device.Name, e.Event);
                    device.ErrorOccurred += (_, e) => _logger.LogWarning("MidiInputDriver scan error '{Device}': {Msg}", device.Name, e.Exception.Message);
                    device.StartEventsListening();
                    _scanDevices.Add(device);
                    _logger.LogInformation("MidiInputDriver: scan otwarto '{Device}'", device.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MidiInputDriver: scan nie można otworzyć '{Device}'", device.Name);
                    try { device.Dispose(); } catch { /* best-effort */ }
                }
            }
        });
    }

    public void StopScan()
    {
        foreach (var device in _scanDevices)
        {
            try
            {
                device.StopEventsListening();
                device.Dispose();
            }
            catch { /* best-effort */ }
        }
        _scanDevices.Clear();
        _logger.LogInformation("MidiInputDriver: scan zatrzymany");
    }

    public void Dispose()
    {
        StopScan();
        foreach (var device in _openDevices)
        {
            try { device.Dispose(); } catch { /* best-effort */ }
        }
        _openDevices.Clear();
    }
}
