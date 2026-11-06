using System.Buffers.Binary;
using Disruptor;
using Disruptor.Dsl;
using Disruptor.Game;
using Disruptor.WaitStrategy;

namespace UsageRecipes;

internal static class JournalReplay
{
    public static void Run()
    {
        const int count = 40;
        var codec = new ScoreCodec();
        var live = new ScoreHandler();
        using var stream = new MemoryStream();
        using (var writer = new JournalWriter(stream, ownsStream: false))
        using (var journal = new JournalHandler<ScoreChange>(writer, codec))
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        using (var pipeline = new Disruptor<ScoreChange>(
            () => new(), 16, ProducerType.Single, new BlockingWaitStrategy()))
        {
            // A failed journal write must not be skipped and then applied as if it succeeded.
            pipeline.SetDefaultExceptionHandler(new HaltOnExceptionHandler<ScoreChange>());
            pipeline.HandleEventsWith(journal).Then(live);
            var ring = pipeline.Start();
            for (int i = 0; i < count; i++)
            {
                long sequence = ring.Next(deadline.Token);
                try
                {
                    ring[sequence].Player = i % 4;
                    ring[sequence].Delta = i + 1;
                }
                finally { ring.Publish(sequence); }
            }
            pipeline.Shutdown(TimeSpan.FromSeconds(5));
            Check.That(writer.RecordCount == count && live.Count == count && live.OrderErrors == 0,
                "Live journal or state did not drain.");
        }

        stream.Position = 0;
        var verification = JournalReader.Verify(stream);
        Check.That(verification.IsValid && verification.RecordCount == count, "Journal verification failed.");

        stream.Position = 0;
        var replay = new ScoreHandler();
        long replayed = JournalReader.Replay(stream, codec, () => new ScoreChange(), replay);
        Check.That(replayed == count && replay.Count == count && replay.OrderErrors == 0
                   && live.Scores.SequenceEqual(new long[] { 190, 200, 210, 220 })
                   && replay.Scores.SequenceEqual(live.Scores), "Replayed state differs from live state.");
        Console.WriteLine($"Records={replayed}, player scores=[{string.Join(", ", replay.Scores)}]; live and replay match");
        // MemoryStream demonstrates ordering, not crash durability. See MatchSimulation for files.
    }

    private sealed class ScoreChange { public int Player, Delta; }
    private sealed class ScoreHandler : IEventHandler<ScoreChange>
    {
        public readonly long[] Scores = new long[4];
        public int Count, OrderErrors;
        public void OnEvent(ScoreChange data, long sequence, bool endOfBatch)
        {
            if (sequence != Count) OrderErrors++;
            Scores[data.Player] += data.Delta;
            Count++;
        }
    }
    private sealed class ScoreCodec : IJournalSerializer<ScoreChange>, IJournalDeserializer<ScoreChange>
    {
        public int Serialize(ScoreChange evt, Span<byte> destination)
        {
            if (destination.Length < 8) return -1;
            BinaryPrimitives.WriteInt32LittleEndian(destination, evt.Player);
            BinaryPrimitives.WriteInt32LittleEndian(destination[4..], evt.Delta);
            return 8;
        }
        public void Deserialize(ReadOnlySpan<byte> source, ScoreChange target)
        {
            if (source.Length != 8) throw new InvalidDataException("Expected an eight-byte score record.");
            target.Player = BinaryPrimitives.ReadInt32LittleEndian(source);
            target.Delta = BinaryPrimitives.ReadInt32LittleEndian(source[4..]);
        }
    }
}
