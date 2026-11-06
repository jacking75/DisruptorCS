using Disruptor;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;

namespace UsageRecipes;

internal static class MultiProducer
{
    public static async Task Run()
    {
        const int connections = 4, perConnection = 1_000;
        var handler = new ConnectionHandler(connections);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipeline = new Disruptor<Command>(
            () => new(), 64, ProducerType.Multi, new BlockingWaitStrategy());
        pipeline.HandleEventsWith(handler);
        var ring = pipeline.Start();

        var producers = Enumerable.Range(0, connections).Select(connection => Task.Run(() =>
        {
            for (int number = 0; number < perConnection; number++)
            {
                long sequence = ring.Next(deadline.Token);
                try
                {
                    ring[sequence].Connection = connection;
                    ring[sequence].Number = number;
                }
                finally { ring.Publish(sequence); }
            }
        })).ToArray();

        // Joining producers first closes all claimed slots before the drain target is captured.
        await Task.WhenAll(producers);
        pipeline.Shutdown(TimeSpan.FromSeconds(5));
        Check.That(handler.Count == connections * perConnection && handler.OrderErrors == 0
                   && handler.Next.All(n => n == perConnection), "Missing or reordered connection commands.");
        Console.WriteLine($"Consumed={handler.Count}, per-connection order errors={handler.OrderErrors}");
    }

    private sealed class Command { public int Connection, Number; }
    private sealed class ConnectionHandler(int connections) : IEventHandler<Command>
    {
        public readonly int[] Next = new int[connections];
        public int Count, OrderErrors;
        public void OnEvent(Command data, long sequence, bool endOfBatch)
        {
            if (data.Number != Next[data.Connection]++) OrderErrors++;
            Count++;
        }
    }
}
