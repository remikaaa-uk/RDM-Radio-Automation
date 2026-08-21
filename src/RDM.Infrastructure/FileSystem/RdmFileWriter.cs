using System.Text.Json;
using System.Text.Json.Serialization;
using RDM.Core.Entities;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.FileSystem;

public sealed class RdmFileWriter : IRdmFileWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented                = true,
        PropertyNamingPolicy         = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition       = JsonIgnoreCondition.Never
    };

    public async Task WriteAsync(Asset asset, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(asset.RdmFilePath)) return;

        string rdmPath = Path.ChangeExtension(asset.RdmFilePath, ".rdm");

        var dto = new RdmExportDto(
            RdmVersion:     "1.0",
            AssetId:        asset.AssetId,
            AssetType:      asset.AssetType.ToString().ToUpperInvariant(),
            FormatId:       asset.FormatId,
            FormatName:     asset.FormatName,
            SubcategoryId:  asset.SubcategoryId,
            SubcategoryName:asset.SubcategoryName,
            Title:          asset.Title,
            Artist:         asset.Artist,
            Album:          asset.Album,
            DurationMs:     asset.DurationMs,
            Bpm:            asset.Bpm,
            Year:           asset.Year,
            Rating:         asset.Rating,
            Mood:           asset.Mood,
            Gender:         asset.Gender,
            Language:       asset.Language,
            Genre:          asset.Genre,
            Comments:       asset.Comments,
            LoudnessLufs:   asset.LoudnessLufs,
            LoudnessPeak:   asset.LoudnessPeak,
            IsDamaged:      asset.IsDamaged,
            StreamUrl:      asset.StreamUrl,
            Checksum:       asset.Checksum,
            CueMarkers: new RdmCueMarkersDto(
                Start:     asset.CueStart,
                Intro:     asset.CueIntro,
                Ramp2:     asset.CueRamp2,
                Ramp3:     asset.CueRamp3,
                Outro:     asset.CueOutro,
                StartNext: asset.CueStartNext,
                FadeOut:   asset.CueFadeOut,
                FadeEnd:   asset.CueFadeEnd,
                End:       asset.CueEnd,
                HookIn:    asset.CueHookIn,
                HookFade:  asset.CueHookFade,
                HookOut:   asset.CueHookOut,
                LoopIn:    asset.CueLoopIn,
                LoopOut:   asset.CueLoopOut,
                Anchor:    asset.CueAnchor),
            GainDb:     0.0,
            CreatedAt:  asset.CreatedAt,
            UpdatedAt:  asset.UpdatedAt,
            ExportedAt: DateTime.UtcNow);

        await using var stream = new FileStream(rdmPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 4096, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, dto, JsonOptions, ct);
    }

    private sealed record RdmExportDto(
        string    RdmVersion,
        string    AssetId,
        string    AssetType,
        string?   FormatId,
        string?   FormatName,
        string?   SubcategoryId,
        string?   SubcategoryName,
        string    Title,
        string?   Artist,
        string?   Album,
        uint      DurationMs,
        decimal?  Bpm,
        int?      Year,
        byte?     Rating,
        string?   Mood,
        string?   Gender,
        string?   Language,
        string?   Genre,
        string?   Comments,
        decimal?  LoudnessLufs,
        decimal?  LoudnessPeak,
        bool      IsDamaged,
        string?   StreamUrl,
        string    Checksum,
        RdmCueMarkersDto CueMarkers,
        double    GainDb,
        DateTime  CreatedAt,
        DateTime  UpdatedAt,
        DateTime  ExportedAt);

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
