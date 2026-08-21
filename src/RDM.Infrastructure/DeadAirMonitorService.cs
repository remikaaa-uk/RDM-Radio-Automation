using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Core.Services;

namespace RDM.Infrastructure;

/// <summary>
/// Polls the master output level once per second and reports how long it has been silent to
/// <see cref="PlaylistEngine.OnDeadAirTickAsync"/>, which decides whether to trigger dead-air
/// recovery (AUTO-mode only, gated by AudioSettings). This service owns the timer; the engine
/// owns the decision — keeping PlaylistEngine's "owns no timers" invariant intact.
///
/// The dB floor is deliberately low (≈ −57 dBFS on the 0–100 VU scale) so only genuine silence —
/// a stopped engine, a broken/silent file, or a muted feed — counts; the seconds threshold in
/// AudioSettings then guards against brief inter-track gaps.
/// </summary>
public sealed class DeadAirMonitorService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    // Program-audio floor on GetPlaylistLevel's 0–100 VU scale: (dBFS + 60) / 60 * 100.
    // 5.0 ≈ −57 dBFS — below any real program audio, at the digital-silence noise floor.
    private const double SilenceFloor = 5.0;

    private readonly IAudioEngine                    _audioEngine;
    private readonly PlaylistEngine                  _engine;
    private readonly ILogger<DeadAirMonitorService>  _logger;

    private long _silentMs;

    public DeadAirMonitorService(
        IAudioEngine                    audioEngine,
        PlaylistEngine                  engine,
        ILogger<DeadAirMonitorService>  logger)
    {
        _audioEngine = audioEngine;
        _engine      = engine;
        _logger      = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DeadAirMonitorService started.");

        using var timer = new PeriodicTimer(PollInterval);
        var last = DateTime.UtcNow;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = DateTime.UtcNow;
                var dtMs = (long)(now - last).TotalMilliseconds;
                last = now;

                try
                {
                    var (left, right) = _audioEngine.GetPlaylistLevel();
                    bool silent = left < SilenceFloor && right < SilenceFloor;
                    _silentMs = silent ? _silentMs + dtMs : 0;

                    await _engine.OnDeadAirTickAsync(_silentMs, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Dead-air poll failed");
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }

        _logger.LogInformation("DeadAirMonitorService stopped.");
    }
}
