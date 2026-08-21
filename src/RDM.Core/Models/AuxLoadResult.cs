namespace RDM.Core.Models;

/// <summary>
/// Result of loading an arbitrary audio file into an AUX player.
/// WaveformPath points to a generated .wvf peak cache; StartMs/EndMs are the
/// auto-detected cue points (from the Audio DJ silence thresholds).
/// </summary>
public readonly record struct AuxLoadResult(
    uint   DurationMs,
    string WaveformPath,
    uint   StartMs,
    uint   EndMs);
