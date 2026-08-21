using System.Collections.Generic;

namespace RDM.Core.Models;

/// <summary>One editable parameter of a built-in BFX effect.</summary>
/// <param name="Key">Stable identifier, used in the API, the config file and the i18n keys.</param>
/// <param name="Unit">Free-form unit shown next to the value ("dB", "ms", "Hz", ":1", "").</param>
public sealed record MicFxParam(string Key, float Min, float Max, float Default, string Unit);

/// <summary>
/// The parameters each BFX effect exposes, with the ranges the UI offers and the values a new
/// slot starts with. Single source of truth: the engine seeds and clamps against this, the API
/// validates against it and the UI builds its editor from it, so adding a parameter is one edit.
/// </summary>
public static class MicFxParams
{
    private static readonly IReadOnlyList<MicFxParam> Compressor =
    [
        new("threshold", -60f,   0f, -12f,  "dB"),
        new("ratio",       1f, 100f,   4f,  ":1"),
        new("attack",   0.01f, 200f,   1f,  "ms"),
        new("release",  0.01f, 1000f, 200f, "ms"),
        new("gain",      -60f,  60f,   0f,  "dB")
    ];

    private static readonly IReadOnlyList<MicFxParam> PeakEq =
    [
        new("center",     20f, 20000f, 1000f, "Hz"),
        new("bandwidth", 0.1f,     6f,    1f, "oct"),
        // Default deliberately non-zero: at 0 dB the effect is audibly a no-op, which used to
        // make a freshly added EQ look broken.
        new("gain",      -15f,    15f,    3f, "dB")
    ];

    private static readonly IReadOnlyList<MicFxParam> Volume =
    [
        // 1.0 = unity. Starts slightly above it for the same reason as the EQ gain above.
        new("volume", 0f, 5f, 1.2f, "x")
    ];

    private static readonly IReadOnlyList<MicFxParam> FreeVerb =
    [
        new("drymix",   0f, 1f, 0.9f, ""),
        new("wetmix",   0f, 3f, 0.1f, ""),
        new("roomsize", 0f, 1f, 0.5f, ""),
        new("damp",     0f, 1f, 0.5f, ""),
        new("width",    0f, 1f, 1.0f, "")
    ];

    public static IReadOnlyList<MicFxParam> For(MicFxType fxType) => fxType switch
    {
        MicFxType.Compressor => Compressor,
        MicFxType.PeakEq     => PeakEq,
        MicFxType.VolumeGain => Volume,
        MicFxType.FreeVerb   => FreeVerb,
        _                    => []
    };

    /// <summary>The starting values for a newly added slot.</summary>
    public static Dictionary<string, float> Defaults(MicFxType fxType)
    {
        var result = new Dictionary<string, float>();
        foreach (var p in For(fxType))
            result[p.Key] = p.Default;
        return result;
    }

    /// <summary>
    /// Keeps only known keys and clamps them into range. Anything the caller sends that the
    /// effect does not have is dropped rather than passed on to BASS.
    /// </summary>
    public static Dictionary<string, float> Sanitize(MicFxType fxType, IReadOnlyDictionary<string, float> values)
    {
        var result = Defaults(fxType);
        foreach (var p in For(fxType))
        {
            if (!values.TryGetValue(p.Key, out float v)) continue;
            if (float.IsNaN(v) || float.IsInfinity(v)) continue;
            result[p.Key] = v < p.Min ? p.Min : v > p.Max ? p.Max : v;
        }
        return result;
    }
}
