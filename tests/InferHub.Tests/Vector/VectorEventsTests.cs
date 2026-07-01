using InferHub.Coordinator.Observability;

namespace InferHub.Tests.Vector;

public class VectorEventsTests
{
    [Fact]
    public void SubscribersReceivePublishedEventsInOrder()
    {
        var bus = new VectorEvents();
        var received = new List<VectorEvent>();
        using var sub = bus.Subscribe(received.Add);

        bus.Publish("vector.collection.created", "docs", new Dictionary<string, object?> { ["dimension"] = 2 });
        bus.Publish("vector.heal.started", "docs", new Dictionary<string, object?> { ["reason"] = "under-target" });

        Assert.Equal(2, received.Count);
        Assert.Equal("vector.collection.created", received[0].Kind);
        Assert.Equal("docs", received[0].Collection);
        Assert.Equal(2, (int)received[0].Data["dimension"]!);
        Assert.Equal("vector.heal.started", received[1].Kind);
        Assert.True(received[1].Sequence > received[0].Sequence);
    }

    [Fact]
    public void DisposedSubscribersStopReceivingEvents()
    {
        var bus = new VectorEvents();
        var received = new List<VectorEvent>();
        var sub = bus.Subscribe(received.Add);

        bus.Publish("vector.replica.assigned", "docs");
        sub.Dispose();
        bus.Publish("vector.replica.assigned", "docs");

        Assert.Single(received);
    }

    [Fact]
    public void ThrowingSubscriberDoesNotAbortOtherSubscribers()
    {
        var bus = new VectorEvents();
        var seenB = 0;
        using var subA = bus.Subscribe(_ => throw new InvalidOperationException("boom"));
        using var subB = bus.Subscribe(_ => Interlocked.Increment(ref seenB));

        bus.Publish("vector.heal.completed", "docs");

        Assert.Equal(1, seenB);
    }
}
