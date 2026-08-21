using RDM.Shared.Enums;

namespace RDM.Core.Entities;

public sealed class ScheduledEvent
{
    public string EventId { get; init; } = string.Empty;
    public string StudioId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public ScheduledEventType EventType { get; init; }
    public string Category { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public TimeSpan? EventHour { get; init; }
    public DateOnly? OnlyOnDate { get; init; }
    public string Days { get; init; } = string.Empty;
    public string Hours { get; init; } = string.Empty;
    public bool SmartTiming { get; init; }
    public string Actions { get; init; } = string.Empty;
    public DateTime? LastFiredAt { get; init; }
    public bool SkipNext { get; init; }
    public DateTime CreatedAt { get; init; }
}
