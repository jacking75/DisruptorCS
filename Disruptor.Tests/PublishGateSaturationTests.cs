using Disruptor.WaitStrategy;
using Xunit;

namespace Disruptor.Tests;

public class PublishGateSaturationTests
{
    [Theory]
    [InlineData(ProducerType.Single)]
    [InlineData(ProducerType.Multi)]
    public void DropPolicy_NeverWaitsForConsumerCapacity_EvenAtFullThreshold(ProducerType producerType)
    {
        var ring = RingBuffer<TestEvent>.Create(producerType, () => new(), 4, new BlockingWaitStrategy());
        using var poller = ring.NewPoller();
        var gate = new PublishGate<TestEvent>(ring) { CongestionThreshold = 1.0 };
        for (int i = 0; i < 4; i++) ring.Publish(ring.Next());

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        int fills = 0;
        bool published = gate.TryPublish(BackpressurePolicy.DropWhenCongested,
            (_, _) => fills++, cancellationToken: deadline.Token);

        Assert.False(published);
        Assert.Equal(0, fills);
        Assert.Equal(1, gate.DroppedCount);
        Assert.Equal(0, gate.PublishedCount);
        Assert.Equal(3, ring.Cursor);

        poller.Poll((_, _, _) => true);
        Assert.True(gate.TryPublish(BackpressurePolicy.DropWhenCongested, (_, _) => fills++));
        Assert.Equal(1, fills);
        Assert.Equal(1, gate.PublishedCount);
    }
}
