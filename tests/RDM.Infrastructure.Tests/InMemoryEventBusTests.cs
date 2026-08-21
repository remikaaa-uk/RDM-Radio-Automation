using FluentAssertions;
using RDM.Core.Events;
using Xunit;

namespace RDM.Infrastructure.Tests;

public sealed class InMemoryEventBusTests
{
    private sealed record TestEvent(string Message);
    private sealed record OtherEvent(int Value);

    [Fact]
    public async Task PublishAsync_WithSubscriber_CallsHandler()
    {
        var bus = new InMemoryEventBus();
        var received = new List<TestEvent>();
        bus.Subscribe<TestEvent>(received.Add);
        var evt = new TestEvent("hello");

        await bus.PublishAsync(evt);

        received.Should().ContainSingle().Which.Should().Be(evt);
    }

    [Fact]
    public async Task PublishAsync_AfterUnsubscribe_HandlerNotCalled()
    {
        var bus = new InMemoryEventBus();
        var received = new List<TestEvent>();
        void Handler(TestEvent e) => received.Add(e);
        bus.Subscribe<TestEvent>(Handler);
        bus.Unsubscribe<TestEvent>(Handler);

        await bus.PublishAsync(new TestEvent("hello"));

        received.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_MultipleSubscribers_AllHandlersCalled()
    {
        var bus = new InMemoryEventBus();
        int callCount = 0;
        bus.Subscribe<TestEvent>(_ => callCount++);
        bus.Subscribe<TestEvent>(_ => callCount++);

        await bus.PublishAsync(new TestEvent("hello"));

        callCount.Should().Be(2);
    }

    [Fact]
    public async Task PublishAsync_NoSubscribers_DoesNotThrow()
    {
        var bus = new InMemoryEventBus();

        var act = async () => await bus.PublishAsync(new TestEvent("hello"));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PublishAsync_CancelledToken_ThrowsOperationCanceledException()
    {
        var bus = new InMemoryEventBus();
        bus.Subscribe<TestEvent>(_ => { });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await bus.PublishAsync(new TestEvent("hello"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PublishAsync_DifferentEventTypes_OnlyMatchingHandlerCalled()
    {
        var bus = new InMemoryEventBus();
        var testReceived = new List<TestEvent>();
        var otherReceived = new List<OtherEvent>();
        bus.Subscribe<TestEvent>(testReceived.Add);
        bus.Subscribe<OtherEvent>(otherReceived.Add);

        await bus.PublishAsync(new TestEvent("hello"));

        testReceived.Should().ContainSingle();
        otherReceived.Should().BeEmpty();
    }
}
