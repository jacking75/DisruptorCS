using Disruptor;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;
using Disruptor.Work;

namespace UsageRecipes;

internal static class WorkerJobs
{
    public static void Run()
    {
        const int count = 1_000, workerCount = 3;
        var workers = Enumerable.Range(0, workerCount).Select(_ => new SquareWorker()).ToArray();
        var result = new ResultHandler(count);
        var prepare = new PrepareHandler();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipeline = new Disruptor<Job>(
            () => new(), 64, ProducerType.Single, new BlockingWaitStrategy());
        // With no preprocessing, use HandleEventsWithWorkerPool(...).Then(result).
        pipeline.HandleEventsWith(prepare)
                .ThenWorkerPool(workerCount, i => workers[i])
                .Then(result);
        var ring = pipeline.Start();
        for (int i = 0; i < count; i++)
        {
            long sequence = ring.Next(deadline.Token);
            try
            {
                ring[sequence].Id = i;
                ring[sequence].Operand = -1;
                ring[sequence].Square = -1;
                ring[sequence].Visits = 0;
            }
            finally { ring.Publish(sequence); }
        }
        pipeline.Shutdown(TimeSpan.FromSeconds(5));
        Check.That(prepare.Count == count && result.Count == count && result.Errors == 0 && result.Seen.All(n => n == 1)
                   && workers.Sum(w => w.Count) == count, "Work was missing, duplicated, or incomplete.");
        Console.WriteLine($"Jobs={result.Count}, per-worker=[{string.Join(", ", workers.Select(w => w.Count))}], result errors={result.Errors}");
    }

    private sealed class Job { public int Id, Operand, Visits; public long Square; }
    private sealed class PrepareHandler : IEventHandler<Job>
    {
        public int Count;
        public void OnEvent(Job data, long sequence, bool endOfBatch)
        {
            data.Operand = data.Id;
            Count++;
        }
    }
    private sealed class SquareWorker : IWorkHandler<Job>
    {
        public int Count;
        public void OnEvent(Job evt)
        {
            // One worker receives each job. Worker completion order is not guaranteed.
            Check.That(evt.Operand == evt.Id, "Worker ran before preprocessing completed.");
            Interlocked.Increment(ref evt.Visits);
            evt.Square = (long)evt.Operand * evt.Operand;
            Count++;
        }
    }
    private sealed class ResultHandler(int count) : IEventHandler<Job>
    {
        public readonly int[] Seen = new int[count];
        public int Count, Errors;
        public void OnEvent(Job data, long sequence, bool endOfBatch)
        {
            // The dependent consumer observes completed results in ring sequence order.
            if (data.Id != sequence || data.Square != (long)data.Id * data.Id || data.Visits != 1) Errors++;
            Seen[data.Id]++;
            Count++;
        }
    }
}
