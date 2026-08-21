using RDM.Shared.Enums;

namespace RDM.Core.Entities;

public sealed class Asset
{
    public string AssetId { get; init; } = string.Empty;
    public AssetType AssetType { get; init; }
    public string? FormatId        { get; init; }
    public string? FormatName      { get; init; }
    public string? SubcategoryId   { get; init; }
    public string? SubcategoryName { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public uint DurationMs { get; init; }
    public string Checksum { get; init; } = string.Empty;
    public string? RdmFilePath { get; init; }
    public string? StreamUrl   { get; init; }
    public string? ImagePath { get; init; }
    public decimal? Bpm { get; init; }
    public int? Year { get; init; }
    public byte? Rating { get; init; }
    public string? Mood { get; init; }
    public string? Gender { get; init; }
    public string? Language { get; init; }
    public string? Genre    { get; init; }
    public string? Comments { get; init; }
    public bool IsDamaged { get; init; }
    /// <summary>Content whose real duration varies between plays of the same file (e.g. a news
    /// bulletin re-recorded in place). When true, PlaylistEngine re-analyzes Start/End cues live
    /// at load time instead of trusting the stored CueStart/CueEnd, which can be stale.</summary>
    public bool IsVariableDuration { get; init; }
    public AssetStatus Status { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public uint? PlayLimit { get; init; }
    public uint PlayCount { get; init; }
    public DateTime? LastPlayedAt { get; init; }
    public decimal? LoudnessLufs { get; init; }
    public decimal? LoudnessPeak { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    // ── Cue Editor markers (seconds, null = not set) ──────────────────────────
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
}
