using System.Buffers;
using Disruptor;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;

namespace UsageRecipes;

internal static class PooledPayload
{
    public static void Run()
    {
        var audit = new PacketReader();
        var decode = new PacketReader();
        var finished = new CompletionHandler();
        using var pipeline = new Disruptor<Packet>(
            () => new(), 16, ProducerType.Single, new BlockingWaitStrategy(),
            new DisruptorOptions { AutoClearSlots = true });
        // A single terminal consumer joins both readers before the framework calls Clear.
        pipeline.HandleEventsWith(audit, decode).Then(finished);
        var ring = pipeline.Start();
        for (int i = 0; i < 100; i++)
        {
            byte[]? payload = ArrayPool<byte>.Shared.Rent(32);
            payload.AsSpan(0, 32).Fill((byte)i);
            try
            {
                using var scope = ring.TryPublishEventScope(TimeSpan.FromSeconds(5));
                Check.That(scope.IsValid, "Timed out waiting for a packet slot.");
                scope.Event.Payload = payload;
                scope.Event.Length = 32;
                payload = null; // Ownership transfers to the slot; Clear returns it exactly once.
            }
            finally
            {
                if (payload is not null) ArrayPool<byte>.Shared.Return(payload);
            }
        }
        pipeline.Shutdown(TimeSpan.FromSeconds(5));
        var slots = Enumerable.Range(0, ring.BufferSize).Select(i => ring[i]).ToArray();
        Check.That(audit.Count == 100 && decode.Count == 100 && finished.Count == 100
                   && audit.Sum == 158_400 && decode.Sum == 158_400
                   && slots.All(p => p.Payload is null && p.Length == 0)
                   && slots.Sum(p => p.Returns) == 100, "Packet contents or buffer ownership differs.");
        Console.WriteLine("Both readers verified 100 packets; 100 rented buffers returned; no slot retains a buffer");
    }

    private sealed class Packet : IClearable
    {
        public byte[]? Payload;
        public int Length, Returns;
        public void Clear()
        {
            if (Payload is null) return;
            ArrayPool<byte>.Shared.Return(Payload);
            Payload = null;
            Length = 0;
            Returns++;
        }
    }
    private sealed class PacketReader : IEventHandler<Packet>
    {
        public int Count, Sum;
        public void OnEvent(Packet data, long sequence, bool endOfBatch)
        {
            Check.That(data.Payload is not null && data.Length == 32, "Payload released before a reader finished.");
            foreach (byte value in data.Payload.AsSpan(0, data.Length)) Sum += value;
            Count++;
        }
    }
    private sealed class CompletionHandler : IEventHandler<Packet>
    {
        public int Count;
        public void OnEvent(Packet data, long sequence, bool endOfBatch) => Count++;
    }
}
