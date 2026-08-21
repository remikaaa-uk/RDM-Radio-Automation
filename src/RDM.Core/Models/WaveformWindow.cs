namespace RDM.Core.Models;

/// <summary>
/// A high-resolution waveform slice decoded on demand for a specific source-time
/// window, used by the segue editor when the user zooms in past the precomputed
/// 1000-point peaks' resolution.
/// <para>
/// <see cref="MinMax"/> is interleaved <c>[min0, max0, min1, max1, …]</c> with one
/// pair per column. When <see cref="SampleLevel"/> is <c>true</c> there is roughly
/// one audio sample per column (min == max == the sample value), so the renderer
/// connects the values into a continuous line (Audacity-style) instead of drawing
/// min/max stems. <see cref="StartMs"/>/<see cref="EndMs"/> are source-relative
/// milliseconds the slice covers.
/// </para>
/// </summary>
public sealed record WaveformWindow(
    double  StartMs,
    double  EndMs,
    float[] MinMax,
    bool    SampleLevel);
