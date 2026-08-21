using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RDM.Core.Interfaces;
using RDM.Core.Services;

namespace RDM.Infrastructure;

/// <summary>
/// Triggers BackupService according to audio_settings.backup_interval_h.
/// On startup checks whether a backup is overdue and runs it immediately if so.
/// Settings are re-read on every cycle to pick up interval changes without restart.
/// </summary>
public sealed class BackupSchedulerService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly IBackupService                    _backupService;
    private readonly IAudioSettingsRepository          _settingsRepo;
    private readonly StudioContext                     _studioContext;
    private readonly ILogger<BackupSchedulerService>   _logger;

    public BackupSchedulerService(
        IBackupService                   backupService,
        IAudioSettingsRepository         settingsRepo,
        StudioContext                    studioContext,
        ILogger<BackupSchedulerService>  logger)
    {
        _backupService = backupService;
        _settingsRepo  = settingsRepo;
        _studioContext = studioContext;
        _logger        = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackupSchedulerService started.");

        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            // Check immediately on startup (covers overdue backups after unclean shutdown)
            await TryBackupIfDueAsync(stoppingToken);

            while (await timer.WaitForNextTickAsync(stoppingToken))
                await TryBackupIfDueAsync(stoppingToken);
        }
        catch (OperationCanceledException) { /* normal shutdown */ }

        _logger.LogInformation("BackupSchedulerService stopped.");
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private async Task TryBackupIfDueAsync(CancellationToken ct)
    {
        try
        {
            var settings = await _settingsRepo.GetByStudioAsync(_studioContext.StudioId, ct);

            if (settings is null || settings.BackupIntervalH == 0)
                return;

            var interval = TimeSpan.FromHours(settings.BackupIntervalH);
            var nextDue  = settings.BackupLastAt.HasValue
                ? settings.BackupLastAt.Value + interval
                : DateTime.MinValue; // never backed up → overdue immediately

            if (DateTime.UtcNow < nextDue)
                return;

            _logger.LogInformation(
                "Scheduled backup due (interval {H}h, last at {Last})",
                settings.BackupIntervalH,
                settings.BackupLastAt?.ToString("yyyy-MM-dd HH:mm") ?? "never");

            await _backupService.BackupNowAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BackupSchedulerService: scheduled backup failed");
        }
    }
}
