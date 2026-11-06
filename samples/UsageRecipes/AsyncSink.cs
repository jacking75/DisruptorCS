using Disruptor;
using Disruptor.Async;

namespace UsageRecipes;

internal static class AsyncSink
{
    public static async Task Run()
    {
        const int count = 80;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ring = RingBuffer<AuditEvent>.CreateMultiProducer(() => new(), 16, new AsyncWaitStrategy());
        var sink = new SimulatedSink();
        using var processor = new AsyncBatchEventProcessor<AuditEvent>(ring, ring.NewBarrier(), sink)
        {
            MaxBatchSize = 8,
        };
        processor.SetExceptionHandler(new HaltOnExceptionHandler<AuditEvent>());
        // Low-level processors require explicit gating; without it producers overwrite live slots.
        ring.AddGatingSequences(processor.Sequence);
        Task consumer = processor.RunAsync(deadline.Token);
        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < count; i++)
                {
                    long sequence = ring.Next(deadline.Token);
                    try { ring[sequence].Id = i; }
                    finally { ring.Publish(sequence); }
                }
            });

            // Halt cancels, it does not drain. Wait for the last completed sequence first.
            while (processor.Sequence.Value < count - 1)
            {
                Check.That(!consumer.IsCompleted, "Async processor stopped before draining.");
                await Task.Delay(10, deadline.Token);
            }
        }
        finally
        {
            processor.Halt();
            await consumer.WaitAsync(TimeSpan.FromSeconds(5));
            ring.RemoveGatingSequence(processor.Sequence);
        }
        Check.That(sink.Saved.SequenceEqual(Enumerable.Range(0, count)), "Async sink lost or reordered records.");
        Console.WriteLine($"Saved={sink.Saved.Count}, awaited batches={sink.Batches}; slots remained valid across await");
    }

    private sealed class AuditEvent { public int Id; }
    private sealed class SimulatedSink : IAsyncBatchEventHandler<AuditEvent>
    {
        public readonly List<int> Saved = [];
        public int Batches;
        public async ValueTask OnBatchAsync(RingBuffer<AuditEvent> ring, long fromSequence,
            long toSequence, CancellationToken cancellationToken)
        {
            // Simulates asynchronous I/O; no database/network service is required.
            await Task.Delay(2, cancellationToken);
            for (long sequence = fromSequence; sequence <= toSequence; sequence++)
                Saved.Add(ring[sequence].Id);
            Batches++;
            // Slot references must not escape this callback, even though await is supported here.
        }
    }
}
