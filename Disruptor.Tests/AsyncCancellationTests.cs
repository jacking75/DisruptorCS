using Disruptor.Async;
using Xunit;

namespace Disruptor.Tests;

public class AsyncCancellationTests
{
    [Fact]
    public async Task CancellationCompletesWait_WithoutPublishOrExplicitSignal()
    {
        var strategy = new AsyncWaitStrategy();
        using var cancellation = new CancellationTokenSource();
        var pending = strategy.WaitForAsync(0, new DependentSequenceGroup(new Sequence()), cancellation.Token).AsTask();
        try
        {
            cancellation.Cancel();
            Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(2))).IsCancelled);
        }
        finally
        {
            // Also clean up the old implementation when this regression test fails.
            strategy.SignalAllWhenBlocking();
            await pending.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task IdleProcessor_StopsOnExternalCancellation_WithoutHalt()
    {
        var ring = RingBuffer<TestEvent>.CreateMultiProducer(() => new(), 16, new AsyncWaitStrategy());
        var handler = new IdleHandler();
        using var processor = new AsyncBatchEventProcessor<TestEvent>(ring, ring.NewBarrier(), handler);
        using var cancellation = new CancellationTokenSource();
        Task run = processor.RunAsync(cancellation.Token);
        try
        {
            cancellation.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(processor.IsRunning);
            Assert.Equal(1, handler.Shutdowns);
        }
        finally
        {
            processor.Halt();
            await run.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task CancellationRacingPublication_CompletesEachWaitExactlyOnce()
    {
        var strategy = new AsyncWaitStrategy();
        var cursor = new Sequence();
        var dependents = new DependentSequenceGroup(cursor);
        for (int i = 0; i < 200; i++)
        {
            using var cancellation = new CancellationTokenSource();
            var pending = strategy.WaitForAsync(i, dependents, cancellation.Token).AsTask();
            int sequence = i;
            await Task.WhenAll(Task.Run(cancellation.Cancel), Task.Run(() =>
            {
                cursor.SetValue(sequence);
                strategy.SignalAllWhenBlocking();
            }));
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(result.IsCancelled || result.AvailableSequence >= sequence);
        }
    }

    private sealed class IdleHandler : IAsyncBatchEventHandler<TestEvent>
    {
        public int Shutdowns;
        public ValueTask OnBatchAsync(RingBuffer<TestEvent> ringBuffer, long fromSequence, long toSequence,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask OnShutdownAsync() { Shutdowns++; return ValueTask.CompletedTask; }
    }
}
