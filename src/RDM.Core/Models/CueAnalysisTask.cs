namespace RDM.Core.Models;

public sealed record CueAnalysisTask(
    string AssetId,
    string FilePath,
    double StartDb,
    double NextStartDb,
    double EndDb);
