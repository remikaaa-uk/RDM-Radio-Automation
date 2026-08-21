namespace RDM.Core.Interfaces;

public interface IBackupService
{
    Task BackupNowAsync(CancellationToken ct = default);
}
