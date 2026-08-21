using System.Text.Json;
using RDM.Core.Entities;
using RDM.Core.Models;
using RDM.Shared.Enums;

namespace RDM.Core.Services;

/// <summary>
/// Pure translation of an asset (+ its queue item and playlist mode) into the low-level
/// playback markers, volume envelope and display title the audio engine consumes.
///
/// Extracted from <see cref="PlaylistEngine"/> to isolate this stateless computation from the
/// engine's mutable, lock-guarded playback state. Behaviour is unchanged — callers pass the same
/// arguments the engine used to compute inline.
/// </summary>
public static class CuePointBuilder
{
    /// <param name="effectiveCrossfadeMs">
    /// Overlap with the *next* track (owned by the incoming item — see PlaylistEngine.TransitionCrossfadeMs).
    /// Drives the computed StartNext position when the asset has no explicit StartNext/FadeOut markers.
    /// </param>
    /// <param name="cueEndOverride">
    /// Live-detected End cue for variable-duration assets; takes precedence over <c>asset.CueEnd</c>
    /// when set. Null means "use the stored marker".
    /// </param>
    public static IReadOnlyList<AssetCuePoint> Build(
        Asset asset, PlaylistItem? item, PlaylistMode mode, uint effectiveCrossfadeMs,
        double? cueEndOverride = null)
    {
        var list = new List<AssetCuePoint>(5);
        double? cueEnd = cueEndOverride ?? asset.CueEnd;

        if (asset.CueIntro.HasValue)
            list.Add(MakeCue(asset, MarkerType.Intro, (uint)(asset.CueIntro.Value  * 1000)));
        if (asset.CueOutro.HasValue)
            list.Add(MakeCue(asset, MarkerType.Outro, (uint)(asset.CueOutro.Value  * 1000)));
        if (asset.CueHookIn.HasValue)
            list.Add(MakeCue(asset, MarkerType.Hook,  (uint)(asset.CueHookIn.Value * 1000)));

        // CueEnd: hard stop in every mode — registered before the Auto-only guard.
        if (cueEnd.HasValue)
            list.Add(MakeCue(asset, MarkerType.End, (uint)(cueEnd.Value * 1000)));

        // Automation markers only in Auto playlist mode
        if (item is null || mode != PlaylistMode.Auto)
            return list;

        // Effective broadcast end: CueEnd if set, otherwise physical DurationMs
        uint effectiveEndMs = cueEnd.HasValue ? (uint)(cueEnd.Value * 1000) : asset.DurationMs;

        if (item.SegueType == SegueType.Timed)
        {
            if (effectiveEndMs > effectiveCrossfadeMs)
                list.Add(MakeCue(asset, MarkerType.StartNext, effectiveEndMs - effectiveCrossfadeMs));
        }
        else if (item.SegueType == SegueType.Auto)
        {
            uint? startNextMs = null;
            if (asset.CueStartNext.HasValue)
                startNextMs = (uint)(asset.CueStartNext.Value * 1000);
            else if (asset.CueFadeOut.HasValue)
                startNextMs = (uint)(asset.CueFadeOut.Value * 1000);
            else
            {
                // No explicit StartNext/FadeOut: compute from broadcast end minus crossfade.
                // CueEnd acts as hard broadcast boundary; crossfade=0 still triggers at CueEnd.
                if (effectiveCrossfadeMs > 0 && effectiveEndMs > effectiveCrossfadeMs)
                    startNextMs = effectiveEndMs - effectiveCrossfadeMs;
                else if (cueEnd.HasValue)
                    startNextMs = effectiveEndMs;
            }

            if (startNextMs.HasValue)
                list.Add(MakeCue(asset, MarkerType.StartNext, startNextMs.Value));

            // Independent fade-out only when both markers are set (Variant B)
            if (asset.CueFadeOut.HasValue && asset.CueStartNext.HasValue)
                list.Add(MakeCue(asset, MarkerType.FadeOut, (uint)(asset.CueFadeOut.Value * 1000)));
        }
        // SegueType.Manual: no automation markers

        return list;
    }

    public static IReadOnlyList<EnvelopePoint>? DeserializeEnvelope(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<EnvelopePoint>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch { return null; }
    }

    public static string DeriveTitleFromPath(string filePath)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(filePath);
        return string.IsNullOrWhiteSpace(name) ? filePath : name;
    }

    private static AssetCuePoint MakeCue(Asset asset, MarkerType type, uint positionMs) => new()
    {
        CueId      = string.Empty,
        AssetId    = asset.AssetId,
        MarkerType = type,
        PositionMs = positionMs
    };
}
