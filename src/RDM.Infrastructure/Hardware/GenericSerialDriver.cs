using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;
using System.IO.Ports;

namespace RDM.Infrastructure.Hardware;

/// <summary>
/// Ogólny sterownik RS-232 do komunikacji z matrycami audio i innymi urządzeniami.
/// Wysyła komendy ASCII na skonfigurowane porty COM.
/// Subskrybuje HardwareOutputCommand z DeviceType == "SERIAL".
///
/// Konfiguracja (rdm.config.json):
/// {
///   "hardware": {
///     "serial_drivers": [
///       { "device_id": "audio_matrix_1", "port": "COM4", "baud_rate": 9600, "terminator": "\r\n" }
///     ]
///   }
/// }
/// </summary>
public sealed class GenericSerialDriver : IHostedService, IDisposable
{
    private readonly IEventBus _eventBus;
    private readonly ILogger<GenericSerialDriver> _logger;
    private readonly IReadOnlyList<SerialDeviceConfig> _configs;

    private readonly Dictionary<string, SerialPort> _ports = new(StringComparer.OrdinalIgnoreCase);

    public GenericSerialDriver(IEventBus eventBus, IConfiguration config, ILogger<GenericSerialDriver> logger)
    {
        _eventBus = eventBus;
        _logger   = logger;
        _configs  = LoadConfigs(config);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_configs.Count == 0)
        {
            _logger.LogInformation("GenericSerialDriver: brak skonfigurowanych portów serial — sterownik nieaktywny");
            return Task.CompletedTask;
        }

        foreach (var cfg in _configs)
        {
            if (string.IsNullOrWhiteSpace(cfg.Port)) continue;

            try
            {
                var port = new SerialPort(cfg.Port, cfg.BaudRate, Parity.None, 8, StopBits.One)
                {
                    NewLine      = cfg.Terminator,
                    WriteTimeout = 500,
                    Encoding     = System.Text.Encoding.ASCII
                };
                port.Open();
                _ports[cfg.DeviceId] = port;

                _logger.LogInformation(
                    "GenericSerialDriver: otwarto port {Port} dla urządzenia '{DeviceId}' ({Baud} baud)",
                    cfg.Port, cfg.DeviceId, cfg.BaudRate);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GenericSerialDriver: nie można otworzyć portu {Port} dla '{DeviceId}'",
                    cfg.Port, cfg.DeviceId);
            }
        }

        if (_ports.Count > 0)
            _eventBus.Subscribe<HardwareOutputCommand>(OnOutputCommand);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var (_, port) in _ports)
        {
            try { port.Close(); } catch { /* best-effort */ }
        }
        _logger.LogInformation("GenericSerialDriver: zamknięto {Count} portów", _ports.Count);
        return Task.CompletedTask;
    }

    private void OnOutputCommand(HardwareOutputCommand cmd)
    {
        if (!string.Equals(cmd.DeviceType, "SERIAL", StringComparison.OrdinalIgnoreCase)) return;

        if (!_ports.TryGetValue(cmd.TargetDeviceId, out var port) || !port.IsOpen)
        {
            _logger.LogWarning("GenericSerialDriver: brak aktywnego portu dla '{DeviceId}'", cmd.TargetDeviceId);
            return;
        }

        var command = cmd.Payload switch
        {
            SerialCommandPayload sc  => sc.Command,
            KeyboardPayload kb       => kb.Signature,
            _                        => null
        };

        if (string.IsNullOrWhiteSpace(command)) return;

        try
        {
            port.WriteLine(command);
            _logger.LogDebug("GenericSerialDriver: wysłano '{Command}' do '{DeviceId}'", command, cmd.TargetDeviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenericSerialDriver: błąd wysyłania do '{DeviceId}'", cmd.TargetDeviceId);
        }
    }

    private static IReadOnlyList<SerialDeviceConfig> LoadConfigs(IConfiguration config)
    {
        var section = config.GetSection("hardware:serial_drivers");
        var children = section.GetChildren().ToList();

        return children
            .Select(c => new SerialDeviceConfig(
                DeviceId:   c["device_id"] ?? string.Empty,
                Port:       c["port"] ?? string.Empty,
                BaudRate:   int.TryParse(c["baud_rate"], out var br) ? br : 9600,
                Terminator: (c["terminator"] ?? "\r\n").Replace("\\r", "\r").Replace("\\n", "\n")))
            .Where(c => !string.IsNullOrWhiteSpace(c.DeviceId))
            .ToList();
    }

    public void Dispose()
    {
        foreach (var (_, port) in _ports)
            port.Dispose();
    }

    private sealed record SerialDeviceConfig(
        string DeviceId,
        string Port,
        int    BaudRate,
        string Terminator);
}
