namespace RDM.Core.Models;

/// <summary>
/// Intermediate DTO used during import — aggregates metadata from the reader chain.
/// Not persisted directly; ImportPipeline maps it to Asset + AssetCuePoint entities.
/// </summary>
public sealed class AssetMetadata
{
    public string?  Title      { get; init; }
    public string?  Artist     { get; init; }
    public string?  Album      { get; init; }
    public decimal? Bpm        { get; init; }
    public int?     Year       { get; init; }
    public string?  Mood       { get; init; }
    public string?  Gender     { get; init; }
    public string?  Language   { get; init; }
    public string?  Genre      { get; init; }
    public string?  Comments   { get; init; }
    public uint?    DurationMs { get; init; }
    public byte[]?  PictureBytes { get; init; }
    public string?  PictureMimeType { get; init; }

    public double? CueStart     { get; init; }
    public double? CueIntro     { get; init; }
    public double? CueRamp2     { get; init; }
    public double? CueRamp3     { get; init; }
    public double? CueOutro     { get; init; }
    public double? CueStartNext { get; init; }
    public double? CueFadeOut   { get; init; }
    public double? CueFadeEnd   { get; init; }
    public double? CueEnd       { get; init; }
    public double? CueHookIn    { get; init; }
    public double? CueHookFade  { get; init; }
    public double? CueHookOut   { get; init; }
    public double? CueLoopIn    { get; init; }
    public double? CueLoopOut   { get; init; }
    public double? CueAnchor    { get; init; }

    public IReadOnlyList<CuePointData> CuePoints { get; init; } = [];

    /// <summary>
    /// Returns a new instance where each field takes this instance's value if non-null,
    /// otherwise falls back to <paramref name="other"/>'s value.
    /// CuePoints: this list wins if non-empty.
    /// </summary>
    public AssetMetadata MergeWith(AssetMetadata other) => new()
    {
        Title           = Title      ?? other.Title,
        Artist          = Artist     ?? other.Artist,
        Album           = Album      ?? other.Album,
        Bpm             = Bpm        ?? other.Bpm,
        Year            = Year       ?? other.Year,
        Mood            = Mood       ?? other.Mood,
        Gender          = Gender     ?? other.Gender,
        Language        = Language   ?? other.Language,
        Genre           = Genre      ?? other.Genre,
        Comments        = Comments   ?? other.Comments,
        DurationMs      = DurationMs ?? other.DurationMs,
        PictureBytes    = PictureBytes ?? other.PictureBytes,
        PictureMimeType = PictureMimeType ?? other.PictureMimeType,
        CueStart        = CueStart     ?? other.CueStart,
        CueIntro        = CueIntro     ?? other.CueIntro,
        CueRamp2        = CueRamp2     ?? other.CueRamp2,
        CueRamp3        = CueRamp3     ?? other.CueRamp3,
        CueOutro        = CueOutro     ?? other.CueOutro,
        CueStartNext    = CueStartNext ?? other.CueStartNext,
        CueFadeOut      = CueFadeOut   ?? other.CueFadeOut,
        CueFadeEnd      = CueFadeEnd   ?? other.CueFadeEnd,
        CueEnd          = CueEnd       ?? other.CueEnd,
        CueHookIn       = CueHookIn    ?? other.CueHookIn,
        CueHookFade     = CueHookFade  ?? other.CueHookFade,
        CueHookOut      = CueHookOut   ?? other.CueHookOut,
        CueLoopIn       = CueLoopIn    ?? other.CueLoopIn,
        CueLoopOut      = CueLoopOut   ?? other.CueLoopOut,
        CueAnchor       = CueAnchor    ?? other.CueAnchor,
        CuePoints       = CuePoints.Count > 0 ? CuePoints : other.CuePoints
    };
}
