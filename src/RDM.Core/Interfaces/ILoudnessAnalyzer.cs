using RDM.Core.Models;

namespace RDM.Core.Interfaces;

public interface ILoudnessAnalyzer
{
    Task<LoudnessResult> AnalyzeAsync(string audioFilePath, CancellationToken ct = default);
}
