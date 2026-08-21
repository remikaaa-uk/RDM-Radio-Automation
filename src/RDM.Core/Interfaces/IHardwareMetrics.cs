namespace RDM.Core.Interfaces;

/// <summary>
/// Metryki diagnostyczne ActionRouter (spec sekcja 11.3).
/// Eksponowane do GUI dashboardu i WebSocket API.
/// </summary>
public interface IHardwareMetrics
{
    int  QueueLength            { get; }
    long ExecutionTimeMs        { get; }
    long ThrottledEventsCount   { get; }
    long DeduplicatedEventsCount { get; }
    long HardwareErrorCount     { get; }
}
