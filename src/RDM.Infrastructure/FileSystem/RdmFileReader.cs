using System.Text.Json;
using RDM.Core.Interfaces;
using RDM.Core.Models;

namespace RDM.Infrastructure.FileSystem;

/// <summary>
/// Reads metadata from a .rdm JSON sidecar placed next to the audio file.
/// Parses the current format written by <see cref="RdmFileWriter"/>: a snake_case
/// document with a <c>cue_markers</c> object whose values are in seconds. Returns
/// null if no sidecar exists or the JSON cannot be parsed.
/// </summary>
public sealed class RdmFileReader : IMetadataReader
{
    // Must mirror RdmFileWriter's options so the snake_case keys (cue_markers,
    // start_next, hook_in, …) round-trip correctly.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    public MetadataReaderKind Kind => MetadataReaderKind.Rdm;

    public async Task<AssetMetadata?> TryReadAsync(string filePath, CancellationToken ct = default)
    {
        string rdmPath = Path.ChangeExtension(filePath, ".rdm");
        if (!File.Exists(rdmPath)) return null;

        try
        {
            await using var stream = File.OpenRead(rdmPath);
            var dto = await JsonSerializer.DeserializeAsync<RdmDto>(stream, JsonOptions, ct);
            if (dto is null) return null;

            var cm = dto.CueMarkers;

            return new AssetMetadata
            {
                Title        = NullIfEmpty(dto.Title),
                Artist       = NullIfEmpty(dto.Artist),
                Album        = NullIfEmpty(dto.Album),
                Bpm          = dto.Bpm,
                Year         = dto.Year,
                Mood         = NullIfEmpty(dto.Mood),
                Gender       = NullIfEmpty(dto.Gender),
                Language     = NullIfEmpty(dto.Language),
                Genre        = NullIfEmpty(dto.Genre),
                Comments     = NullIfEmpty(dto.Comments),
                CueStart     = cm?.Start,
                CueIntro     = cm?.Intro,
                CueRamp2     = cm?.Ramp2,
                CueRamp3     = cm?.Ramp3,
                CueOutro     = cm?.Outro,
                CueStartNext = cm?.StartNext,
                CueFadeOut   = cm?.FadeOut,
                CueFadeEnd   = cm?.FadeEnd,
                CueEnd       = cm?.End,
                CueHookIn    = cm?.HookIn,
                CueHookFade  = cm?.HookFade,
                CueHookOut   = cm?.HookOut,
                CueLoopIn    = cm?.LoopIn,
                CueLoopOut   = cm?.LoopOut,
                CueAnchor    = cm?.Anchor,
            };
        }
        catch { return null; }
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;

    private sealed record RdmDto(
        string?          Title,
        string?          Artist,
        string?          Album,
        decimal?         Bpm,
        int?             Year,
        string?          Mood,
        string?          Gender,
        string?          Language,
        string?          Genre,
        string?          Comments,
        RdmCueMarkersDto? CueMarkers);

    private sealed record RdmCueMarkersDto(
        double? Start,
        double? Intro,
        double? Ramp2,
        double? Ramp3,
        double? Outro,
        double? StartNext,
        double? FadeOut,
        double? FadeEnd,
        double? End,
        double? HookIn,
        double? HookFade,
        double? HookOut,
        double? LoopIn,
        double? LoopOut,
        double? Anchor);
}
