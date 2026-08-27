using Disruptor.Dsl;
using Disruptor.WaitStrategy;
using Xunit;

namespace Disruptor.Tests;

public class ValueRingBufferTests
{
    private sealed class ValueVerifier : IValueEventHandler<TestValueEvent>
    {
        private long _count;
        private long _mismatches;

        public long Count => Volatile.Read(ref _count);
        public long Mismatches => Volatile.Read(ref _mismatches);

        public void OnEvent(ref TestValueEvent data, long sequence, bool endOfBatch)
        {
            if (data.Sequence != sequence)
                Interlocked.Increment(ref _mismatches);

            Interlocked.Increment(ref _count);
        }
    }

    [Theory]
    [InlineData(ProducerType.Single)]
    [InlineData(ProducerType.Multi)]
    public void ValueRingBuffer_DeliversAllEvents(ProducerType producerType)
    {
        const int count = 100_000;

        var handler = new ValueVerifier();

        using var disruptor = new ValueDisruptor<TestValueEvent>(
            1024, producerType, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "value-test", AutoClearSlots = false });

        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring[seq].Value = i;
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(20));

        Assert.Equal(count, handler.Count);
        Assert.Equal(0, handler.Mismatches);
    }

    [Fact]
    public void Indexer_ReturnsReference_NotCopy()
    {
        var ring = ValueRingBuffer<TestValueEvent>.CreateSingleProducer(64, new YieldingWaitStrategy());

        long seq = ring.Next();
        ring[seq].Value = 42;
        ring.Publish(seq);

        // ref 반환이 아니라 복사였다면 값이 유지되지 않는다.
        Assert.Equal(42, ring[seq].Value);
    }

    [Fact]
    public void PublishEventScope_PublishesOnDispose()
    {
        var ring = ValueRingBuffer<TestValueEvent>.CreateSingleProducer(64, new YieldingWaitStrategy());
        var gating = new Sequence();
        ring.AddGatingSequences(gating);

        long sequence;
        using (var scope = ring.PublishEventScope())
        {
            sequence = scope.Sequence;
            scope.Event.Value = 7;
            Assert.False(ring.IsPublished(sequence));
        }

        Assert.True(ring.IsPublished(sequence));
        Assert.Equal(7, ring[sequence].Value);
    }

    [Fact]
    public void Backpressure_AppliesToValueRingBuffer()
    {
        var ring = ValueRingBuffer<TestValueEvent>.CreateMultiProducer(8, new YieldingWaitStrategy());
        var gating = new Sequence();
        ring.AddGatingSequences(gating);

        for (int i = 0; i < 8; i++)
            ring.Publish(ring.Next());

        Assert.False(ring.TryNext(out _));

        var blocked = BlockingCall.Start(() => ring.Next());
        Assert.False(blocked.CompletedWithin(TimeSpan.FromMilliseconds(300)),
            "값 타입 링 버퍼에서도 백프레셔가 동작해야 합니다.");

        // 스핀 중인 스레드를 반드시 회수한다.
        // 남겨두면 이후 모든 테스트 동안 코어 하나를 태워 CPU 예산 테스트가 흔들린다.
        gating.SetValue(0);
        Assert.True(blocked.CompletedWithin(TimeSpan.FromSeconds(5)));
    }
}

public class PublishGateTests
{
    [Fact]
    public void DropWhenCongested_DropsInsteadOfBlocking()
    {
        var ring = RingBuffer<TestEvent>.CreateSingleProducer(
            () => new TestEvent(), 16, new YieldingWaitStrategy());
        ring.AddGatingSequences(new Sequence());

        var gate = new PublishGate<TestEvent>(ring) { CongestionThreshold = 0.5 };

        int published = 0;
        int dropped = 0;

        for (int i = 0; i < 32; i++)
        {
            bool ok = gate.TryPublish(BackpressurePolicy.DropWhenCongested,
                                      (e, s) => e.Sequence = s);
            if (ok) published++;
            else dropped++;
        }

        Assert.True(published > 0);
        Assert.True(dropped > 0, "혼잡 상태인데 드롭이 한 건도 발생하지 않았습니다.");
        Assert.Equal(dropped, gate.DroppedCount);
        Assert.Equal(published, gate.PublishedCount);
    }

    [Fact]
    public void FailAfterTimeout_RejectsWhenFull()
    {
        var ring = RingBuffer<TestEvent>.CreateSingleProducer(
            () => new TestEvent(), 8, new YieldingWaitStrategy());
        ring.AddGatingSequences(new Sequence());

        var gate = new PublishGate<TestEvent>(ring);

        for (int i = 0; i < 8; i++)
            Assert.True(gate.TryPublish(BackpressurePolicy.FailAfterTimeout, (e, s) => e.Sequence = s));

        Assert.False(gate.TryPublish(BackpressurePolicy.FailAfterTimeout, (e, s) => e.Sequence = s,
                                     timeout: TimeSpan.FromMilliseconds(50)));
        Assert.Equal(1, gate.RejectedCount);
    }

    [Fact]
    public void Fill_Throwing_StillPublishes_SoRingDoesNotJam()
    {
        var ring = RingBuffer<TestEvent>.CreateSingleProducer(
            () => new TestEvent(), 8, new YieldingWaitStrategy());
        var gating = new Sequence();
        ring.AddGatingSequences(gating);

        var gate = new PublishGate<TestEvent>(ring);

        Assert.Throws<InvalidOperationException>(() =>
            gate.TryPublish(BackpressurePolicy.Block, (_, _) => throw new InvalidOperationException()));

        // 예외가 났어도 시퀀스는 게시되어야 링 버퍼가 막히지 않는다.
        Assert.True(ring.IsPublished(0));
        Assert.Equal(0, ring.Cursor);
    }
}
