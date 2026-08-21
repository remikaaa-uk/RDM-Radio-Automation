using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Core.Queues;

namespace RDM.Infrastructure;

/// <summary>
/// Background service that drains WaveformQueue, generates compressed waveform data,
/// saves it to the database via IWaveformRepository, and publishes WaveformReadyEvent.
/// One failure never blocks the queue — exceptions are caught and logged per task.
/// </summary>
public sealed class WaveformQueueService : BackgroundService
{
    private readonly WaveformQueue                  _queue;
    private readonly IWaveformGenerator             _generator;
    private readonly IWaveformRepository            _waveformRepository;
    private readonly IEventBus                      _eventBus;
    private readonly ILogger<WaveformQueueService>  _logger;

    public WaveformQueueService(
        WaveformQueue                 queue,
        IWaveformGenerator            generator,
        IWaveformRepository           waveformRepository,
        IEventBus                     eventBus,
        ILogger<WaveformQueueService> logger)
    {
        _queue              = queue;
        _generator          = generator;
        _waveformRepository = waveformRepository;
        _eventBus           = eventBus;
        _logger             = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WaveformQueueService started.");
        try
        {
            await foreach (var task in _queue.Reader.ReadAllAsync(stoppingToken))
                await ProcessTaskAsync(task, stoppingToken);
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        _logger.LogInformation("WaveformQueueService stopped.");
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private async Task ProcessTaskAsync(WaveformTask task, CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Generating waveform for asset {AssetId}", task.AssetId);

            byte[] waveData = await _generator.GenerateAsync(task.FilePath, ct);
            await _waveformRepository.SaveAsync(task.AssetId, waveData, WaveformConstants.NPoints, ct);

            _logger.LogDebug("Waveform saved for asset {AssetId}", task.AssetId);
            await _eventBus.PublishAsync(new WaveformReadyEvent(task.AssetId), ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Waveform generation failed for asset {AssetId}", task.AssetId);
        }
    }
}
