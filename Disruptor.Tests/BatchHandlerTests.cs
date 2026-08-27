using Disruptor.Dsl;
using Disruptor.WaitStrategy;
using Xunit;
using Xunit.Abstractions;

namespace Disruptor.Tests;

/// <summary>
/// §2.2 배치 핸들러 API — 이벤트 구간을 span 으로 통째로 받는 경로.
/// </summary>
public class BatchHandlerTests
{
    private readonly ITestOutputHelper _output;

    public BatchHandlerTests(ITestOutputHelper output) => _output = output;

    private sealed class CollectingBatchHandler : IBatchEventHandler<TestEvent>
    {
        private readonly List<long> _seen = new();

        public IReadOnlyList<long> Seen => _seen;
        public int BatchCalls { get; private set; }
        public int MaxSegmentLength { get; private set; }
        public int EndOfBatchCount { get; private set; }
        public long Timeouts { get; private set; }

        public void OnBatch(ReadOnlySpan<TestEvent> events, long startSequence, bool endOfBatch)
        {
            BatchCalls++;
            if (events.Length > MaxSegmentLength)
                MaxSegmentLength = events.Length;

            if (endOfBatch)
                EndOfBatchCount++;

            for (int i = 0; i < events.Length; i++)
            {
                // span 인덱스와 시퀀스가 정확히 대응해야 한다.
                Assert.Equal(startSequence + i, events[i].Sequence);
                _seen.Add(startSequence + i);
            }
        }

        public void OnTimeout(long lastProcessedSequence) => Timeouts++;
    }

    [Theory]
    [InlineData(ProducerType.Single)]
    [InlineData(ProducerType.Multi)]
    public void BatchHandler_ReceivesEveryEvent_ExactlyOnce_InOrder(ProducerType producerType)
    {
        const int count = 50_000;
        const int ringSize = 256; // 작게 잡아 랩 경계를 여러 번 넘게 한다

        var handler = new CollectingBatchHandler();

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), ringSize, producerType, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "batch-handler", AutoClearSlots = false });

        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(20));

        _output.WriteLine($"batchCalls={handler.BatchCalls:N0} maxSegment={handler.MaxSegmentLength}");

        Assert.Equal(count, handler.Seen.Count);
        for (int i = 0; i < count; i++)
            Assert.Equal(i, handler.Seen[i]);

        // 호출 경계가 배치당 1~2회이므로 이벤트 수보다 훨씬 적어야 한다.
        Assert.True(handler.BatchCalls < count, $"배치 호출이 {handler.BatchCalls}회로 이벤트 수와 비슷합니다.");
    }

    [Fact]
    public void BatchHandler_SegmentsNeverCrossWrapBoundary()
    {
        const int ringSize = 64;
        const int count = 1_000;

        var segmentStarts = new List<(long start, int length)>();

        var handler = new DelegateBatchHandler((events, start, eob) =>
            segmentStarts.Add((start, events.Length)));

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), ringSize, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "wrap-test", AutoClearSlots = false });

        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(20));

        // 각 조각은 링의 끝을 넘지 않아야 한다: (startIndex + length) <= ringSize
        foreach (var (start, length) in segmentStarts)
        {
            int startIndex = (int)(start & (ringSize - 1));
            Assert.True(startIndex + length <= ringSize,
                $"조각이 랩 경계를 넘었습니다: start={start} idx={startIndex} len={length}");
        }
    }

    [Fact]
    public void BatchHandler_ExceptionSkipsWholeSegment()
    {
        const int count = 100;
        int errors = 0;

        var handler = new DelegateBatchHandler((events, start, eob) =>
        {
            if (start <= 5 && 5 < start + events.Length)
                throw new InvalidOperationException("의도적 예외");
        });

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), 1024, ProducerType.Single, new BlockingWaitStrategy(),
            new DisruptorOptions { Name = "batch-ex", AutoClearSlots = false });

        disruptor.SetDefaultExceptionHandler(new ResilientExceptionHandler<TestEvent>(
            (ex, seq, evt) => Interlocked.Increment(ref errors)));

        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(10));

        // 프로세스는 살아 있고, 실패한 조각만 스킵되었다.
        Assert.True(errors >= 1);
    }

    [Fact]
    public void ValueBatchHandler_ReceivesWritableSpan()
    {
        const int count = 5_000;

        long sum = 0;
        int seen = 0;

        var handler = new DelegateValueBatchHandler((events, start, eob) =>
        {
            for (int i = 0; i < events.Length; i++)
            {
                Assert.Equal(start + i, events[i].Sequence);
                sum += events[i].Value;
                events[i].Flags = 1;   // 쓰기 가능한 span 인지 확인
                seen++;
            }
        });

        using var disruptor = new ValueDisruptor<TestValueEvent>(
            256, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "value-batch", AutoClearSlots = false });

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

        Assert.Equal(count, seen);
        Assert.Equal(Enumerable.Range(0, count).Sum(i => (long)i), sum);
    }

    [Fact]
    public void BatchHandler_CanFollowEventHandler_InChain()
    {
        const int count = 5_000;

        var upstream = new VerifyingHandler();
        var downstream = new CollectingBatchHandler();

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), 1024, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "mixed-chain", AutoClearSlots = false });

        disruptor.HandleEventsWith(upstream).Then(downstream);
        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(20));

        Assert.Equal(count, upstream.Count);
        Assert.Equal(count, downstream.Seen.Count);
    }

    private sealed class DelegateBatchHandler : IBatchEventHandler<TestEvent>
    {
        public delegate void BatchAction(ReadOnlySpan<TestEvent> events, long start, bool endOfBatch);

        private readonly BatchAction _action;

        public DelegateBatchHandler(BatchAction action) => _action = action;

        public void OnBatch(ReadOnlySpan<TestEvent> events, long startSequence, bool endOfBatch)
            => _action(events, startSequence, endOfBatch);
    }

    private sealed class DelegateValueBatchHandler : IValueBatchEventHandler<TestValueEvent>
    {
        public delegate void BatchAction(Span<TestValueEvent> events, long start, bool endOfBatch);

        private readonly BatchAction _action;

        public DelegateValueBatchHandler(BatchAction action) => _action = action;

        public void OnBatch(Span<TestValueEvent> events, long startSequence, bool endOfBatch)
            => _action(events, startSequence, endOfBatch);
    }
}

/// <summary>
/// §2.7 MaxBatchSize — 배치 상한 = 지연 상한.
/// </summary>
public class MaxBatchSizeTests
{
    [Fact]
    public void MaxBatchSize_CapsBatches_SoTimeoutHookStillRuns()
    {
        const int count = 20_000;
        const int maxBatch = 64;

        var handler = new VerifyingHandler();

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), 8192, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions
            {
                Name = "maxbatch",
                AutoClearSlots = false,
                MaxBatchSize = maxBatch,
            });

        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(20));

        Assert.Equal(count, handler.Count);
        Assert.Equal(0, handler.OutOfOrder);
        Assert.True(handler.MaxBatchSize <= maxBatch,
            $"배치 상한 {maxBatch} 를 초과했습니다: {handler.MaxBatchSize}");
    }

    [Fact]
    public void MaxBatchSize_Unlimited_ByDefault()
    {
        var options = DisruptorOptions.Default;
        Assert.Equal(int.MaxValue, options.MaxBatchSize);
    }
}
