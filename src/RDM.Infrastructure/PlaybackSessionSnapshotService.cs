using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Core.Services;

namespace RDM.Infrastructure;

/// <summary>
/// Saves a PlaybackSession snapshot to the database every 30 seconds.
/// PlaylistEngine.GetSnapshot() is atomic — no lock held across the await.
/// </summary>
public sealed class PlaybackSessionSnapshotService : BackgroundService
{
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(30);

    private readonly PlaylistEngine                          _playlistEngine;
    private readonly IPlaybackSessionRepository              _sessionRepo;
    private readonly ILogger<PlaybackSessionSnapshotService> _logger;

    public PlaybackSessionSnapshotService(
        PlaylistEngine                           playlistEngine,
        IPlaybackSessionRepository               sessionRepo,
        ILogger<PlaybackSessionSnapshotService>  logger)
    {
        _playlistEngine = playlistEngine;
        _sessionRepo    = sessionRepo;
        _logger         = logger;
    }

    /// <summary>
    /// Forces an immediate snapshot outside the periodic timer.
    /// Called by MainWindow shutdown sequence (step 2) to persist state before BASS_Free.
    /// </summary>
    public async Task TriggerSnapshotAsync(CancellationToken ct = default)
    {
        var snapshot = _playlistEngine.GetSnapshot();
        await _sessionRepo.UpsertAsync(snapshot, ct);
        _logger.LogInformation("PlaybackSession snapshot saved on demand (shutdown)");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PlaybackSessionSnapshotService started.");

        using var timer = new PeriodicTimer(SnapshotInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var snapshot = _playlistEngine.GetSnapshot();
                    await _sessionRepo.UpsertAsync(snapshot, stoppingToken);
                    _logger.LogDebug("PlaybackSession snapshot saved for studio {StudioId}", snapshot.StudioId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to save PlaybackSession snapshot");
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }

        _logger.LogInformation("PlaybackSessionSnapshotService stopped.");
    }
}
