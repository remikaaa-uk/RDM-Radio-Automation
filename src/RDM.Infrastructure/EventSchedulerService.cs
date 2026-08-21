using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RDM.Core.Services;

namespace RDM.Infrastructure;

/// <summary>
/// Calls EventScheduler.CheckAndFireDueEventsAsync() every 1 second.
/// Owns the tick timer; EventScheduler owns the scheduling logic (SRP).
/// </summary>
public sealed class EventSchedulerService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly EventScheduler                _scheduler;
    private readonly ILogger<EventSchedulerService> _logger;

    public EventSchedulerService(
        EventScheduler                 scheduler,
        ILogger<EventSchedulerService> logger)
    {
        _scheduler = scheduler;
        _logger    = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EventSchedulerService started.");

        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _scheduler.CheckAndFireDueEventsAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "EventScheduler tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }

        _logger.LogInformation("EventSchedulerService stopped.");
    }
}
