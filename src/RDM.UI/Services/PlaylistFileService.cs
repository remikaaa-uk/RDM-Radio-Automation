using RDM.Shared.DTOs;
using RDM.UI.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace RDM.UI.Services;

public record PlaylistFileDocument(string Name, DateTime SavedAt, List<PlaylistFileItem> Items);

public record PlaylistFileItem(
    string          AssetId,
    string          Title,
    string?         Artist,
    string          AssetType,
    uint            DurationMs,
    string          SegueType,
    uint?           CrossfadeMs     = null,
    uint?           TrimStartMs     = null,
    uint?           TrimEndMs       = null,
    CueMarkersDto?  CueMarkers      = null,
    string?         Album           = null,
    decimal?        Bpm             = null,
    string?         FormatName      = null,
    string?         SubcategoryName = null,
    int?            Year            = null,
    byte?           Rating          = null,
    string?         Genre           = null,
    string?         Mood            = null,
    string?         Gender          = null,
    string?         Language        = null,
    decimal?        LoudnessLufs    = null);

public record M3uEntry(string FilePath, string? DisplayName, string? Artist, string? Title, int DurationSec);

public static class PlaylistFileService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task SaveAsync(string filePath, string name, IEnumerable<BuilderItemViewModel> items)
    {
        var doc = new PlaylistFileDocument(
            name, DateTime.UtcNow,
            items.Select(i => new PlaylistFileItem(
                AssetId:         i.AssetId,
                Title:           i.Title,
                Artist:          i.Artist,
                AssetType:       i.AssetType,
                DurationMs:      i.DurationMs,
                SegueType:       i.OriginalDto?.SegueType ?? "AUTO",
                CrossfadeMs:     i.CrossfadeMs > 0 ? i.CrossfadeMs : null,
                TrimStartMs:     i.TrimStartMs > 0 ? i.TrimStartMs : null,
                TrimEndMs:       i.TrimEndMs   > 0 ? i.TrimEndMs   : null,
                CueMarkers:      i.CueMarkers,
                Album:           i.Album,
                Bpm:             i.Bpm,
                FormatName:      i.FormatName,
                SubcategoryName: i.SubcategoryName,
                Year:            i.Year,
                Rating:          i.Rating,
                Genre:           i.Genre,
                Mood:            i.Mood,
                Gender:          i.Gender,
                Language:        i.Language,
                LoudnessLufs:    i.LoudnessLufs
            )).ToList());

        await File.WriteAllTextAsync(filePath, JsonSerializer.Serialize(doc, JsonOpts));
    }

    public static async Task<PlaylistFileDocument?> LoadAsync(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<PlaylistFileDocument>(json, JsonOpts);
    }

    // ── M3U ───────────────────────────────────────────────────────────────────

    /// Writes a standard extended M3U file (#EXTM3U / #EXTINF).
    /// <paramref name="items"/> — tuples of (Title, Artist, DurationMs, AudioFilePath).
    public static async Task SaveM3uAsync(
        string filePath,
        IEnumerable<(string Title, string? Artist, uint DurationMs, string? AudioPath)> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        foreach (var (title, artist, durationMs, path) in items)
        {
            var durationSec = (int)Math.Round(durationMs / 1000.0);
            var display     = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} - {title}";
            sb.AppendLine($"#EXTINF:{durationSec},{display}");
            sb.AppendLine(path ?? "");
        }
        await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8);
    }

    /// Parses a standard M3U / M3U8 file. Returns one entry per track.
    public static async Task<List<M3uEntry>> LoadM3uAsync(string filePath)
    {
        if (!File.Exists(filePath)) return new();

        var lines   = await File.ReadAllLinesAsync(filePath, Encoding.UTF8);
        var results = new List<M3uEntry>();

        string? pendingDisplay = null;
        int     pendingDur     = -1;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line == "#EXTM3U") continue;

            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                // #EXTINF:seconds,Display Name
                var rest     = line[8..];
                var commaIdx = rest.IndexOf(',');
                if (commaIdx > 0)
                {
                    _ = int.TryParse(rest[..commaIdx], out pendingDur);
                    pendingDisplay = rest[(commaIdx + 1)..].Trim();
                }
                continue;
            }

            if (line.StartsWith('#')) continue; // unknown comment

            // Track line
            string? artist = null;
            string? title  = pendingDisplay;
            if (pendingDisplay?.Contains(" - ") == true)
            {
                var sep = pendingDisplay.IndexOf(" - ", StringComparison.Ordinal);
                artist = pendingDisplay[..sep].Trim();
                title  = pendingDisplay[(sep + 3)..].Trim();
            }
            results.Add(new M3uEntry(line, pendingDisplay, artist, title, pendingDur));
            pendingDisplay = null;
            pendingDur     = -1;
        }
        return results;
    }
}
