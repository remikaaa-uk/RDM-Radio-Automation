namespace RDM.Core.Services;

/// <summary>
/// Pure AUTO cue detection from a waveform peak array.
/// Computes START / NEXT START (mix point) / END from dBFS thresholds:
///   START      = first sample at or above the Start threshold
///   NEXT START = last  sample at or above the Mix   threshold
///   END        = last  sample at or above the End   threshold
/// Thresholds are supplied by the caller (from the Silence Remover audio settings) —
/// there are no hardcoded defaults.
/// Peaks are amplitudes relative to full scale (1.0); the scan uses |value|, so the
/// generator's interleaved min/max pairs ([min0, max0, …], min ≤ 0 ≤ max) work as-is.
/// No audio-engine dependency, so it is unit-testable in isolation.
/// </summary>
public static class AutoCueCalculator
{
    /// <summary>
    /// Returns the detected cue positions in seconds, or <c>null</c> when there is no
    /// usable waveform / duration. NEXT START is clamped to not exceed END.
    /// </summary>
    public static AutoCueResult? Calculate(
        IReadOnlyList<float> peaks,
        double durationSec,
        double startThresholdDb,
        double mixThresholdDb,
        double endThresholdDb)
    {
        if (peaks is null || peaks.Count == 0 || durationSec <= 0) return null;

        float startTh = DbToLinear(startThresholdDb);
        float mixTh   = DbToLinear(mixThresholdDb);
        float endTh   = DbToLinear(endThresholdDb);

        int n = peaks.Count;

        // START — first crossing of the start threshold (scanning forward).
        int startIdx = 0;
        for (int i = 0; i < n; i++)
            if (Math.Abs(peaks[i]) >= startTh) { startIdx = i; break; }

        // END — last crossing of the (quiet) end threshold (scanning backward).
        int endIdx = n - 1;
        for (int i = n - 1; i >= 0; i--)
            if (Math.Abs(peaks[i]) >= endTh) { endIdx = i; break; }

        // NEXT START — last crossing of the (loud) mix threshold; the next track mixes in
        // here. Falls back to END when the track never reaches the mix threshold.
        int nextStartIdx = endIdx;
        for (int i = n - 1; i >= 0; i--)
            if (Math.Abs(peaks[i]) >= mixTh) { nextStartIdx = i; break; }

        double startSec     = startIdx * durationSec / n;
        double endSec       = endIdx   * durationSec / n;
        double nextStartSec = Math.Min(nextStartIdx * durationSec / n, endSec);

        return new AutoCueResult(startSec, nextStartSec, endSec);
    }

    /// <summary>Converts a dBFS value to a linear amplitude (1.0 = full scale).</summary>
    public static float DbToLinear(double db) => (float)Math.Pow(10.0, db / 20.0);
}

/// <summary>Result of <see cref="AutoCueCalculator.Calculate"/>; positions in seconds.</summary>
public readonly record struct AutoCueResult(double StartSec, double NextStartSec, double EndSec);
