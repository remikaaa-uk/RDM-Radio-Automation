namespace RDM.Core.Interfaces;

public interface IMidiLearnScanner
{
    Task StartScanAsync();
    void StopScan();
}
