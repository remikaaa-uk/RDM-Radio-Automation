using RDM.Shared.Enums;

namespace RDM.Core.Entities;

public sealed class PlaylistItem
{
    public string ItemId { get; init; } = string.Empty;
    public string PlaylistId { get; init; } = string.Empty;
    public string? AssetId { get; init; }
    public uint Position { get; init; }
    public PlaylistItemType ItemType { get; init; }
    public string? ExternalFilePath { get; init; }
    public string? DummyLabel { get; init; }
    public string? DummyNote { get; init; }
    public uint? DummyDurationMs { get; init; }
    public uint? CrossfadeMs { get; init; }
    /// Signed free-positioning offset (ms): start[i-1] + dur[i-1] - start[i].
    /// Positive = overlap / earlier start, negative = gap. Source of truth for the
    /// segue editor's free layout; not bounded by the previous item's duration.
    /// When null, layout falls back to <see cref="CrossfadeMs"/>.
    public int? LeadInMs { get; init; }
    public uint? TrimStartMs { get; init; }
    public uint? TrimEndMs { get; init; }
    public SegueType SegueType { get; init; }
    public DateTime? ScheduledAt { get; init; }
    public bool AutoLinkNext { get; init; }
    /// JSON-serialised list of EnvelopePoint; null means flat (no automation).
    public string? VolumeEnvelope { get; init; }
}
