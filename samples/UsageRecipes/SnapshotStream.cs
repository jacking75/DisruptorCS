using Disruptor;
using Disruptor.Async;
using Disruptor.WaitStrategy;

namespace UsageRecipes;

internal static class SnapshotStream
{
    public static async Task Run()
    {
        const int count = 100;
        var ring = RingBuffer<Score>.CreateMultiProducer(() => new(), 16, new BlockingWaitStrategy());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var snapshots = new List<ScoreSnapshot>();

        // Calling this directly reaches the first MoveNextAsync and registers the poller
        // before publication. Merely constructing the lazy enumerable does not subscribe.
        Task consumer = ConsumeAsync();
        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < count; i++)
                {
                    long sequence = ring.Next(deadline.Token);
                    try { ring[sequence].PlayerId = i; ring[sequence].Points = i * 10; }
                    finally { ring.Publish(sequence); }
                }
            });
            await consumer.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            deadline.Cancel();
            await consumer.WaitAsync(TimeSpan.FromSeconds(5));
        }

        // Enumeration disposed its poller. Overwrite every slot to demonstrate independent ownership.
        for (int i = 0; i < ring.BufferSize; i++)
            ring.PublishEvent(static (evt, _) => { evt.PlayerId = -1; evt.Points = -1; });
        Check.That(snapshots.Count == count && snapshots.Select(s => s.PlayerId).SequenceEqual(Enumerable.Range(0, count))
                   && snapshots.Sum(s => s.Points) == 49_500, "Saved snapshots changed after slot reuse.");
        Console.WriteLine($"Snapshots={snapshots.Count}, sum=49500; unchanged after all slots were overwritten");

        async Task ConsumeAsync()
        {
            await foreach (var snapshot in ring.ReadAllAsync(
                static evt => new ScoreSnapshot(evt.PlayerId, evt.Points),
                pollInterval: TimeSpan.FromMilliseconds(2), maxEventsPerPoll: 8,
                cancellationToken: deadline.Token))
            {
                snapshots.Add(snapshot);
                if (snapshots.Count == count) break;
            }
        }
    }
    private sealed class Score { public int PlayerId, Points; }
    private readonly record struct ScoreSnapshot(int PlayerId, int Points);
}
