using System.Diagnostics;
using Disruptor.Async;
using Disruptor.WaitStrategy;
using Xunit;
using Xunit.Abstractions;

namespace Disruptor.Tests;

/// <summary>
/// §4.3 Async 브리지 — .NET 생태계 기대치 충족을 위한 통합 경로.
/// </summary>
public class AsyncTests
{
    private readonly ITestOutputHelper _output;

    public AsyncTests(ITestOutputHelper output) => _output = output;

    private sealed class CollectingAsyncHandler : IAsyncBatchEventHandler<TestEvent>
    {
        private readonly List<long> _seen = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly long _expected;

        public CollectingAsyncHandler(long expected) => _expected = expected;

        public IReadOnlyList<long> Seen => _seen;
        public Task Completion => _completion.Task;
        public int BatchCalls { get; private set; }
        public int Starts { get; private set; }
        public int Shutdowns { get; private set; }

        public ValueTask OnStartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            return ValueTask.CompletedTask;
        }

        public ValueTask OnShutdownAsync()
        {
            Shutdowns++;
            return ValueTask.CompletedTask;
        }

        public async ValueTask OnBatchAsync(RingBuffer<TestEvent> ringBuffer, long from, long to,
                                            CancellationToken cancellationToken)
        {
            BatchCalls++;

            for (long s = from; s <= to; s++)
            {
                Assert.Equal(s, ringBuffer[s].Sequence);
                _seen.Add(s);
            }

            // 실제 async 경계를 만들어 순서 보장을 검증한다.
            await Task.Yield();

            if (_seen.Count >= _expected)
                _completion.TrySetResult();
        }
    }

    [Theory]
    [InlineData(ProducerType.Single)]
    [InlineData(ProducerType.Multi)]
    public async Task AsyncProcessor_ConsumesAllEvents_InOrder(ProducerType producerType)
    {
        const int count = 20_000;

        var strategy = new AsyncWaitStrategy(TimeSpan.FromMilliseconds(50));
        var ring = RingBuffer<TestEvent>.Create(
            producerType, () => new TestEvent(), 1024, strategy);

        var barrier = ring.NewBarrier();
        var handler = new CollectingAsyncHandler(count);
        using var processor = new AsyncBatchEventProcessor<TestEvent>(ring, barrier, handler);
        ring.AddGatingSequences(processor.Sequence);

        using var cts = new CancellationTokenSource();
        Task loop = processor.RunAsync(cts.Token);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        await handler.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        sw.Stop();

        cts.Cancel();
        processor.Halt();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        _output.WriteLine($"count={handler.Seen.Count:N0} batches={handler.BatchCalls:N0} " +
                          $"elapsed={sw.Elapsed.TotalMilliseconds:F0}ms");

        Assert.Equal(count, handler.Seen.Count);
        for (int i = 0; i < count; i++)
            Assert.Equal(i, handler.Seen[i]);

        Assert.Equal(1, handler.Starts);
        Assert.Equal(1, handler.Shutdowns);
    }

    [Fact]
    public async Task AsyncWaitStrategy_CompletesWaiters_OnPublish()
    {
        var strategy = new AsyncWaitStrategy(TimeSpan.FromSeconds(5));
        var cursor = new Sequence();
        var dependents = new DependentSequenceGroup(cursor);

        ValueTask<SequenceWaitResult> pending = strategy.WaitForAsync(0, dependents, CancellationToken.None);
        Assert.False(pending.IsCompleted);

        cursor.SetValue(0);
        strategy.SignalAllWhenBlocking();

        SequenceWaitResult result = await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.IsTimeout);
        Assert.False(result.IsCancelled);
        Assert.Equal(0, result.AvailableSequence);
    }

    [Fact]
    public async Task AsyncWaitStrategy_ReturnsImmediately_WhenAlreadyAvailable()
    {
        var strategy = new AsyncWaitStrategy();
        var cursor = new Sequence(42);
        var dependents = new DependentSequenceGroup(cursor);

        ValueTask<SequenceWaitResult> task = strategy.WaitForAsync(10, dependents, CancellationToken.None);

        Assert.True(task.IsCompleted); // 할당 없는 빠른 경로
        Assert.Equal(42, (await task).AvailableSequence);
    }

    [Fact]
    public async Task AsyncWaitStrategy_HonorsCancellation()
    {
        var strategy = new AsyncWaitStrategy(TimeSpan.FromMilliseconds(20));
        var cursor = new Sequence();
        var dependents = new DependentSequenceGroup(cursor);

        using var cts = new CancellationTokenSource();
        ValueTask<SequenceWaitResult> pending = strategy.WaitForAsync(100, dependents, cts.Token);

        cts.Cancel();
        strategy.SignalAllWhenBlocking();

        SequenceWaitResult result = await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsCancelled);
    }

    [Fact]
    public void AsyncWaitStrategy_SupportsSynchronousProcessors_Too()
    {
        // 동기 경로도 제공하므로 같은 파이프라인에서 혼용할 수 있어야 한다.
        var strategy = new AsyncWaitStrategy(TimeSpan.FromMilliseconds(20));
        var cursor = new Sequence(5);
        var dependents = new DependentSequenceGroup(cursor);

        SequenceWaitResult result = strategy.WaitFor(3, dependents, CancellationToken.None);
        Assert.Equal(5, result.AvailableSequence);
    }

    [Fact]
    public async Task ReadAllAsync_StreamsSnapshots()
    {
        const int count = 500;

        var ring = RingBuffer<TestEvent>.CreateSingleProducer(
            () => new TestEvent(), 1024, new YieldingWaitStrategy());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var received = new List<long>();
        Task consumer = Task.Run(async () =>
        {
            await foreach (long value in ring.ReadAllAsync(
                               static e => e.Sequence,
                               TimeSpan.FromMilliseconds(1),
                               cancellationToken: cts.Token))
            {
                received.Add(value);
                if (received.Count >= count)
                    break;
            }
        }, cts.Token);

        // 소비자가 폴러를 등록할 시간을 준다 (폴러는 생성 시점 이후 이벤트만 본다).
        await Task.Delay(100, cts.Token);

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        await consumer.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(count, received.Count);
        for (int i = 0; i < count; i++)
            Assert.Equal(i, received[i]);
    }
}
