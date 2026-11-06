using Disruptor;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;

namespace UsageRecipes;

internal static class ErrorHandling
{
    public static void Run()
    {
        var rejected = new List<RejectedInput>();
        var handler = new ScoreHandler();
        using var pipeline = new Disruptor<Score>(
            () => new(), 16, ProducerType.Single, new BlockingWaitStrategy());
        // This stateful exception policy belongs to this ONE processor.
        pipeline.SetDefaultExceptionHandler(new ResilientExceptionHandler<Score>((ex, sequence, evt) =>
        {
            // Copy values, never retain evt: it is a reusable ring slot.
            rejected.Add(new RejectedInput(sequence, evt?.Points ?? 0, ex.Message));
        }, maxConsecutiveFailures: 3));
        pipeline.HandleEventsWith(handler);
        var ring = pipeline.Start();
        foreach (int points in new[] { 10, -5, 20, -9, 30 })
            ring.PublishEvent(static (evt, _, value) => evt.Points = value, points);

        pipeline.Shutdown(TimeSpan.FromSeconds(5));
        Check.That(handler.Accepted == 3 && handler.Total == 60 && rejected.Count == 2
                   && rejected[0].Sequence == 1 && rejected[0].Points == -5
                   && rejected[1].Sequence == 3 && rejected[1].Points == -9, "Error isolation result differs.");
        Console.WriteLine($"Accepted={handler.Accepted}, total={handler.Total}, rejected snapshots={rejected.Count}");
        Console.WriteLine("Skip records a failure; it does not retry or persist a dead-letter queue.");
    }

    private sealed class Score { public int Points; }
    private sealed record RejectedInput(long Sequence, int Points, string Reason);
    private sealed class ScoreHandler : IEventHandler<Score>
    {
        public int Accepted, Total;
        public void OnEvent(Score data, long sequence, bool endOfBatch)
        {
            // Check before mutation: skipping an exception does not roll back partial work.
            if (data.Points < 0) throw new ArgumentException("Score must be nonnegative.");
            Total += data.Points;
            Accepted++;
        }
    }
}
