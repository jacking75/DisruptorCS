using Disruptor;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;

namespace UsageRecipes;

internal static class Publication
{
    public static void Run()
    {
        var handler = new TotalHandler();
        using var pipeline = new Disruptor<ScoreEvent>(
            () => new(), 16, ProducerType.Single, new BlockingWaitStrategy());
        pipeline.HandleEventsWith(handler);
        var ring = pipeline.Start();

        // Validate/parse input before claiming: even a failed fill must publish its slot.
        using (var scope = ring.PublishEventScope())
            scope.Event.Points = 10;

        ring.PublishEvent(static (evt, _, points) => evt.Points = points, 20);
        ring.PublishEvent(default(ScoreTranslator), 30);

        int[] scores = [40, 50, 60];
        // A claimed batch must fit in the ring. This translator only assigns validated values.
        ring.PublishEvents<int>(scores, static (evt, _, points) => evt.Points = points);

        // Stop publishing before Shutdown: it drains the published work, then stops consumers.
        pipeline.Shutdown(TimeSpan.FromSeconds(5));
        Check.That(handler.Count == 6 && handler.Total == 210, "Score count or total differs.");
        Console.WriteLine($"Scores={handler.Count}, total={handler.Total} (expected 6, 210)");
    }

    private sealed class ScoreEvent { public int Points; }
    private readonly struct ScoreTranslator : IEventTranslator<ScoreEvent, int>
    {
        public void Translate(ScoreEvent evt, long sequence, int points) => evt.Points = points;
    }
    private sealed class TotalHandler : IEventHandler<ScoreEvent>
    {
        public int Count;
        public int Total;
        public void OnEvent(ScoreEvent data, long sequence, bool endOfBatch)
        {
            Count++;
            Total += data.Points;
        }
    }
}
