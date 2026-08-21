using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Hardware;

public record ActionExecutionTask(
    Func<IHardwarePayload, Task> ActionDelegate,
    IHardwarePayload Payload,
    ActionId ActionId);

public sealed class ActionRouter : IHardwareMetrics
{
    private readonly IEventBus _eventBus;
    private readonly IActionRegistry _actionRegistry;
    private readonly ITriggerMappingCache _mappingCache;
    private readonly IHardwareLearnService _learnService;
    private readonly ILogger<ActionRouter> _logger;

    private readonly Channel<ActionExecutionTask> _eventChannel;

    private readonly ConcurrentDictionary<string, DateTime> _lastThrottledEvents = new();
    private static readonly TimeSpan ThrottleInterval = TimeSpan.FromMilliseconds(30);

    private readonly ConcurrentDictionary<ActionId, DateTime> _lastExecutedActions = new();
    private static readonly TimeSpan DeduplicationWindow = TimeSpan.FromMilliseconds(200);

    // ── Telemetria (sekcja 11.3) ──────────────────────────────────────────────
    private long _throttledEventsCount;
    private long _deduplicatedEventsCount;
    private long _hardwareErrorCount;
    private long _executionTimeMs;

    public int  QueueLength             => _eventChannel.Reader.Count;
    public long ExecutionTimeMs         => Interlocked.Read(ref _executionTimeMs);
    public long ThrottledEventsCount    => Interlocked.Read(ref _throttledEventsCount);
    public long DeduplicatedEventsCount => Interlocked.Read(ref _deduplicatedEventsCount);
    public long HardwareErrorCount      => Interlocked.Read(ref _hardwareErrorCount);

    public ActionRouter(
        IEventBus eventBus,
        IActionRegistry actionRegistry,
        ITriggerMappingCache mappingCache,
        IHardwareLearnService learnService,
        ILogger<ActionRouter> logger)
    {
        _eventBus       = eventBus;
        _actionRegistry = actionRegistry;
        _mappingCache   = mappingCache;
        _learnService   = learnService;
        _logger         = logger;

        _eventChannel = Channel.CreateBounded<ActionExecutionTask>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode     = BoundedChannelFullMode.DropOldest
        });

        _eventBus.Subscribe<HardwareInputEvent>(OnHardwareInputReceived);

        Task.Run(ProcessEventsQueueAsync);
    }

    private void OnHardwareInputReceived(HardwareInputEvent evt)
    {
        if (_learnService.IsLearningActive)
        {
            _learnService.HandleEvent(evt);
            return;
        }

        if (evt.Payload is MidiCcPayload || (evt.Payload is DrMixerPayload dr && dr.Target == "Fader"))
        {
            var sig = evt.Payload.Signature;
            var now = DateTime.UtcNow;
            if (_lastThrottledEvents.TryGetValue(sig, out var lastTime) && (now - lastTime) < ThrottleInterval)
            {
                Interlocked.Increment(ref _throttledEventsCount);
                return;
            }
            _lastThrottledEvents[sig] = now;
        }

        var lookupKey = new TriggerLookupKey(evt.DeviceType, evt.Payload.Signature);
        if (!_mappingCache.TryGetMappings(lookupKey, out var mappings))
            return;

        foreach (var match in mappings)
        {
            if (!match.IsEnabled) continue;
            if (match.SourceDeviceId != null &&
                !string.Equals(match.SourceDeviceId, evt.DeviceId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!ShouldExecuteAction(match.TargetActionId)) continue;

            var actionDelegate = _actionRegistry.GetActionDelegate(match.TargetActionId);
            if (actionDelegate is null) continue;

            var normalized = NormalizePayload(evt.Payload);
            // Jeśli mapping ma TargetParameter, owijamy payload żeby delegate mógł go odczytać.
            IHardwarePayload payload = !string.IsNullOrEmpty(match.TargetParameter)
                ? new ParameterizedPayload(normalized, match.TargetParameter)
                : normalized;

            // Akcje krytyczne i playback omijają kolejkę FIFO — uruchamiane natychmiastowo
            if (match.TargetActionId is ActionId.AutomationEmergencyPanic or ActionId.AutomationRunScript
                or ActionId.PlayerPlay or ActionId.PlayerStop or ActionId.PlayerNext
                or ActionId.PlayerPlayStopToggle or ActionId.PlayerPause or ActionId.MicToggle
                or ActionId.PlayerLoopToggle or ActionId.PlayerRemoveFromPlayer)
            {
                var timeout = match.TargetActionId is ActionId.AutomationEmergencyPanic or ActionId.AutomationRunScript
                    ? TimeSpan.FromSeconds(6)
                    : TimeSpan.FromSeconds(2);
                _ = Task.Run(() => ExecuteWithTimeoutAsync(actionDelegate, payload, match.TargetActionId, timeout));
                continue;
            }

            _eventChannel.Writer.TryWrite(new ActionExecutionTask(actionDelegate, payload, match.TargetActionId));
        }
    }

    private bool ShouldExecuteAction(ActionId actionId)
    {
        if (actionId is ActionId.PlayerPlay or ActionId.PlayerStop or ActionId.PlayerNext
            or ActionId.MicToggle or ActionId.PlayerPlayStopToggle or ActionId.PlayerPause
            or ActionId.PlayerLoopToggle or ActionId.PlayerRemoveFromPlayer)
        {
            var now = DateTime.UtcNow;
            if (_lastExecutedActions.TryGetValue(actionId, out var lastTime) && (now - lastTime) < DeduplicationWindow)
            {
                Interlocked.Increment(ref _deduplicatedEventsCount);
                return false;
            }
            _lastExecutedActions[actionId] = now;
        }
        return true;
    }

    private static IHardwarePayload NormalizePayload(IHardwarePayload raw) => raw switch
    {
        MidiCcPayload cc                            => new NormalizedAnalogPayload(cc.Value / 127f),
        DrMixerPayload dr when dr.Target == "Fader" => new NormalizedAnalogPayload(dr.Value),
        _                                           => raw
    };

    private async Task ProcessEventsQueueAsync()
    {
        var reader = _eventChannel.Reader;
        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var task))
                await ExecuteWithTimeoutAsync(task.ActionDelegate, task.Payload, task.ActionId, TimeSpan.FromMilliseconds(80));
        }
    }

    private async Task ExecuteWithTimeoutAsync(
        Func<IHardwarePayload, Task> actionDelegate,
        IHardwarePayload payload,
        ActionId actionId,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var sw = Stopwatch.StartNew();
        try
        {
            var actionTask = actionDelegate(payload);

            if (actionTask.IsCompleted)
            {
                await actionTask;
            }
            else
            {
                var completed = await Task.WhenAny(actionTask, Task.Delay(timeout, cts.Token));
                if (completed == actionTask)
                {
                    cts.Cancel();
                    await actionTask;
                }
                else
                {
                    _logger.LogWarning("Akcja {ActionId} przekroczyła limit czasu {Timeout}ms", actionId, timeout.TotalMilliseconds);
                    Interlocked.Increment(ref _hardwareErrorCount);
                    return;
                }
            }

            sw.Stop();
            Interlocked.Exchange(ref _executionTimeMs, sw.ElapsedMilliseconds);

            if (sw.ElapsedMilliseconds > 20)
                _logger.LogWarning("Akcja {ActionId} wykonana w {Ms}ms (powyżej progu 20ms)", actionId, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _hardwareErrorCount);
            _logger.LogError(ex, "Błąd podczas wykonywania akcji {ActionId}", actionId);
        }
    }
}
