namespace RDM.Core.Interfaces;

public interface IBpmAnalyzer
{
    Task<decimal?> AnalyzeAsync(string filePath, CancellationToken ct = default);
}
