using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;
using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;
using System.Collections.Concurrent;

namespace RDM.Infrastructure.Hardware;

public sealed class MidiOutputDriver : IDisposable
{
    private readonly ILogger<MidiOutputDriver> _logger;
    private readonly ConcurrentDictionary<string, OutputDevice?> _deviceCache = new(StringComparer.OrdinalIgnoreCase);

    public MidiOutputDriver(IEventBus eventBus, ILogger<MidiOutputDriver> logger)
    {
        _logger = logger;
        eventBus.Subscribe<HardwareOutputCommand>(OnOutputCommand);
    }

    private void OnOutputCommand(HardwareOutputCommand cmd)
    {
        if (!string.Equals(cmd.DeviceType, "MIDI", StringComparison.OrdinalIgnoreCase)) return;

        var device = GetOrOpenDevice(cmd.TargetDeviceId);
        if (device is null) return;

        try
        {
            switch (cmd.Payload)
            {
                case MidiNotePayload note:
                    MidiEvent midiEvent = note.IsNoteOn
                        ? new NoteOnEvent((SevenBitNumber)note.Note, (SevenBitNumber)note.Velocity)
                          { Channel = (FourBitNumber)note.Channel }
                        : new NoteOffEvent((SevenBitNumber)note.Note, (SevenBitNumber)note.Velocity)
                          { Channel = (FourBitNumber)note.Channel };

                    device.SendEvent(midiEvent);
                    break;

                case MidiCcPayload cc:
                    device.SendEvent(new ControlChangeEvent((SevenBitNumber)cc.Controller, (SevenBitNumber)cc.Value)
                    {
                        Channel = (FourBitNumber)cc.Channel
                    });
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MidiOutputDriver: błąd wysyłania do '{Device}'", cmd.TargetDeviceId);
            _deviceCache.TryRemove(cmd.TargetDeviceId, out _);
        }
    }

    private OutputDevice? GetOrOpenDevice(string deviceId)
    {
        return _deviceCache.GetOrAdd(deviceId, name =>
        {
            try
            {
                var device = OutputDevice.GetByName(name);
                _logger.LogInformation("MidiOutputDriver: otwarto urządzenie wyjściowe '{Device}'", name);
                return device;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MidiOutputDriver: nie można otworzyć urządzenia '{Device}'", name);
                return null;
            }
        });
    }

    public void Dispose()
    {
        foreach (var kv in _deviceCache)
        {
            try { kv.Value?.Dispose(); } catch { /* best-effort */ }
        }
        _deviceCache.Clear();
    }
}
