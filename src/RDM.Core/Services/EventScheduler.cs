using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Interfaces;
using RDM.Shared.Enums;

namespace RDM.Core.Services;

/// <summary>
/// Checks whether any enabled ScheduledEvents are due and executes their actions sequentially.
/// Called exclusively by EventSchedulerService every 1 second.
///
/// SmartTiming: if a track is playing when the event is due, execution is deferred until the
/// track ends OR 300 seconds have elapsed (whichever comes first).
/// </summary>
public sealed class EventScheduler
{
    private readonly IScheduledEventRepository   _eventRepo;
    private readonly IEventBus                   _eventBus;
    private readonly IPlaylistController         _playlist;
    private readonly ScheduledActionExecutor     _executor;
    private readonly StudioContext               _studioContext;
    private readonly ILogger<EventScheduler>     _logger;

    // Keyed by EventId — prevents duplicate entries for the same event across ticks
    private readonly Dictionary<string, PendingSmartEvent> _pendingSmartEvents = new();
    private readonly TimeSpan SmartTimingTimeout = TimeSpan.FromSeconds(300);

    private record PendingSmartEvent(
        Core.Entities.ScheduledEvent Event,
        DateTime                     PendingSince);

    public EventScheduler(
        IScheduledEventRepository  eventRepo,
        IEventBus                  eventBus,
        IPlaylistController        playlist,
        ScheduledActionExecutor    executor,
        StudioContext              studioContext,
        ILogger<EventScheduler>    logger)
    {
        _eventRepo     = eventRepo;
        _eventBus      = eventBus;
        _playlist      = playlist;
        _executor      = executor;
        _studioContext = studioContext;
        _logger        = logger;
    }

    /// <param name="ct">Cancellation token.</param>
    /// <param name="forceNow">Override current time — used only in unit tests.</param>
    public async Task CheckAndFireDueEventsAsync(
        CancellationToken ct, DateTime? forceNow = null)
    {
        // Local wall-clock time: operators schedule events in their studio's local time
        // (event_hour / hours / days / only_on_date are all local), so due-checks must
        // compare against DateTime.Now, not UtcNow.
        var now    = forceNow ?? DateTime.Now;
        var events = await _eventRepo.GetEnabledAsync(_studioContext.StudioId, ct);

        foreach (var evt in events)
        {
            if (!IsDue(evt, now))
                continue;

            // Already queued for smart-timing — the pending loop below handles it
            if (_pendingSmartEvents.ContainsKey(evt.EventId))
                continue;

            if (evt.SkipNext)
            {
                await _eventRepo.UpdateSkipNextAsync(evt.EventId, false, ct);
                await _eventBus.PublishAsync(
                    new ScheduledEventSkippedEvent(evt.EventId, evt.Name), ct);
                _logger.LogInformation("Event '{Name}' skipped (skip_next=true)", evt.Name);
                continue;
            }

            if (evt.SmartTiming && _playlist.IsPlaying)
            {
                _pendingSmartEvents[evt.EventId] = new PendingSmartEvent(evt, now);
                _logger.LogDebug(
                    "Event '{Name}' deferred — smart_timing, track playing", evt.Name);
                continue;
            }

            await FireEventAsync(evt, now, ct);
        }

        // Process pending smart events — fire when track stops OR timeout reached
        foreach (var (eventId, pending) in _pendingSmartEvents.ToList())
        {
            bool timedOut = (now - pending.PendingSince) >= SmartTimingTimeout;
            if (!_playlist.IsPlaying || timedOut)
            {
                if (timedOut)
                    _logger.LogInformation(
                        "Event '{Name}' smart_timing timeout ({S}s) — firing now",
                        pending.Event.Name, (int)SmartTimingTimeout.TotalSeconds);

                await FireEventAsync(pending.Event, now, ct);
                _pendingSmartEvents.Remove(eventId);
            }
        }
    }

    // ── IsDue logic ───────────────────────────────────────────────────────────

    public static bool IsDue(Core.Entities.ScheduledEvent evt, DateTime now)
    {
        if (evt.EventHour is null)
            return false;

        return evt.EventType == ScheduledEventType.OneTime
            ? IsDueOneTime(evt, now)
            : IsDueRepeat(evt, now);
    }

    private static bool IsDueRepeat(Core.Entities.ScheduledEvent evt, DateTime now)
    {
        string dayAbbr = now.DayOfWeek switch
        {
            DayOfWeek.Monday    => "MON",
            DayOfWeek.Tuesday   => "TUE",
            DayOfWeek.Wednesday => "WED",
            DayOfWeek.Thursday  => "THU",
            DayOfWeek.Friday    => "FRI",
            DayOfWeek.Saturday  => "SAT",
            DayOfWeek.Sunday    => "SUN",
            _                   => string.Empty
        };

        // Check active day
        var days = evt.Days.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (!days.Contains(dayAbbr, StringComparer.OrdinalIgnoreCase))
            return false;

        // Check active hour
        var hours = evt.Hours.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (!hours.Contains(now.Hour.ToString()))
            return false;

        // Scheduled time = start of current hour + event_hour minutes:seconds
        var eh = evt.EventHour!.Value;
        var scheduledTime = now.Date
            .AddHours(now.Hour)
            .AddMinutes(eh.Minutes)
            .AddSeconds(eh.Seconds);

        // 1-minute fire window
        if (now < scheduledTime || now >= scheduledTime.AddMinutes(1))
            return false;

        // Idempotency: already fired in this window?
        if (evt.LastFiredAt.HasValue && evt.LastFiredAt.Value >= scheduledTime)
            return false;

        return true;
    }

    private static bool IsDueOneTime(Core.Entities.ScheduledEvent evt, DateTime now)
    {
        if (evt.OnlyOnDate is null)
            return false;

        if (evt.OnlyOnDate.Value != DateOnly.FromDateTime(now))
            return false;

        var scheduledTime = evt.OnlyOnDate.Value
            .ToDateTime(TimeOnly.MinValue)
            .Add(evt.EventHour!.Value);

        if (now < scheduledTime || now >= scheduledTime.AddMinutes(1))
            return false;

        // ONE_TIME fires exactly once
        return !evt.LastFiredAt.HasValue;
    }

    // ── Action execution ──────────────────────────────────────────────────────

    private async Task FireEventAsync(
        Core.Entities.ScheduledEvent evt, DateTime firedAt, CancellationToken ct)
    {
        string result = "SUCCESS";
        try
        {
            await _executor.ExecuteAsync(evt.Actions, ct);
            _logger.LogInformation("Scheduled event '{Name}' fired at {At:HH:mm:ss}", evt.Name, firedAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled event '{Name}' failed during action execution", evt.Name);
            result = "FAILED";
        }

        // Recorded regardless of outcome — the idempotency check in IsDue* keys off this,
        // so a persistently-failing action (e.g. EXECUTE_FILE with external execution disabled)
        // must not be retried every tick for the rest of its 1-minute fire window.
        await _eventRepo.UpdateLastFiredAsync(evt.EventId, firedAt, ct);

        await _eventBus.PublishAsync(
            new ScheduledEventFiredEvent(evt.EventId, evt.Name, firedAt, evt.Actions, result), ct);
    }

    public async Task<(Core.Entities.ScheduledEvent Event, DateTime FiresAt)?> GetNextFiringAsync(CancellationToken ct = default)
    {
        var now = DateTime.Now;   // local wall-clock — see CheckAndFireDueEventsAsync
        var events = await _eventRepo.GetEnabledAsync(_studioContext.StudioId, ct);
        
        Core.Entities.ScheduledEvent? nextEvent = null;
        DateTime? minFiringTime = null;

        foreach (var evt in events)
        {
            var nextFiring = CalculateNextFiring(evt, now);
            if (nextFiring.HasValue)
            {
                if (minFiringTime == null || nextFiring.Value < minFiringTime.Value)
                {
                    minFiringTime = nextFiring.Value;
                    nextEvent = evt;
                }
            }
        }

        if (nextEvent != null && minFiringTime.HasValue)
        {
            return (nextEvent, minFiringTime.Value);
        }

        return null;
    }

    private static DateTime? CalculateNextFiring(Core.Entities.ScheduledEvent evt, DateTime now)
    {
        if (evt.EventHour == null) return null;

        if (evt.EventType == ScheduledEventType.OneTime)
        {
            if (evt.OnlyOnDate == null) return null;
            var firingTime = evt.OnlyOnDate.Value.ToDateTime(TimeOnly.MinValue).Add(evt.EventHour.Value);
            return firingTime > now ? firingTime : null;
        }

        var days = evt.Days.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hours = evt.Hours.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => int.TryParse(h, out var parsed) ? parsed : -1)
            .Where(h => h >= 0)
            .ToHashSet();

        if (days.Count == 0 || hours.Count == 0) return null;

        var current = now.Date.AddHours(now.Hour);
        for (int i = 0; i < 168; i++) // up to 7 days
        {
            var checkHour = current.AddHours(i);
            if (!hours.Contains(checkHour.Hour))
                continue;

            string dayAbbr = checkHour.DayOfWeek switch
            {
                DayOfWeek.Monday    => "MON",
                DayOfWeek.Tuesday   => "TUE",
                DayOfWeek.Wednesday => "WED",
                DayOfWeek.Thursday  => "THU",
                DayOfWeek.Friday    => "FRI",
                DayOfWeek.Saturday  => "SAT",
                DayOfWeek.Sunday    => "SUN",
                _                   => string.Empty
            };

            if (!days.Contains(dayAbbr))
                continue;

            var eh = evt.EventHour.Value;
            var scheduledTime = checkHour.Date.AddHours(checkHour.Hour).AddMinutes(eh.Minutes).AddSeconds(eh.Seconds);

            if (scheduledTime > now)
            {
                if (evt.LastFiredAt.HasValue && evt.LastFiredAt.Value >= scheduledTime)
                    continue;

                return scheduledTime;
            }
        }

        return null;
    }
}
