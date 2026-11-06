using Disruptor;
using Disruptor.WaitStrategy;

namespace UsageRecipes;

internal static class Backpressure
{
    public static void Run()
    {
        var ring = RingBuffer<Position>.CreateSingleProducer(() => new(), 4, new BlockingWaitStrategy());
        using var poller = ring.NewPoller();
        var gate = new PublishGate<Position>(ring) { CongestionThreshold = 0.5 };
        Action<Position, long> fill = static (evt, seq) => evt.X = (int)seq;

        // Deliberately do not poll yet: saturation is deterministic, independent of CPU speed.
        for (int i = 0; i < 3; i++)
            Check.That(gate.TryPublish(BackpressurePolicy.Block, fill), "Initial publish failed.");
        Check.That(!gate.TryPublish(BackpressurePolicy.DropWhenCongested, fill), "Expected a drop above 50% utilization.");
        Check.That(gate.TryPublish(BackpressurePolicy.Block, fill), "Last free slot was not claimed.");
        Check.That(!gate.TryPublish(BackpressurePolicy.FailAfterTimeout, fill,
            timeout: TimeSpan.FromMilliseconds(10)), "Expected rejection on a full ring.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        bool cancellationObserved = false;
        try { gate.TryPublish(BackpressurePolicy.Block, fill, cancellationToken: cancelled.Token); }
        catch (OperationCanceledException) { cancellationObserved = true; }
        Check.That(cancellationObserved, "Blocked publication did not observe cancellation.");

        int consumed = 0;
        poller.Poll((_, _, _) => { consumed++; return true; }, maxEvents: 4);
        Check.That(gate.TryPublish(BackpressurePolicy.FailAfterTimeout, fill,
            timeout: TimeSpan.Zero), "Publication did not recover after polling.");
        poller.Poll((_, _, _) => { consumed++; return true; });
        Check.That(consumed == 5 && gate.PublishedCount == 5 && gate.DroppedCount == 1
                   && gate.RejectedCount == 1, "Backpressure accounting differs.");
        Console.WriteLine($"Published={gate.PublishedCount}, dropped={gate.DroppedCount}, rejected={gate.RejectedCount}, consumed={consumed}; cancellation observed");
        // DropWhenCongested rechecks capacity through TryNext after the utilization check.
        // It drops if another producer fills the ring; it never waits for consumers to catch up.
    }
    private sealed class Position { public int X; }
}
