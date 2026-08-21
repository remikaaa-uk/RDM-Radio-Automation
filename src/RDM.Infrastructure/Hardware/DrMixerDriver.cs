using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;
using System.IO.Ports;

namespace RDM.Infrastructure.Hardware;

/// <summary>
/// Bidirectional D&amp;R mixer driver over a virtual COM port (USB-serial).
/// Input: parses fader-start and GPI events → publishes HardwareInputEvent.
/// Output: subscribes to HardwareOutputCommand for DeviceType "DR_MIXER" and sends GPO commands.
///
/// Protocol (D&amp;R ASCII, 38400 8N1, LF-terminated):
///   Received from mixer:  "CH01 FADER START\n", "CH01 FADER STOP\n", "GPI01 ON\n", "GPI01 OFF\n"
///   Sent to mixer:        "GPO01 ON\n", "GPO01 OFF\n"
/// </summary>
public sealed class DrMixerDriver : IHostedService, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly ILogger<DrMixerDriver> _logger;
    private readonly string? _portName;
    private readonly int _baudRate;
    private readonly string _deviceId;

    private SerialPort?         _port;
    private CancellationTokenSource? _cts;
    private Task?               _readLoop;

    public DrMixerDriver(IEventBus eventBus, IConfiguration config, ILogger<DrMixerDriver> logger)
    {
        _eventBus = eventBus;
        _logger   = logger;

        var section = config.GetSection("hardware:dr_mixer");
        _portName = section["port"];
        _baudRate  = int.TryParse(section["baud_rate"], out var br) ? br : 38400;
        _deviceId  = section["device_id"] ?? "dr_mixer_main";
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_portName))
        {
            _logger.LogInformation("DrMixerDriver: brak skonfigurowanego portu COM — sterownik nieaktywny");
            return Task.CompletedTask;
        }

        try
        {
            _port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
            {
                NewLine      = "\n",
                ReadTimeout  = 500,
                WriteTimeout = 500,
                Encoding     = System.Text.Encoding.ASCII
            };
            _port.Open();

            _eventBus.Subscribe<HardwareOutputCommand>(OnOutputCommand);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token), _cts.Token);

            _logger.LogInformation("DrMixerDriver: połączono z mikserem D&R na porcie {Port} ({Baud} baud)", _portName, _baudRate);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DrMixerDriver: nie można otworzyć portu {Port}", _portName);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();

        if (_readLoop is not null)
        {
            try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch { /* best-effort */ }
        }

        _port?.Close();
        _logger.LogInformation("DrMixerDriver: rozłączono z mikserem D&R");
    }

    // ── Serial read loop ──────────────────────────────────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _port?.IsOpen == true)
        {
            try
            {
                var line = await Task.Run(() => _port.ReadLine(), ct);
                ProcessLine(line.Trim());
            }
            catch (OperationCanceledException) { break; }
            catch (TimeoutException) { /* normalny brak danych */ }
            catch (Exception ex) when (_port?.IsOpen == true)
            {
                _logger.LogError(ex, "DrMixerDriver: błąd odczytu z portu COM");
                await Task.Delay(500, ct);
            }
        }
    }

    private void ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        // "CH01 FADER START" → Fader up (isActive=true)
        // "CH01 FADER STOP"  → Fader down (isActive=false)
        if (line.StartsWith("CH", StringComparison.OrdinalIgnoreCase))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return;

            if (!int.TryParse(parts[0][2..], out var channel)) return;

            var isStart  = parts[2].Equals("START", StringComparison.OrdinalIgnoreCase);
            var isStop   = parts[2].Equals("STOP",  StringComparison.OrdinalIgnoreCase);
            if (!isStart && !isStop) return;

            Publish(new DrMixerPayload("Fader", channel, isStart ? 1f : 0f, isStart));
            return;
        }

        // "GPI01 ON" / "GPI01 OFF"
        if (line.StartsWith("GPI", StringComparison.OrdinalIgnoreCase))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return;

            if (!int.TryParse(parts[0][3..], out var index)) return;
            var isActive = parts[1].Equals("ON", StringComparison.OrdinalIgnoreCase);

            Publish(new DrMixerPayload("Gpi", index, isActive ? 1f : 0f, isActive));
        }
    }

    private void Publish(DrMixerPayload payload)
    {
        var evt = new HardwareInputEvent(
            DeviceId:   _deviceId,
            DeviceType: "DR_MIXER",
            Payload:    payload,
            Timestamp:  DateTime.UtcNow);

        _ = _eventBus.PublishAsync(evt);
    }

    // ── GPO output ────────────────────────────────────────────────────────────

    private void OnOutputCommand(HardwareOutputCommand cmd)
    {
        if (!string.Equals(cmd.DeviceType, "DR_MIXER", StringComparison.OrdinalIgnoreCase)) return;
        if (_port is not { IsOpen: true }) return;

        if (cmd.Payload is not DrMixerPayload dr) return;
        if (!string.Equals(dr.Target, "Gpo", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var state = dr.IsActive ? "ON" : "OFF";
            var command = $"GPO{dr.Index:D2} {state}\n";
            _port.Write(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DrMixerDriver: błąd wysyłania GPO do miksera");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _port?.Dispose();
    }
}
