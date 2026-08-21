using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Core.Queues;

namespace RDM.Infrastructure;

/// <summary>
/// Runs once on startup and re-queues waveform generation for any active asset
/// that has no row in asset_waveforms. Uses a single query to find missing IDs
/// rather than checking each asset individually.
/// </summary>
public sealed class WaveformRescanService : BackgroundService
{
    private readonly IServiceScopeFactory           _scopeFactory;
    private readonly WaveformQueue                  _queue;
    private readonly ILogger<WaveformRescanService> _logger;

    public WaveformRescanService(
        IServiceScopeFactory           scopeFactory,
        WaveformQueue                  queue,
        ILogger<WaveformRescanService> logger)
    {
        _scopeFactory = scopeFactory;
        _queue        = queue;
        _logger       = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WaveformRescanService: starting scan");

        int queued = 0;
        try
        {
            using var scope      = _scopeFactory.CreateScope();
            var assetRepo        = scope.ServiceProvider.GetRequiredService<IAssetRepository>();
            var waveformRepo     = scope.ServiceProvider.GetRequiredService<IWaveformRepository>();

            var assets      = await assetRepo.GetAllForWaveformScanAsync(stoppingToken);
            var existingIds = await waveformRepo.GetExistingAssetIdsAsync(stoppingToken);

            _logger.LogInformation(
                "WaveformRescanService: {Total} active assets, {Existing} already have waveform",
                assets.Count, existingIds.Count);

            foreach (var a in assets)
            {
                if (string.IsNullOrEmpty(a.FilePath)) continue;
                if (existingIds.Contains(a.AssetId))  continue;

                await _queue.Writer.WriteAsync(new WaveformTask(a.AssetId, a.FilePath), stoppingToken);
                queued++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WaveformRescanService: scan failed");
            return;
        }

        _logger.LogInformation("WaveformRescanService: queued {Count} waveform tasks", queued);
    }
}
