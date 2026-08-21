using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Core.Queues;

namespace RDM.Infrastructure;

public sealed class BpmQueueService : BackgroundService
{
    private readonly BpmQueue                   _queue;
    private readonly IBpmAnalyzer               _analyzer;
    private readonly IAssetRepository           _assetRepository;
    private readonly ILogger<BpmQueueService>   _logger;

    public BpmQueueService(
        BpmQueue                  queue,
        IBpmAnalyzer              analyzer,
        IAssetRepository          assetRepository,
        ILogger<BpmQueueService>  logger)
    {
        _queue           = queue;
        _analyzer        = analyzer;
        _assetRepository = assetRepository;
        _logger          = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BpmQueueService started.");
        try
        {
            await foreach (var task in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    _logger.LogDebug("Analysing BPM for asset {AssetId}", task.AssetId);
                    var bpm = await _analyzer.AnalyzeAsync(task.FilePath, stoppingToken);
                    if (bpm.HasValue)
                    {
                        int rows = await _assetRepository.UpdateBpmAsync(task.AssetId, bpm.Value, stoppingToken);
                        if (rows > 0)
                            _logger.LogDebug("BPM stored for asset {AssetId}: {Bpm}", task.AssetId, bpm.Value);
                    }
                    else
                    {
                        _logger.LogDebug("BPM analysis returned no result for asset {AssetId}", task.AssetId);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "BPM analysis failed for asset {AssetId}", task.AssetId);
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        _logger.LogInformation("BpmQueueService stopped.");
    }
}
