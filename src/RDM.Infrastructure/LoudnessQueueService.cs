using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Core.Queues;

namespace RDM.Infrastructure;

/// <summary>
/// Background service that drains LoudnessQueue and runs EBU R128 analysis via ILoudnessAnalyzer.
/// One failure never blocks the queue — exceptions are caught and logged per task.
/// R3: if UpdateLoudnessAsync returns 0 affected rows (asset deleted while processing),
/// no action is taken — loudness analysis produces no on-disk artefact to clean up.
/// </summary>
public sealed class LoudnessQueueService : BackgroundService
{
    private readonly LoudnessQueue                   _queue;
    private readonly ILoudnessAnalyzer               _analyzer;
    private readonly IAssetRepository                _assetRepository;
    private readonly IEventBus                       _eventBus;
    private readonly ILogger<LoudnessQueueService>   _logger;

    public LoudnessQueueService(
        LoudnessQueue                  queue,
        ILoudnessAnalyzer              analyzer,
        IAssetRepository               assetRepository,
        IEventBus                      eventBus,
        ILogger<LoudnessQueueService>  logger)
    {
        _queue           = queue;
        _analyzer        = analyzer;
        _assetRepository = assetRepository;
        _eventBus        = eventBus;
        _logger          = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LoudnessQueueService started.");

        try
        {
            await foreach (var task in _queue.Reader.ReadAllAsync(stoppingToken))
                await ProcessTaskAsync(task, stoppingToken);
        }
        catch (OperationCanceledException) { /* normal shutdown */ }

        _logger.LogInformation("LoudnessQueueService stopped.");
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private async Task ProcessTaskAsync(LoudnessTask task, CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Analysing loudness for asset {AssetId}", task.AssetId);

            var result = await _analyzer.AnalyzeAsync(task.FilePath, ct);

            int rows = await _assetRepository.UpdateLoudnessAsync(
                task.AssetId, result.LufsIntegrated, result.TruePeak, ct);

            if (rows > 0)
            {
                _logger.LogDebug(
                    "Loudness stored for asset {AssetId}: {Lufs} LUFS, {Peak} dBTP",
                    task.AssetId, result.LufsIntegrated, result.TruePeak);

                await _eventBus.PublishAsync(new LoudnessAnalyzedEvent(
                    task.AssetId, result.LufsIntegrated, result.TruePeak), ct);
            }
            else
            {
                // R3 — asset deleted while loudness analysis was in progress; silent no-op
                _logger.LogDebug(
                    "Asset {AssetId} no longer exists — loudness result discarded.", task.AssetId);
            }
        }
        catch (OperationCanceledException) { throw; } // propagate to stop the drain loop
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loudness analysis failed for asset {AssetId}", task.AssetId);
            // Intentionally not re-thrown — one failure must not block the queue
        }
    }
}
