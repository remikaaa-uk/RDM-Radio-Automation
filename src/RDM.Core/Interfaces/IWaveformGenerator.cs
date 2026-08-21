namespace RDM.Core.Interfaces;

public interface IWaveformGenerator
{
    Task<byte[]> GenerateAsync(string audioFilePath, CancellationToken ct = default);
}
