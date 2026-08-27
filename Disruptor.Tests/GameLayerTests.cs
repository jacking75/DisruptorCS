using System.Collections.Concurrent;
using Disruptor.Dsl;
using Disruptor.Game;
using Disruptor.WaitStrategy;
using Xunit;
using Xunit.Abstractions;

namespace Disruptor.Tests;

public class ShardedDisruptorTests
{
    private readonly ITestOutputHelper _output;

    public ShardedDisruptorTests(ITestOutputHelper output) => _output = output;

    private sealed class ShardHandler : IEventHandler<TestEvent>
    {
        private readonly ConcurrentDictionary<long, List<long>> _perKey;

        public ShardHandler(ConcurrentDictionary<long, List<long>> perKey) => _perKey = perKey;

        public void OnEvent(TestEvent data, long sequence, bool endOfBatch)
        {
            // 핸들러는 샤드당 하나뿐이므로 이 리스트에 대한 접근은 단일 스레드다.
            var list = _perKey.GetOrAdd(data.Value, _ => new List<long>());
            list.Add(data.Sequence);
        }
    }

    [Fact]
    public void SameKey_AlwaysMapsToSameShard()
    {
        using var sharded = new ShardedDisruptor<TestEvent>(
            shardCount: 8, ringSizePerShard: 64,
            () => new TestEvent(),
            _ => new NoopHandler());

        for (long key = 0; key < 1000; key++)
            Assert.Equal(sharded.ShardOf(key), sharded.ShardOf(key));
    }

    [Fact]
    public void Keys_AreDistributedAcrossShards()
    {
        using var sharded = new ShardedDisruptor<TestEvent>(
            shardCount: 8, ringSizePerShard: 64,
            () => new TestEvent(),
            _ => new NoopHandler());

        var counts = new int[8];

        // 연속 키와 16의 배수 키 모두 균등 분산되어야 한다 (splitmix64 finalizer 효과)
        for (long key = 0; key < 8000; key += 16)
            counts[sharded.ShardOf(key)]++;

        _output.WriteLine($"샤드별 분포: [{string.Join(", ", counts)}]");

        int totalKeys = 8000 / 16;              // 500개 키
        double expected = totalKeys / 8.0;      // 샤드당 62.5개 기대
        foreach (int c in counts)
            Assert.InRange(c, expected * 0.6, expected * 1.4);
    }

    [Fact]
    public void PerKeyOrdering_IsPreserved()
    {
        const int keys = 64;
        const int perKey = 200;

        var perKeyEvents = new ConcurrentDictionary<long, List<long>>();

        using var sharded = new ShardedDisruptor<TestEvent>(
            shardCount: 4, ringSizePerShard: 1024,
            () => new TestEvent(),
            _ => new ShardHandler(perKeyEvents),
            ProducerType.Multi,
            _ => new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "shard-order", AutoClearSlots = false });

        sharded.Start();

        // 키마다 하나의 프로듀서 스레드 -> 키 내부 순서가 보장되어야 한다
        var threads = new Thread[keys];
        for (int k = 0; k < keys; k++)
        {
            long key = k;
            threads[k] = new Thread(() =>
            {
                for (int i = 0; i < perKey; i++)
                {
                    int order = i;
                    sharded.Publish(key, (evt, seq) =>
                    {
                        evt.Value = key;
                        evt.Sequence = order;
                    });
                }
            })
            { IsBackground = true };
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        sharded.Shutdown(TimeSpan.FromSeconds(20));

        Assert.Equal(keys, perKeyEvents.Count);

        foreach (var (key, list) in perKeyEvents)
        {
            Assert.Equal(perKey, list.Count);
            for (int i = 0; i < perKey; i++)
                Assert.Equal(i, list[i]);   // 키 내부 순서 보장
        }
    }
}

public class FixedTickDriverTests
{
    [Fact]
    public void Advances_ApproximatelyAtRequestedRate()
    {
        long ticks = 0;
        var driver = new FixedTickDriver(TimeSpan.FromMilliseconds(10), _ => ticks++);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromMilliseconds(300))
        {
            driver.TryAdvance();
            Thread.Sleep(1);
        }

        // 300ms / 10ms = 30틱 전후
        Assert.InRange(ticks, 15, 60);
    }

    [Fact]
    public void DoesNotDeathSpiral_WhenSeverelyBehind()
    {
        long ticks = 0;
        var driver = new FixedTickDriver(TimeSpan.FromMilliseconds(1), _ => ticks++, maxCatchUpTicks: 3);

        // 1ms 틱인데 500ms를 쉬면 500틱이 밀린다.
        Thread.Sleep(500);

        int advanced = driver.TryAdvance();

        Assert.True(advanced <= 3, $"한 번에 {advanced}틱을 따라잡았습니다 — death spiral 방지가 동작하지 않습니다.");
        Assert.True(driver.SkippedTicks > 0, "밀린 틱을 건너뛰지 않았습니다.");
    }
}
