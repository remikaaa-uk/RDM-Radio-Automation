using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Core.Models;
using RDM.Core.Queues;
using RDM.Shared.DTOs;

namespace RDM.API.Services;

public record ImportJobStatus(
    string ImportId,
    string FilePath,
    string Status, // "QUEUED", "PROCESSING", "COMPLETED", "FAILED"
    bool IsDuplicate = false,
    string? AssetId = null,
    string? Title = null,
    string? Artist = null,
    DateTime? CompletedAt = null
);

public record ImportJobEntry(
    string ImportId,
    string FilePath,
    ImportReaderFlags Flags,
    string? FormatId = null,
    string? SubcategoryId = null
);

public sealed class ImportJobService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly BpmQueue         _bpmQueue;
    private readonly ILogger<ImportJobService> _logger;
    private readonly ConcurrentDictionary<string, ImportJobStatus> _jobs = new();

    private readonly Channel<ImportJobEntry> _queue = Channel.CreateBounded<ImportJobEntry>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    public ImportJobService(IServiceProvider serviceProvider, BpmQueue bpmQueue, ILogger<ImportJobService> logger)
    {
        _serviceProvider = serviceProvider;
        _bpmQueue        = bpmQueue;
        _logger          = logger;
    }

    public string Enqueue(string filePath, ImportReaderFlags? flags = null, string? formatId = null, string? subcategoryId = null)
    {
        var id = Guid.NewGuid().ToString();
        _jobs[id] = new ImportJobStatus(id, filePath, "QUEUED");
        if (!_queue.Writer.TryWrite(new ImportJobEntry(id, filePath, flags ?? new ImportReaderFlags(), formatId, subcategoryId)))
            _jobs[id] = _jobs[id] with { Status = "FAILED", CompletedAt = DateTime.UtcNow };
        return id;
    }

    public ImportJobStatus? GetStatus(string id)
    {
        return _jobs.TryGetValue(id, out var status) ? status : null;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.WhenAll(DrainLoopAsync(ct), CleanupLoopAsync(ct));
    }

    private async Task DrainLoopAsync(CancellationToken ct)
    {
        await foreach (var entry in _queue.Reader.ReadAllAsync(ct))
        {
            _jobs[entry.ImportId] = _jobs[entry.ImportId] with { Status = "PROCESSING" };

            using var scope = _serviceProvider.CreateScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<IImportPipeline>();
            var assetRepository = scope.ServiceProvider.GetRequiredService<IAssetRepository>();

            try
            {
                var result = await pipeline.ImportAsync(entry.FilePath, entry.Flags, entry.FormatId, entry.SubcategoryId, ct);
                
                if (result is ImportResult.Success success)
                {
                    _bpmQueue.Writer.TryWrite(new BpmTask(success.Asset.AssetId, entry.FilePath));

                    _jobs[entry.ImportId] = _jobs[entry.ImportId] with
                    {
                        Status = "COMPLETED",
                        AssetId = success.Asset.AssetId,
                        Title = success.Asset.Title,
                        Artist = success.Asset.Artist,
                        CompletedAt = DateTime.UtcNow
                    };
                }
                else if (result is ImportResult.Duplicate duplicate)
                {
                    string? title = null;
                    string? artist = null;
                    try
                    {
                        var existing = await assetRepository.GetByIdAsync(duplicate.AssetId, ct);
                        title = existing?.Title;
                        artist = existing?.Artist;
                    }
                    catch
                    {
                        // Ignore lookup errors
                    }

                    _jobs[entry.ImportId] = _jobs[entry.ImportId] with
                    {
                        Status = "COMPLETED",
                        IsDuplicate = true,
                        AssetId = duplicate.AssetId,
                        Title = title,
                        Artist = artist,
                        CompletedAt = DateTime.UtcNow
                    };
                }
                else
                {
                    _jobs[entry.ImportId] = _jobs[entry.ImportId] with
                    {
                        Status = "FAILED",
                        CompletedAt = DateTime.UtcNow
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception during import of: {FilePath}", entry.FilePath);
                _jobs[entry.ImportId] = _jobs[entry.ImportId] with
                {
                    Status = "FAILED",
                    CompletedAt = DateTime.UtcNow
                };
            }
        }
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-30);
            foreach (var (id, status) in _jobs)
            {
                if ((status.Status == "COMPLETED" || status.Status == "FAILED")
                    && status.CompletedAt < cutoff)
                {
                    _jobs.TryRemove(id, out _);
                }
            }
        }
    }
}
