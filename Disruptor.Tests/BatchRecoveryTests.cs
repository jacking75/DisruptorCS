using Disruptor.WaitStrategy;
using Xunit;

namespace Disruptor.Tests;

public class BatchRecoveryTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ConsecutiveFailures_AreIndependentOfBatchBoundaries(bool valueType, bool successesBetween, bool batch)
    {
        var handler = new FailurePatternHandler(successesBetween);
        var errors = new List<long>();
        IEventProcessor processor;
        if (valueType)
        {
            var ring = ValueRingBuffer<ValueSlot>.CreateSingleProducer(4, new BlockingWaitStrategy());
            ring.Publish(0, ring.Next(4));
            var barrier = new FiniteBarrier(ring.NewBarrier(), 3);
            var p = batch
                ? new ValueBatchEventProcessor<ValueSlot>(ring, barrier, (IValueBatchEventHandler<ValueSlot>)handler)
                : new ValueBatchEventProcessor<ValueSlot>(ring, barrier, (IValueEventHandler<ValueSlot>)handler);
            p.MaxBatchSize = successesBetween && !batch ? 4 : 1;
            p.SetExceptionHandler(new ResilientExceptionHandler<ValueSlot>((_, sequence, _) => errors.Add(sequence),
                maxConsecutiveFailures: successesBetween ? 2 : 3));
            processor = p;
        }
        else
        {
            var ring = RingBuffer<Slot>.CreateSingleProducer(() => new(), 4, new BlockingWaitStrategy());
            ring.Publish(0, ring.Next(4));
            var barrier = new FiniteBarrier(ring.NewBarrier(), 3);
            var p = batch
                ? new BatchEventProcessor<Slot>(ring, barrier, (IBatchEventHandler<Slot>)handler)
                : new BatchEventProcessor<Slot>(ring, barrier, (IEventHandler<Slot>)handler);
            p.MaxBatchSize = successesBetween && !batch ? 4 : 1;
            p.SetExceptionHandler(new ResilientExceptionHandler<Slot>((_, sequence, _) => errors.Add(sequence),
                maxConsecutiveFailures: successesBetween ? 2 : 3));
            processor = p;
        }
        using var lifetime = (IDisposable)processor;
        processor.Run(CancellationToken.None);
        Assert.Equal(successesBetween ? new long[] { 0, 2 } : [0, 1, 2], errors);
        Assert.Equal(successesBetween ? 3 : 1, processor.Sequence.Value);
        Assert.Equal(successesBetween ? 2 : 0, handler.Successes);
    }

    [Fact]
    public void BatchCleanupFailure_HaltsWithoutRetryingBusinessSideEffects()
    {
        var handler = new RecordingBatchHandler(failedStart: -1);
        var errors = new List<long>();
        using var fixture = new WrappedBatch(false, handler, EventExceptionAction.Retry, errors,
            clear: true, throwDuringClear: true);
        fixture.Processor.Run(CancellationToken.None);
        Assert.Equal(new long[] { 2, 3 }, handler.Seen);
        Assert.Equal(new long[] { 3 }, errors);
        Assert.Equal(2, fixture.Processor.Sequence.Value);
        Assert.Equal(1, fixture.HaltCount);
    }

    [Theory]
    [InlineData(false, 2, EventExceptionAction.Skip)]
    [InlineData(true, 2, EventExceptionAction.Skip)]
    [InlineData(false, 4, EventExceptionAction.Retry)]
    [InlineData(true, 4, EventExceptionAction.Retry)]
    [InlineData(false, 4, EventExceptionAction.Halt)]
    [InlineData(true, 4, EventExceptionAction.Halt)]
    public void WrappedBatch_RecoveryStartsAtTheFailedSegment(bool valueType, long failedStart,
        EventExceptionAction action)
    {
        var handler = new RecordingBatchHandler(failedStart);
        var errors = new List<long>();
        using var fixture = new WrappedBatch(valueType, handler, action, errors, clear: false);
        fixture.Processor.Run(CancellationToken.None);

        Assert.Equal(new[] { failedStart }, errors);
        long[] expected = action switch
        {
            EventExceptionAction.Skip => [4, 5],
            EventExceptionAction.Retry => [2, 3, 4, 5],
            _ => [2, 3],
        };
        Assert.Equal(expected, handler.Seen);
        Assert.Equal(action == EventExceptionAction.Halt ? 3 : 5, fixture.Processor.Sequence.Value);
        Assert.Equal(action == EventExceptionAction.Halt ? 1 : 0, fixture.HaltCount);
    }

    [Theory]
    [InlineData(false, EventExceptionAction.Skip)]
    [InlineData(true, EventExceptionAction.Skip)]
    [InlineData(false, EventExceptionAction.Retry)]
    [InlineData(true, EventExceptionAction.Retry)]
    public void WrappedBatch_ClearsCompletedOrSkippedSlots_WithoutClearingBeforeRetry(
        bool valueType, EventExceptionAction action)
    {
        var handler = new RecordingBatchHandler(failedStart: 4);
        using var fixture = new WrappedBatch(valueType, handler, action, [], clear: true);
        fixture.Processor.Run(CancellationToken.None);

        Assert.False(handler.SawClearedPayload);
        Assert.Equal(5, fixture.Processor.Sequence.Value);
        for (long sequence = 2; sequence <= 5; sequence++)
            fixture.AssertCleared(sequence);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Retry_ObservesCancellation_WithoutAnotherHandlerCall(bool valueType, bool batch)
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new AlwaysFailingHandler();
        int errors = 0, halts = 0;
        EventExceptionAction OnError(long _)
        {
            cancellation.Cancel();
            // A fallback makes the broken implementation terminate too, without a stuck test thread.
            return ++errors == 1 ? EventExceptionAction.Retry : EventExceptionAction.Halt;
        }

        IEventProcessor processor;
        if (valueType)
        {
            var ring = ValueRingBuffer<ValueSlot>.CreateSingleProducer(4, new BlockingWaitStrategy());
            ring.Publish(ring.Next());
            var p = batch
                ? new ValueBatchEventProcessor<ValueSlot>(ring, ring.NewBarrier(), (IValueBatchEventHandler<ValueSlot>)handler)
                : new ValueBatchEventProcessor<ValueSlot>(ring, ring.NewBarrier(), (IValueEventHandler<ValueSlot>)handler);
            p.SetExceptionHandler(new Policy<ValueSlot>(OnError));
            p.Halted += (_, _) => halts++;
            processor = p;
        }
        else
        {
            var ring = RingBuffer<Slot>.CreateSingleProducer(() => new(), 4, new BlockingWaitStrategy());
            ring.Publish(ring.Next());
            var p = batch
                ? new BatchEventProcessor<Slot>(ring, ring.NewBarrier(), (IBatchEventHandler<Slot>)handler)
                : new BatchEventProcessor<Slot>(ring, ring.NewBarrier(), (IEventHandler<Slot>)handler);
            p.SetExceptionHandler(new Policy<Slot>(OnError));
            p.Halted += (_, _) => halts++;
            processor = p;
        }

        using var lifetime = (IDisposable)processor;
        processor.Run(cancellation.Token);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, errors);
        Assert.Equal(0, halts);
        Assert.Equal(-1, processor.Sequence.Value);
    }

    // Stops at a known published boundary, removing scheduler timing from wrap/error tests.
    private sealed class FiniteBarrier(ISequenceBarrier inner, long last) : ISequenceBarrier
    {
        public SequenceWaitResult WaitFor(long sequence, CancellationToken cancellationToken)
            => sequence > last ? SequenceWaitResult.Cancelled : inner.WaitFor(sequence, cancellationToken);
        public long Cursor => inner.Cursor;
        public DependentSequenceGroup Dependents => inner.Dependents;
        public IWaitStrategy WaitStrategy => inner.WaitStrategy;
    }

    private sealed class WrappedBatch : IDisposable
    {
        public IEventProcessor Processor { get; }
        public Action<long> AssertCleared { get; }
        public int HaltCount;

        public WrappedBatch(bool valueType, RecordingBatchHandler handler, EventExceptionAction action,
            List<long> errors, bool clear, bool throwDuringClear = false)
        {
            EventExceptionAction OnError(long sequence) { errors.Add(sequence); return action; }
            if (valueType)
            {
                var ring = ValueRingBuffer<ValueSlot>.CreateSingleProducer(4, new BlockingWaitStrategy());
                // Advance two slots, then publish a full ring spanning [2,3] and [4,5].
                ring.Publish(0, ring.Next(2));
                var p = new ValueBatchEventProcessor<ValueSlot>(ring, new FiniteBarrier(ring.NewBarrier(), 5), handler)
                { ClearAfterProcess = clear };
                p.Sequence.SetValue(1);
                ring.AddGatingSequences(p.Sequence);
                for (int i = 0; i < 4; i++)
                {
                    long sequence = ring.Next();
                    try { ring[sequence] = new ValueSlot { Sequence = sequence, Payload = [1] }; }
                    finally { ring.Publish(sequence); }
                }
                p.SetExceptionHandler(new Policy<ValueSlot>(OnError));
                p.Halted += (_, _) => HaltCount++;
                Processor = p;
                AssertCleared = sequence => Assert.Null(ring[sequence].Payload);
            }
            else
            {
                var ring = RingBuffer<Slot>.CreateSingleProducer(() => new(), 4, new BlockingWaitStrategy());
                ring.Publish(0, ring.Next(2));
                var p = new BatchEventProcessor<Slot>(ring, new FiniteBarrier(ring.NewBarrier(), 5), handler)
                { ClearAfterProcess = clear };
                p.Sequence.SetValue(1);
                ring.AddGatingSequences(p.Sequence);
                for (int i = 0; i < 4; i++)
                {
                    long sequence = ring.Next();
                    try
                    {
                        ring[sequence].Sequence = sequence;
                        ring[sequence].Payload = [1];
                        ring[sequence].ThrowDuringClear = throwDuringClear && sequence == 3;
                    }
                    finally { ring.Publish(sequence); }
                }
                p.SetExceptionHandler(new Policy<Slot>(OnError));
                p.Halted += (_, _) => HaltCount++;
                Processor = p;
                AssertCleared = sequence =>
                {
                    Assert.Null(ring[sequence].Payload);
                    Assert.Equal(1, ring[sequence].ClearCount);
                };
            }
        }
        public void Dispose() => ((IDisposable)Processor).Dispose();
    }

    private sealed class Slot : IClearable
    {
        public long Sequence;
        public byte[]? Payload;
        public int ClearCount;
        public bool ThrowDuringClear;
        public void Clear()
        {
            Payload = null;
            ClearCount++;
            if (ThrowDuringClear) throw new InvalidOperationException("Cleanup failed after returning a buffer");
        }
    }
    private struct ValueSlot { public long Sequence; public byte[]? Payload; }

    private sealed class RecordingBatchHandler(long failedStart) : IBatchEventHandler<Slot>, IValueBatchEventHandler<ValueSlot>
    {
        public readonly List<long> Seen = [];
        public bool SawClearedPayload;
        private bool _failed;
        private void FailOnce(long start)
        {
            if (start != failedStart || _failed) return;
            _failed = true;
            throw new InvalidOperationException("Injected segment failure");
        }
        public void OnBatch(ReadOnlySpan<Slot> events, long startSequence, bool endOfBatch)
        {
            foreach (var evt in events) SawClearedPayload |= evt.Payload is null;
            FailOnce(startSequence);
            foreach (var evt in events) Seen.Add(evt.Sequence);
        }
        public void OnBatch(Span<ValueSlot> events, long startSequence, bool endOfBatch)
        {
            foreach (var evt in events) SawClearedPayload |= evt.Payload is null;
            FailOnce(startSequence);
            foreach (var evt in events) Seen.Add(evt.Sequence);
        }
    }

    private sealed class AlwaysFailingHandler : IEventHandler<Slot>, IValueEventHandler<ValueSlot>,
        IBatchEventHandler<Slot>, IValueBatchEventHandler<ValueSlot>
    {
        public int Calls;
        private void Fail() { Calls++; throw new InvalidOperationException("Persistent failure"); }
        public void OnEvent(Slot data, long sequence, bool endOfBatch) => Fail();
        public void OnEvent(ref ValueSlot data, long sequence, bool endOfBatch) => Fail();
        public void OnBatch(ReadOnlySpan<Slot> events, long startSequence, bool endOfBatch) => Fail();
        public void OnBatch(Span<ValueSlot> events, long startSequence, bool endOfBatch) => Fail();
    }
    private sealed class FailurePatternHandler(bool successesBetween) : IEventHandler<Slot>, IValueEventHandler<ValueSlot>,
        IBatchEventHandler<Slot>, IValueBatchEventHandler<ValueSlot>
    {
        public int Successes;
        private void Handle(long sequence)
        {
            if (!successesBetween || sequence % 2 == 0) throw new InvalidOperationException("Invalid input");
            Successes++;
        }
        public void OnEvent(Slot data, long sequence, bool endOfBatch) => Handle(sequence);
        public void OnEvent(ref ValueSlot data, long sequence, bool endOfBatch) => Handle(sequence);
        public void OnBatch(ReadOnlySpan<Slot> events, long startSequence, bool endOfBatch) => Handle(startSequence);
        public void OnBatch(Span<ValueSlot> events, long startSequence, bool endOfBatch) => Handle(startSequence);
    }
    private sealed class Policy<T>(Func<long, EventExceptionAction> onError) : IExceptionHandler<T>
    {
        public EventExceptionAction HandleEventException(Exception ex, long sequence, T? evt) => onError(sequence);
        public void HandleOnStartException(Exception ex) => throw ex;
        public void HandleOnShutdownException(Exception ex) => throw ex;
    }
}
