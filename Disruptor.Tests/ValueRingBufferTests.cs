using Disruptor.Dsl;
using Disruptor.Work;
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

/// <summary>
/// §4.9 값 타입 워커 풀 — 참조 타입과의 기능 격차를 메운 부분.
/// </summary>
public class ValueWorkerPoolTests
{
    private sealed class CountingValueWorker : IValueWorkHandler<TestValueEvent>
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<long> _seen;

        public CountingValueWorker(System.Collections.Concurrent.ConcurrentBag<long> seen) => _seen = seen;

        public int Handled { get; private set; }

        public void OnEvent(ref TestValueEvent evt)
        {
            _seen.Add(evt.Sequence);
            Handled++;
        }
    }

    [Fact]
    public void ValueWorkerPool_ProcessesEachEventExactlyOnce()
    {
        const int count = 20_000;
        const int workers = 4;

        var seen = new System.Collections.Concurrent.ConcurrentBag<long>();
        var handlers = new CountingValueWorker[workers];

        using var disruptor = new ValueDisruptor<TestValueEvent>(
            1024, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "value-worker", AutoClearSlots = false });

        disruptor.HandleEventsWithWorkerPool(workers, i =>
        {
            handlers[i] = new CountingValueWorker(seen);
            return handlers[i];
        });

        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(20));

        Assert.Equal(count, seen.Count);           // 중복 없음
        Assert.Equal(count, seen.Distinct().Count()); // 유실 없음
        Assert.True(handlers.Count(h => h.Handled > 0) >= 2, "이벤트가 한 워커에만 몰렸습니다.");
    }

    [Fact]
    public void ValueWorkerPool_AfterHandlerChain_SeesEventsOnlyAfterUpstream()
    {
        const int count = 5_000;

        var seen = new System.Collections.Concurrent.ConcurrentBag<long>();
        var upstream = new CountingValueHandler();

        using var disruptor = new ValueDisruptor<TestValueEvent>(
            1024, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "value-worker-chain", AutoClearSlots = false });

        disruptor.HandleEventsWith(upstream)
                 .ThenWorkerPool(3, i => new CountingValueWorker(seen));

        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(20));

        Assert.Equal(count, upstream.Count);
        Assert.Equal(count, seen.Distinct().Count());
    }

    private sealed class CountingValueHandler : IValueEventHandler<TestValueEvent>
    {
        private long _count;
        public long Count => Volatile.Read(ref _count);
        public void OnEvent(ref TestValueEvent data, long sequence, bool endOfBatch)
            => Interlocked.Increment(ref _count);
    }
}

/// <summary>
/// §2.8 고정 객체 힙(POH) 옵션.
/// </summary>
public class PinnedObjectHeapTests
{
    [Fact]
    public void PinnedRingBuffer_BehavesIdentically()
    {
        const int count = 10_000;

        var options = new SequencerOptions { PipelineName = "poh", UsePinnedObjectHeap = true };

        var ring = RingBuffer<TestEvent>.Create(
            ProducerType.Single, () => new TestEvent(), 1024, new YieldingWaitStrategy(), options);

        var barrier = ring.NewBarrier();
        var handler = new VerifyingHandler();
        using var processor = new BatchEventProcessor<TestEvent>(ring, barrier, handler);
        ring.AddGatingSequences(processor.Sequence);

        using var cts = new CancellationTokenSource();
        var thread = new Thread(() => processor.Run(cts.Token)) { IsBackground = true };
        thread.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (handler.Count < count && sw.Elapsed < TimeSpan.FromSeconds(20))
            Thread.Sleep(5);

        cts.Cancel();
        processor.Halt();
        thread.Join(TimeSpan.FromSeconds(2));

        Assert.Equal(count, handler.Count);
        Assert.Equal(0, handler.Mismatches);
        Assert.Equal(0, handler.OutOfOrder);
    }

    [Fact]
    public void PinnedValueRingBuffer_BehavesIdentically()
    {
        var options = new SequencerOptions { PipelineName = "poh-value", UsePinnedObjectHeap = true };

        var ring = ValueRingBuffer<TestValueEvent>.Create(
            ProducerType.Single, 256, new YieldingWaitStrategy(), options: options);

        long seq = ring.Next();
        ring[seq].Value = 42;
        ring.Publish(seq);

        Assert.Equal(42, ring[seq].Value);
        Assert.Equal(256, ring.BufferSize);
    }
}
