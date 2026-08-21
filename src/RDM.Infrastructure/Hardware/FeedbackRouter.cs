using Microsoft.Extensions.Logging;
using RDM.Core.Events;
using RDM.Core.Hardware;
using RDM.Core.Interfaces;

namespace RDM.Infrastructure.Hardware;

/// <summary>
/// Translates domain events (player start, mic on, etc.) into HardwareOutputCommands
/// consumed by output drivers (LED, GPO). Keeps business logic out of output drivers.
/// </summary>
public sealed class FeedbackRouter
{
    private readonly IEventBus _eventBus;
    private readonly IFeedbackMappingCache _feedbackCache;
    private readonly ILogger<FeedbackRouter> _logger;

    public FeedbackRouter(IEventBus eventBus, IFeedbackMappingCache feedbackCache, ILogger<FeedbackRouter> logger)
    {
        _eventBus      = eventBus;
        _feedbackCache = feedbackCache;
        _logger        = logger;

        _eventBus.Subscribe<PlaylistItemStartedEvent>(OnTrackStarted);
        _eventBus.Subscribe<PlaylistItemEndedEvent>(OnTrackEnded);
        _eventBus.Subscribe<PlaylistStoppedEvent>(OnPlaylistStopped);
        _eventBus.Subscribe<MicActivatedEvent>(OnMicActivated);
        _eventBus.Subscribe<MicDeactivatedEvent>(OnMicDeactivated);
        _eventBus.Subscribe<CartTriggeredEvent>(OnCartTriggered);
        _eventBus.Subscribe<CartEndedEvent>(OnCartEnded);
    }

    private void OnTrackStarted(PlaylistItemStartedEvent _)   => Publish("Player.Started",  isActive: true);
    private void OnTrackEnded(PlaylistItemEndedEvent _)       => Publish("Player.Stopped",  isActive: false);
    private void OnPlaylistStopped(PlaylistStoppedEvent _)    => Publish("Player.Stopped",  isActive: false);
    private void OnMicActivated(MicActivatedEvent _)          => Publish("Mic.On",          isActive: true);
    private void OnMicDeactivated(MicDeactivatedEvent _)      => Publish("Mic.Off",         isActive: false);
    private void OnCartTriggered(CartTriggeredEvent _)        => Publish("Cart.Started",    isActive: true);
    private void OnCartEnded(CartEndedEvent _)                => Publish("Cart.Stopped",    isActive: false);

    private void Publish(string eventName, bool isActive)
    {
        var rules = _feedbackCache.GetFeedbackRules(eventName);
        if (rules.Count == 0) return;

        foreach (var rule in rules)
        {
            IHardwarePayload payload = rule.TargetDeviceType switch
            {
                "DR_MIXER" => new DrMixerPayload(
                    target:   rule.DrTarget ?? "Gpo",
                    index:    rule.DrGpoIndex ?? 1,
                    value:    isActive ? 1f : 0f,
                    isActive: isActive),

                "SERIAL" => new SerialCommandPayload(rule.SerialCommand ?? string.Empty, isActive),

                _ => new MidiNotePayload(rule.Channel, rule.NoteCode, rule.Velocity, isActive)
            };

            var command = new HardwareOutputCommand(
                TargetDeviceId: rule.TargetDeviceId,
                DeviceType:     rule.TargetDeviceType,
                Payload:        payload,
                Timestamp:      DateTime.UtcNow);

            _ = _eventBus.PublishAsync(command);
        }

        _logger.LogDebug("FeedbackRouter: {EventName} → {Count} komend wyjściowych", eventName, rules.Count);
    }
}
