using System.Xml.Linq;
using RDM.Core.Interfaces;
using RDM.Core.Models;

namespace RDM.Infrastructure.FileSystem;

/// <summary>
/// Reads metadata from a MusicMaster/Selector .mmd XML sidecar (track.mp3.mmd).
/// Format: PlaylistItem XML with Title, Artist, Duration, Attributes, and Markers.
/// Marker types: CueIn, Ramp1, Ramp2, Ramp3, HookIn, HookOut, LoopIn, LoopOut,
///               Outro, StartNext, FadeOut, FadeEnd, CueOut, Anchor.
/// </summary>
public sealed class MmdFileReader : IMetadataReader
{
    public MetadataReaderKind Kind => MetadataReaderKind.Mmd;

    public Task<AssetMetadata?> TryReadAsync(string filePath, CancellationToken ct = default)
    {
        string mmdPath = filePath + ".mmd";
        if (!File.Exists(mmdPath)) return Task.FromResult<AssetMetadata?>(null);

        try
        {
            var doc  = XDocument.Load(mmdPath);
            var root = doc.Root;
            if (root is null) return Task.FromResult<AssetMetadata?>(null);

            string? title    = root.Element("Title")?.Value;
            string? artist   = root.Element("Artist")?.Value;
            string? album    = GetAttribute(root, "Album");

            double? durationSecs = TryParseDouble(root.Element("Duration")?.Value);
            uint?   durationMs   = durationSecs.HasValue ? (uint)(durationSecs.Value * 1000) : null;

            var markers  = root.Element("Markers")?.Elements("Marker") ?? [];
            var markerMap = markers.ToDictionary(
                m => m.Attribute("Type")?.Value ?? "",
                m => TryParseDouble(m.Attribute("Position")?.Value),
                StringComparer.OrdinalIgnoreCase);

            return Task.FromResult<AssetMetadata?>(new AssetMetadata
            {
                Title      = title,
                Artist     = artist,
                Album      = album,
                DurationMs = durationMs,

                CueStart     = markerMap.GetValueOrDefault("CueIn"),
                CueRamp2     = markerMap.GetValueOrDefault("Ramp2"),
                CueRamp3     = markerMap.GetValueOrDefault("Ramp3"),
                CueHookIn    = markerMap.GetValueOrDefault("HookIn"),
                CueHookOut   = markerMap.GetValueOrDefault("HookOut"),
                CueLoopIn    = markerMap.GetValueOrDefault("LoopIn"),
                CueLoopOut   = markerMap.GetValueOrDefault("LoopOut"),
                CueOutro     = markerMap.GetValueOrDefault("Outro"),
                CueStartNext = markerMap.GetValueOrDefault("StartNext"),
                CueFadeOut   = markerMap.GetValueOrDefault("FadeOut"),
                CueFadeEnd   = markerMap.GetValueOrDefault("FadeEnd"),
                CueEnd       = markerMap.GetValueOrDefault("CueOut"),
                CueAnchor    = markerMap.GetValueOrDefault("Anchor"),
            });
        }
        catch
        {
            return Task.FromResult<AssetMetadata?>(null);
        }
    }

    private static string? GetAttribute(XElement root, string name) =>
        root.Element("Attributes")?
            .Elements("Item")
            .FirstOrDefault(i => string.Equals(i.Element("Name")?.Value, name, StringComparison.OrdinalIgnoreCase))?
            .Element("Value")?.Value;

    private static double? TryParseDouble(string? s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
}
