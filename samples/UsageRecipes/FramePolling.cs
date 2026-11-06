using Disruptor;
using Disruptor.WaitStrategy;

namespace UsageRecipes;

internal static class FramePolling
{
    public static void Run()
    {
        const int count = 100, budget = 3;
        var ring = RingBuffer<Input>.CreateSingleProducer(() => new(), 8, new BlockingWaitStrategy());
        // Register before publishing; the poller starts at the current cursor.
        using var poller = ring.NewPoller();
        int published = 0, consumed = 0, frames = 0, maxPerFrame = 0, orderErrors = 0;
        while (consumed < count)
        {
            // Blocking Next here would deadlock: this same thread must consume to free space.
            while (published < count && ring.TryNext(out long sequence))
            {
                try { ring[sequence].Number = published++; }
                finally { ring.Publish(sequence); }
            }
            int before = consumed;
            poller.Poll((evt, _, _) =>
            {
                if (evt.Number != consumed) orderErrors++;
                consumed++;
                return true;
            }, maxEvents: budget);
            maxPerFrame = Math.Max(maxPerFrame, consumed - before);
            frames++; // A real game loop runs simulation/rendering here.
            Check.That(frames <= count, "Polling made insufficient progress.");
        }
        Check.That(published == count && orderErrors == 0 && maxPerFrame <= budget,
            "Frame budget or command ordering violated.");
        Console.WriteLine($"Commands={consumed}, frames={frames}, max commands/frame={maxPerFrame} (budget 3)");
    }
    private sealed class Input { public int Number; }
}
