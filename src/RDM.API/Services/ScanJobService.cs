using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Infrastructure.FileSystem;
using RDM.Shared.DTOs;

namespace RDM.API.Services;

public sealed class ScanJob
{
    public required string ScanId { get; init; }
    public required IReadOnlyList<string> FilePaths { get; init; }
    public string Status { get; set; } = "QUEUED"; // QUEUED, PROCESSING, COMPLETED, FAILED
    public int Done { get; set; }
    public int Total { get; init; }
    public DateTime? CompletedAt { get; set; }
    public List<NewTrackDto> Results { get; } = new();
}

/// <summary>
/// Background scanner for the Update Tracks feature. Given a list of file paths,
/// it reports which files are new to the library — i.e. exist neither by path nor
/// by SHA-256 checksum. Mirrors <see cref="ImportJobService"/> in structure.
/// A single failing file never aborts the whole scan.
/// </summary>
public sealed class ScanJobService : BackgroundService
{
    private readonly IServiceProvider          _serviceProvider;
    private readonly IChecksumService          _checksumService;
    private readonly ILogger<ScanJobService>   _logger;
    private readonly ConcurrentDictionary<string, ScanJob> _jobs = new();

    private readonly Channel<ScanJob> _queue = Channel.CreateBounded<ScanJob>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });

    public ScanJobService(
        IServiceProvider        serviceProvider,
        IChecksumService        checksumService,
        ILogger<ScanJobService> logger)
    {
        _serviceProvider = serviceProvider;
        _checksumService = checksumService;
        _logger          = logger;
    }

    public string Enqueue(IReadOnlyList<string> filePaths)
    {
        var id  = Guid.NewGuid().ToString();
        var job = new ScanJob { ScanId = id, FilePaths = filePaths, Total = filePaths.Count };
        _jobs[id] = job;
        if (!_queue.Writer.TryWrite(job))
        {
            job.Status      = "FAILED";
            job.CompletedAt = DateTime.UtcNow;
        }
        return id;
    }

    public ScanJob? GetJob(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.WhenAll(DrainLoopAsync(ct), CleanupLoopAsync(ct));
    }

    private async Task DrainLoopAsync(CancellationToken ct)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(ct))
        {
            job.Status = "PROCESSING";

            using var scope = _serviceProvider.CreateScope();
            var assetRepository = scope.ServiceProvider.GetRequiredService<IAssetRepository>();
            var id3 = scope.ServiceProvider.GetServices<IMetadataReader>().OfType<Id3TagReader>().First();

            try
            {
                foreach (var path in job.FilePaths)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var newTrack = await EvaluateFileAsync(path, assetRepository, id3, ct);
                        if (newTrack is not null)
                            job.Results.Add(newTrack);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // A single unreadable/inaccessible file must not abort the scan.
                        _logger.LogWarning(ex, "Scan skipped file due to error: {FilePath}", path);
                    }
                    finally
                    {
                        job.Done++;
                    }
                }

                job.Status      = "COMPLETED";
                job.CompletedAt = DateTime.UtcNow;
            }
            catch (OperationCanceledException)
            {
                job.Status      = "FAILED";
                job.CompletedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception during scan {ScanId}", job.ScanId);
                job.Status      = "FAILED";
                job.CompletedAt = DateTime.UtcNow;
            }
        }
    }

    /// Returns a <see cref="NewTrackDto"/> when the file is new to the library
    /// (not found by path and not found by checksum); otherwise null.
    private async Task<NewTrackDto?> EvaluateFileAsync(
        string path, IAssetRepository assetRepository, Id3TagReader id3, CancellationToken ct)
    {
        // 1. By path — fast reject for files already tracked at this exact location.
        var byPath = await assetRepository.FindByFilePathAsync(path, ct);
        if (byPath is not null) return null;

        // 2. By checksum (SHA-256 fingerprint) — catches moved/renamed files.
        string checksum = await _checksumService.ComputeAsync(path, ct);
        var byChecksum = await assetRepository.GetByChecksumAsync(checksum, ct);
        if (byChecksum is not null) return null;

        // 3. New file — read metadata for display only.
        string? title = null, artist = null;
        int? durationMs = null;
        try
        {
            var meta = await id3.TryReadAsync(path, ct);
            if (meta is not null)
            {
                title      = meta.Title;
                artist     = meta.Artist;
                durationMs = meta.DurationMs.HasValue ? (int)meta.DurationMs.Value : null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scan could not read metadata for new file: {FilePath}", path);
        }

        return new NewTrackDto(
            FilePath:   path,
            Filename:   Path.GetFileName(path),
            Artist:     artist,
            Title:      title ?? Path.GetFileNameWithoutExtension(path),
            DurationMs: durationMs,
            Folder:     Path.GetDirectoryName(path));
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-30);
            foreach (var (id, job) in _jobs)
            {
                if ((job.Status == "COMPLETED" || job.Status == "FAILED")
                    && job.CompletedAt < cutoff)
                {
                    _jobs.TryRemove(id, out _);
                }
            }
        }
    }
}
