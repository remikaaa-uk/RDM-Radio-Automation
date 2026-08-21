using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Core.Models;

namespace RDM.Infrastructure.FileSystem;

/// <summary>
/// Monitors a folder for new audio files and feeds them to ImportPipeline.
/// Never writes to the database directly — all persistence is delegated to IImportPipeline.
/// </summary>
public sealed class FolderWatchService : BackgroundService
{
    private readonly IImportPipeline          _pipeline;
    private readonly FolderWatchSettings      _settings;
    private readonly ILogger<FolderWatchService> _logger;

    // Captured in ExecuteAsync so FSW event handlers can use the service lifetime token.
    private CancellationToken _stoppingToken;

    // Backoff delays between retries when the file is still being copied (P2).
    private static readonly int[] RetryDelaysMs = [500, 1000, 2000];

    public FolderWatchService(
        IImportPipeline             pipeline,
        FolderWatchSettings         settings,
        ILogger<FolderWatchService> logger)
    {
        _pipeline = pipeline;
        _settings = settings;
        _logger   = logger;
    }

    // ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        Directory.CreateDirectory(_settings.WatchPath);

        _logger.LogInformation("FolderWatch starting. Path: {Path}", _settings.WatchPath);

        // P5 — initial scan: import files that arrived while the service was offline
        await InitialScanAsync(stoppingToken);

        using var watcher = BuildWatcher();
        watcher.EnableRaisingEvents = true;

        // Hold the service alive until the host signals shutdown
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stoppingToken.Register(() => shutdown.TrySetResult());
        await shutdown.Task;

        watcher.EnableRaisingEvents = false;
        _logger.LogInformation("FolderWatch stopped.");
    }

    // ── FileSystemWatcher setup ───────────────────────────────────────────────

    private FileSystemWatcher BuildWatcher()
    {
        var w = new FileSystemWatcher(_settings.WatchPath)
        {
            Filter                = "*.*",
            IncludeSubdirectories = true,
            NotifyFilter          = NotifyFilters.FileName,
            InternalBufferSize    = 64 * 1024  // P5 — reduce event loss under heavy load
        };

        w.Created += OnFileCreated;
        w.Renamed += OnFileRenamed;
        w.Error   += OnError;

        return w;
    }

    // ── FSW event handlers (called on FSW internal thread) ────────────────────

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (!IsSupportedExtension(e.FullPath)) return;
        _ = ProcessFileAsync(e.FullPath, _stoppingToken);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsSupportedExtension(e.FullPath)) return;
        _ = ProcessFileAsync(e.FullPath, _stoppingToken);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // P5 — FSW internal buffer overflowed; some events were lost → rescan
        _logger.LogWarning(e.GetException(),
            "FolderWatch buffer overflow — triggering rescan of {Path}", _settings.WatchPath);
        _ = InitialScanAsync(_stoppingToken);
    }

    // ── Core import logic ─────────────────────────────────────────────────────

    /// <summary>
    /// Enumerates all supported files in the watch folder and imports those not yet in the library.
    /// ImportPipeline handles duplicates gracefully (returns Duplicate result), so rescanning
    /// already-imported files is safe.
    /// </summary>
    private async Task InitialScanAsync(CancellationToken ct)
    {
        _logger.LogInformation("InitialScan started: {Path}", _settings.WatchPath);
        int imported = 0, skipped = 0;

        foreach (string file in EnumerateSupportedFiles())
        {
            if (ct.IsCancellationRequested) break;

            var result = await TryImportWithRetryAsync(file, ct);
            if (result is ImportResult.Success) imported++;
            else skipped++;
        }

        _logger.LogInformation(
            "InitialScan complete: {Imported} imported, {Skipped} skipped/duplicate.", imported, skipped);
    }

    /// <summary>
    /// Processes a single file — called from FSW event handlers (fire-and-forget).
    /// </summary>
    private async Task ProcessFileAsync(string filePath, CancellationToken ct)
    {
        var result = await TryImportWithRetryAsync(filePath, ct);
        LogResult(result, filePath);
    }

    /// <summary>
    /// Calls ImportPipeline with exponential backoff on IOException (P2 — partial file copy).
    /// Returns null on cancellation or after all retries exhausted.
    /// </summary>
    private async Task<ImportResult?> TryImportWithRetryAsync(string filePath, CancellationToken ct)
    {
        for (int attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
        {
            try
            {
                return await _pipeline.ImportAsync(filePath, null, null, null, ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (IOException) when (attempt < RetryDelaysMs.Length)
            {
                _logger.LogDebug(
                    "File not ready (attempt {Attempt}/{Max}), retrying in {Delay}ms: {File}",
                    attempt + 1, RetryDelaysMs.Length + 1, RetryDelaysMs[attempt], filePath);

                await Task.Delay(RetryDelaysMs[attempt], ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Import failed for {FilePath}", filePath);
                return null;
            }
        }

        // All retries exhausted — file was never readable
        _logger.LogWarning("Import abandoned after {Max} attempts: {FilePath}",
            RetryDelaysMs.Length + 1, filePath);
        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void LogResult(ImportResult? result, string filePath)
    {
        switch (result)
        {
            case ImportResult.Success s:
                _logger.LogInformation(
                    "Imported: {Title} ({FilePath})", s.Asset.Title, filePath);
                break;
            case ImportResult.Duplicate d:
                _logger.LogDebug(
                    "Duplicate skipped: {FilePath} (assetId: {AssetId})", filePath, d.AssetId);
                break;
            case ImportResult.Failed f:
                _logger.LogWarning(
                    "Import returned failed: {FilePath} — {Reason}", filePath, f.Reason);
                break;
            // null = cancelled or exception already logged
        }
    }

    private IEnumerable<string> EnumerateSupportedFiles()
    {
        try
        {
            return Directory.EnumerateFiles(_settings.WatchPath, "*.*", SearchOption.AllDirectories)
                .Where(IsSupportedExtension);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot enumerate {Path}", _settings.WatchPath);
            return [];
        }
    }

    private bool IsSupportedExtension(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return _settings.SupportedExtensions.Contains(ext);
    }
}
