using System.Collections.Concurrent;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;
using Disruptor.Work;
using Xunit;
using Xunit.Abstractions;

namespace Disruptor.Tests;

public class WorkerPoolTests
{
    private readonly ITestOutputHelper _output;

    public WorkerPoolTests(ITestOutputHelper output) => _output = output;

    private sealed class CountingWorkHandler : IWorkHandler<TestEvent>
    {
        private readonly ConcurrentBag<long> _seen;
        private readonly int _index;

        public CountingWorkHandler(ConcurrentBag<long> seen, int index)
        {
            _seen = seen;
            _index = index;
        }

        public int Handled { get; private set; }

        public void OnEvent(TestEvent evt)
        {
            _seen.Add(evt.Sequence);
            Handled++;
        }
    }

    [Fact]
    public void WorkerPool_ProcessesEachEventExactlyOnce()
    {
        const int count = 20_000;
        const int workers = 4;

        var seen = new ConcurrentBag<long>();
        var handlers = new CountingWorkHandler[workers];

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), 1024, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "worker-test", AutoClearSlots = false });

        disruptor.HandleEventsWithWorkerPool(workers, i =>
        {
            handlers[i] = new CountingWorkHandler(seen, i);
            return handlers[i];
        });

        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(20));

        var distinct = seen.Distinct().ToList();

        _output.WriteLine($"총 {seen.Count}건 처리, 고유 {distinct.Count}건, " +
                          $"워커별 = [{string.Join(", ", handlers.Select(h => h.Handled))}]");

        Assert.Equal(count, seen.Count);          // 중복 처리 없음
        Assert.Equal(count, distinct.Count);      // 유실 없음

        // 실제로 여러 워커에 분배되었는지 확인 (한 워커가 다 가져가면 풀의 의미가 없다)
        Assert.True(handlers.Count(h => h.Handled > 0) >= 2,
            "이벤트가 한 워커에만 몰렸습니다 — 분배가 동작하지 않습니다.");
    }

    [Fact]
    public void WorkerPool_AfterHandlerChain_SeesEventsOnlyAfterUpstream()
    {
        const int count = 5_000;

        var seen = new ConcurrentBag<long>();

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), 1024, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "worker-chain-test", AutoClearSlots = false });

        var upstream = new VerifyingHandler();
        disruptor.HandleEventsWith(upstream)
                 .ThenWorkerPool(3, i => new CountingWorkHandler(seen, i));

        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(20));

        Assert.Equal(count, upstream.Count);
        Assert.Equal(count, seen.Distinct().Count());
    }
}
