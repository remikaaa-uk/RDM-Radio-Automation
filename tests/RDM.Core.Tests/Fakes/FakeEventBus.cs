using System.Collections.Concurrent;
using RDM.Core.Interfaces;

namespace RDM.Core.Tests.Fakes;

/// <summary>
/// Test double for IEventBus. Records all published events for assertion.
/// Thread-safe via ConcurrentQueue — safe for fire-and-forget async handlers.
/// </summary>
public sealed class FakeEventBus : IEventBus
{
    public ConcurrentQueue<object> PublishedEvents { get; } = new();

    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : class
    {
        PublishedEvents.Enqueue(@event);
        return Task.CompletedTask;
    }

    public void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class { }
    public void Unsubscribe<TEvent>(Action<TEvent> handler) where TEvent : class { }

    // ── Helpers for assertions ────────────────────────────────────────────────

    public IReadOnlyList<T> Published<T>() where T : class =>
        PublishedEvents.OfType<T>().ToList();

    public int CountOf<T>() where T : class =>
        PublishedEvents.OfType<T>().Count();
}
