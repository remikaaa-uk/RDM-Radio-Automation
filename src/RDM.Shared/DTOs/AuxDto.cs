namespace RDM.Shared.DTOs;

/// Request to load an arbitrary audio file into an AUX deck.
public record AuxLoadRequestDto(string FilePath);

/// Result of an AUX load: duration, generated waveform cache path and auto-detected cues.
public record AuxLoadedDto(
    uint   DurationMs,
    string WaveformPath,
    uint   StartMs,
    uint   EndMs);

public record AuxVolumeRequestDto(float Gain);   // 0.0 – 1.0 linear

public record AuxLoopRequestDto(bool Enabled);

public record AuxRouteRequestDto(bool On, bool Pfl);
