using Disruptor;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;

namespace UsageRecipes;

internal static class BatchAggregation
{
    public static void Run()
    {
        const int count = 1_000;
        var handler = new AggregateHandler();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipeline = new Disruptor<Measurement>(
            () => new(), 64, ProducerType.Single, new BlockingWaitStrategy(),
            new DisruptorOptions { MaxBatchSize = 16 });
        pipeline.HandleEventsWith(handler);
        var ring = pipeline.Start();
        for (int i = 1; i <= count; i++)
        {
            long sequence = ring.Next(deadline.Token);
            try { ring[sequence].Value = i; }
            finally { ring.Publish(sequence); }
        }
        pipeline.Shutdown(TimeSpan.FromSeconds(5));
        Check.That(handler.Count == count && handler.Sum == 500_500
                   && handler.MaxSegment <= 16 && handler.Flushes > 0, "Aggregation result differs.");
        Console.WriteLine($"Events={handler.Count}, sum={handler.Sum}, span calls={handler.Calls}, flushes={handler.Flushes}, max span={handler.MaxSegment}");
    }

    private sealed class Measurement { public int Value; }
    private sealed class AggregateHandler : IBatchEventHandler<Measurement>
    {
        public int Count, Calls, Flushes, MaxSegment;
        public long Sum;
        private long _pendingSum;
        public void OnBatch(ReadOnlySpan<Measurement> events, long startSequence, bool endOfBatch)
        {
            Calls++;
            MaxSegment = Math.Max(MaxSegment, events.Length);
            foreach (var evt in events) _pendingSum += evt.Value;
            Count += events.Length;
            // A wrap can split one batch into two spans. Flush only after its final segment.
            if (endOfBatch)
            {
                Sum += _pendingSum;
                _pendingSum = 0;
                Flushes++;
            }
        }
    }
}
