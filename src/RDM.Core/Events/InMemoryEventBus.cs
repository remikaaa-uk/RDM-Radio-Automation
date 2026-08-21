using RDM.Core.Interfaces;

namespace RDM.Core.Events;

public sealed class InMemoryEventBus : IEventBus
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();
    private readonly object _lock = new();

    public void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class
    {
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list))
            {
                list = new List<Delegate>();
                _handlers[typeof(TEvent)] = list;
            }
            list.Add(handler);
        }
    }

    public void Unsubscribe<TEvent>(Action<TEvent> handler) where TEvent : class
    {
        lock (_lock)
        {
            if (_handlers.TryGetValue(typeof(TEvent), out var list))
                list.Remove(handler);
        }
    }

    public Task PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default) where TEvent : class
    {
        List<Delegate>? snapshot;
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(TEvent), out var list) || list.Count == 0)
                return Task.CompletedTask;
            snapshot = new List<Delegate>(list);
        }

        foreach (var handler in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            ((Action<TEvent>)handler)(evt);
        }
        return Task.CompletedTask;
    }
}
