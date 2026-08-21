using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Core.Services;

namespace RDM.Infrastructure;

/// <summary>
/// Runs mysqldump to create a timestamped SQL backup of the RDM database.
/// Backup path and retention are read from audio_settings per studio.
/// Called by BackupSchedulerService on an interval and by UI on shutdown.
/// </summary>
public sealed class BackupService : IBackupService
{
    private readonly IAudioSettingsRepository _settingsRepo;
    private readonly StudioContext            _studioContext;
    private readonly IConfiguration           _configuration;
    private readonly ILogger<BackupService>   _logger;

    public BackupService(
        IAudioSettingsRepository settingsRepo,
        StudioContext            studioContext,
        IConfiguration          configuration,
        ILogger<BackupService>  logger)
    {
        _settingsRepo  = settingsRepo;
        _studioContext = studioContext;
        _configuration = configuration;
        _logger        = logger;
    }

    public async Task BackupNowAsync(CancellationToken ct = default)
    {
        var settings = await _settingsRepo.GetByStudioAsync(_studioContext.StudioId, ct);

        if (settings is null)
        {
            _logger.LogWarning("BackupService: audio_settings not found for studio {StudioId}", _studioContext.StudioId);
            return;
        }

        if (string.IsNullOrWhiteSpace(settings.BackupPath))
        {
            _logger.LogWarning("BackupService: backup_path is not configured — skipping backup");
            return;
        }

        var backupDir = Environment.ExpandEnvironmentVariables(settings.BackupPath);
        Directory.CreateDirectory(backupDir);

        var timestamp  = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var outputFile = Path.Combine(backupDir, $"rdm_backup_{timestamp}.sql");

        await RunDumpAsync(outputFile, ct);

        await _settingsRepo.UpdateBackupLastAtAsync(settings.SettingsId, DateTime.UtcNow, ct);
        _logger.LogInformation("Backup completed: {File}", outputFile);

        RotateBackups(backupDir, settings.BackupKeepCount);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task RunDumpAsync(string outputFile, CancellationToken ct)
    {
        var host     = _configuration["database:host"]     ?? "localhost";
        var port     = _configuration["database:port"]     ?? "3306";
        var dbName   = _configuration["database:name"]     ?? throw new InvalidOperationException("database:name is not configured.");
        var username = _configuration["database:username"] ?? throw new InvalidOperationException("database:username is not configured.");
        var password = _configuration["database:password"] ?? throw new InvalidOperationException("database:password is not configured.");
        var dumpTool = _configuration["database:dump_tool_path"] ?? "mysqldump";

        var args = $"--host={host} --port={port} --user={username} --result-file=\"{outputFile}\" --single-transaction --routines {dbName}";

        var psi = new ProcessStartInfo(dumpTool, args)
        {
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            Environment            = { ["MYSQL_PWD"] = password }
        };

        _logger.LogDebug("Running: {Tool} {Args}", dumpTool, args.Replace(password, "***"));

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {dumpTool}");

        string stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{dumpTool} exited with code {process.ExitCode}: {stderr}");
    }

    private void RotateBackups(string backupDir, byte keepCount)
    {
        if (keepCount == 0) return;

        var files = Directory.GetFiles(backupDir, "rdm_backup_*.sql")
            .OrderByDescending(f => f)
            .Skip(keepCount)
            .ToList();

        foreach (var old in files)
        {
            try
            {
                File.Delete(old);
                _logger.LogDebug("Rotated old backup: {File}", old);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not delete old backup: {File}", old);
            }
        }
    }
}
