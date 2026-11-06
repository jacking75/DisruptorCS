using Disruptor;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;

namespace UsageRecipes;

internal static class ValueEvents
{
    public static void Run()
    {
        var handler = new DistanceHandler();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipeline = new ValueDisruptor<Movement>(
            32, ProducerType.Single, new BlockingWaitStrategy());
        pipeline.HandleEventsWith(new IntegrateHandler()).Then(handler);
        var ring = pipeline.Start();
        for (int i = 0; i < 100; i++)
        {
            long sequence = ring.Next(deadline.Token);
            try
            {
                // Without ref, a local variable would only change a COPY of the slot.
                ref Movement movement = ref ring[sequence];
                movement = new Movement { Position = i, Velocity = 2 };
            }
            finally { ring.Publish(sequence); }
        }
        pipeline.Shutdown(TimeSpan.FromSeconds(5));
        Check.That(handler.Count == 100 && handler.Sum == 5_150, "Struct transformation differs.");
        Console.WriteLine($"Movements={handler.Count}, position sum={handler.Sum} (expected 5150)");
    }

    private struct Movement { public int Position, Velocity; }
    private sealed class IntegrateHandler : IValueEventHandler<Movement>
    {
        public void OnEvent(ref Movement data, long sequence, bool endOfBatch)
            => data.Position += data.Velocity;
    }
    private sealed class DistanceHandler : IValueEventHandler<Movement>
    {
        public int Count, Sum;
        public void OnEvent(ref Movement data, long sequence, bool endOfBatch)
        {
            Count++;
            Sum += data.Position;
        }
    }
}
