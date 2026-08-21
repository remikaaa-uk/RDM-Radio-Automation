using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Core.Queues;
using RDM.Shared.DTOs;

namespace RDM.Infrastructure;

public sealed class CueAnalysisQueueService : BackgroundService
{
    private readonly CueAnalysisQueue                   _queue;
    private readonly IAudioEngine                       _audioEngine;
    private readonly IAssetRepository                   _assetRepository;
    private readonly ILogger<CueAnalysisQueueService>   _logger;

    public CueAnalysisQueueService(
        CueAnalysisQueue                  queue,
        IAudioEngine                      audioEngine,
        IAssetRepository                  assetRepository,
        ILogger<CueAnalysisQueueService>  logger)
    {
        _queue           = queue;
        _audioEngine     = audioEngine;
        _assetRepository = assetRepository;
        _logger          = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CueAnalysisQueueService started.");
        try
        {
            await foreach (var task in _queue.Reader.ReadAllAsync(stoppingToken))
                await ProcessTaskAsync(task, stoppingToken);
        }
        catch (OperationCanceledException) { }
        _logger.LogInformation("CueAnalysisQueueService stopped.");
    }

    private async Task ProcessTaskAsync(Core.Models.CueAnalysisTask task, CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Analysing cue points for asset {AssetId}", task.AssetId);

            var result = await _audioEngine.AnalyzeCuePointsAsync(
                task.FilePath, task.StartDb, task.NextStartDb, task.EndDb, ct);

            if (result is null)
            {
                _logger.LogDebug("Cue analysis returned no result for {AssetId}", task.AssetId);
                return;
            }

            var (start, nextStart, end, durationSec) = result.Value;

            // Merge with existing markers — only overwrite START, NEXT_START, END
            var asset = await _assetRepository.GetByIdAsync(task.AssetId, ct);
            if (asset is null) return;

            var existing = new CueMarkersDto(
                Start:     asset.CueStart,
                Intro:     asset.CueIntro,
                Ramp2:     asset.CueRamp2,
                Ramp3:     asset.CueRamp3,
                Outro:     asset.CueOutro,
                StartNext: asset.CueStartNext,
                FadeOut:   asset.CueFadeOut,
                FadeEnd:   asset.CueFadeEnd,
                End:       asset.CueEnd,
                HookIn:    asset.CueHookIn,
                HookFade:  asset.CueHookFade,
                HookOut:   asset.CueHookOut,
                LoopIn:    asset.CueLoopIn,
                LoopOut:   asset.CueLoopOut,
                Anchor:    asset.CueAnchor);

            var merged = existing with
            {
                Start     = start     ?? existing.Start,
                StartNext = nextStart ?? existing.StartNext,
                End       = end       ?? existing.End
            };

            await _assetRepository.UpdateCueMarkersAsync(task.AssetId, merged, ct);

            if (durationSec is > 0)
                await _assetRepository.UpdateDurationAsync(task.AssetId, (uint)(durationSec.Value * 1000), ct);

            _logger.LogDebug(
                "Cue markers saved for {AssetId}: Start={Start:F3}s NextStart={NextStart:F3}s End={End:F3}s",
                task.AssetId, start, nextStart, end);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cue analysis failed for asset {AssetId}", task.AssetId);
        }
    }
}
