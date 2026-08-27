extern alias DisruptorNet;

using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Disruptor;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;

using DNet = DisruptorNet::Disruptor;
using DNetDsl = DisruptorNet::Disruptor.Dsl;

namespace Disruptor.Benchmarks;

/// <summary>
/// 공식 disruptor-net 과의 정직한 비교.
/// </summary>
/// <remarks>
/// <para>
/// disruptor-net 은 수년간 최적화된 성숙한 구현입니다. <b>지는 항목이 있으면 그대로 게시합니다</b> —
/// 유리한 조건만 보여주는 것이 신뢰를 잃는 가장 빠른 방법이기 때문입니다.
/// </para>
/// <para>
/// 이 라이브러리의 차별점은 순수 처리량이 아니라 게임 서버 레이어(샤딩·틱 통합·저널/리플레이)와
/// 운영 도구(백프레셔 정책·스톨 진단·Analyzer)입니다. 그 항목들은 disruptor-net 에 없으므로
/// 비교 대상이 아니라 <see cref="GameServerScenarioBenchmarks"/> 에서 단독 측정합니다.
/// </para>
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, warmupCount: 2, iterationCount: 5)]
public class UnicastComparisonBenchmarks
{
    private const int Operations = 2_000_000;

    [Params(1024, 65536)]
    public int RingSize { get; set; }

    // ── DisruptorCS ─────────────────────────────────────────────────────────

    [Benchmark(Baseline = true, Description = "DisruptorCS (Yielding)")]
    public long DisruptorCs()
    {
        using var completion = new ManualResetEventSlim(false);
        var handler = new CsCountingHandler(Operations, completion);

        using var disruptor = new Dsl.Disruptor<BenchEvent>(
            () => new BenchEvent(), RingSize, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "cmp-cs", AutoClearSlots = false });

        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        for (int i = 0; i < Operations; i++)
        {
            long seq = ring.Next();
            ring[seq].Value = i;
            ring.Publish(seq);
        }

        completion.Wait(TimeSpan.FromSeconds(60));
        disruptor.Shutdown(TimeSpan.FromSeconds(10));
        return handler.Sum;
    }

    // ── disruptor-net ───────────────────────────────────────────────────────

    [Benchmark(Description = "disruptor-net (Yielding)")]
    public long DisruptorNetLibrary()
    {
        using var completion = new ManualResetEventSlim(false);
        var handler = new NetCountingHandler(Operations, completion);

        var disruptor = new DNetDsl.Disruptor<BenchEvent>(
            () => new BenchEvent(), RingSize, TaskScheduler.Default,
            DNetDsl.ProducerType.Single, new DNet.YieldingWaitStrategy());

        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        for (int i = 0; i < Operations; i++)
        {
            long seq = ring.Next();
            ring[seq].Value = i;
            ring.Publish(seq);
        }

        completion.Wait(TimeSpan.FromSeconds(60));
        disruptor.Shutdown(TimeSpan.FromSeconds(10));
        return handler.Sum;
    }

    // ── Channel<T> ──────────────────────────────────────────────────────────

    [Benchmark(Description = "Channel<T> (bounded SPSC)")]
    public long ChannelBaseline()
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<BenchEvent>(
            new BoundedChannelOptions(RingSize)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

        long sum = 0;
        var consumer = Task.Run(async () =>
        {
            await foreach (var e in channel.Reader.ReadAllAsync())
                sum += e.Value;
        });

        for (int i = 0; i < Operations; i++)
        {
            // Channel 은 슬롯 재사용 개념이 없어 매번 객체를 만들어야 한다 (GC 압력의 원인).
            var evt = new BenchEvent { Value = i };
            while (!channel.Writer.TryWrite(evt))
                Thread.SpinWait(1);
        }

        channel.Writer.Complete();
        consumer.GetAwaiter().GetResult();
        return sum;
    }

    private sealed class CsCountingHandler : IEventHandler<BenchEvent>
    {
        private readonly long _expected;
        private readonly ManualResetEventSlim _completion;
        private long _count;

        public CsCountingHandler(long expected, ManualResetEventSlim completion)
        {
            _expected = expected;
            _completion = completion;
        }

        public long Sum { get; private set; }

        public void OnEvent(BenchEvent data, long sequence, bool endOfBatch)
        {
            Sum += data.Value;
            if (++_count == _expected)
                _completion.Set();
        }
    }

    private sealed class NetCountingHandler : DNet.IEventHandler<BenchEvent>
    {
        private readonly long _expected;
        private readonly ManualResetEventSlim _completion;
        private long _count;

        public NetCountingHandler(long expected, ManualResetEventSlim completion)
        {
            _expected = expected;
            _completion = completion;
        }

        public long Sum { get; private set; }

        public void OnEvent(BenchEvent data, long sequence, bool endOfBatch)
        {
            Sum += data.Value;
            if (++_count == _expected)
                _completion.Set();
        }
    }
}

/// <summary>
/// 다중 프로듀서 + 작은 링 = 백프레셔 상시. 이식 버그가 있던 조건입니다.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, warmupCount: 2, iterationCount: 5)]
public class MultiProducerComparisonBenchmarks
{
    private const int PerProducer = 200_000;
    private const int RingSize = 1024;

    [Params(2, 8)]
    public int Producers { get; set; }

    [Benchmark(Baseline = true, Description = "DisruptorCS MP")]
    public long DisruptorCs()
    {
        long expected = (long)Producers * PerProducer;
        using var completion = new ManualResetEventSlim(false);
        var handler = new TerminalHandler(expected, completion);

        using var disruptor = new Dsl.Disruptor<BenchEvent>(
            () => new BenchEvent(), RingSize, ProducerType.Multi, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "cmp-mp-cs", AutoClearSlots = false });

        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        RunProducers(Producers, i =>
        {
            long seq = ring.Next();
            ring[seq].Value = i;
            ring.Publish(seq);
        });

        if (!completion.Wait(TimeSpan.FromSeconds(60)))
            throw new InvalidOperationException($"컨슈머 정지: {handler.Count}/{expected}");

        disruptor.Shutdown(TimeSpan.FromSeconds(10));
        return handler.Count;
    }

    [Benchmark(Description = "disruptor-net MP")]
    public long DisruptorNetLibrary()
    {
        long expected = (long)Producers * PerProducer;
        using var completion = new ManualResetEventSlim(false);
        var handler = new NetTerminalHandler(expected, completion);

        var disruptor = new DNetDsl.Disruptor<BenchEvent>(
            () => new BenchEvent(), RingSize, TaskScheduler.Default,
            DNetDsl.ProducerType.Multi, new DNet.YieldingWaitStrategy());

        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        RunProducers(Producers, i =>
        {
            long seq = ring.Next();
            ring[seq].Value = i;
            ring.Publish(seq);
        });

        if (!completion.Wait(TimeSpan.FromSeconds(60)))
            throw new InvalidOperationException($"컨슈머 정지: {handler.Count}/{expected}");

        disruptor.Shutdown(TimeSpan.FromSeconds(10));
        return handler.Count;
    }

    private static void RunProducers(int producers, Action<int> publish)
    {
        var threads = new Thread[producers];

        for (int p = 0; p < producers; p++)
        {
            threads[p] = new Thread(() =>
            {
                for (int i = 0; i < PerProducer; i++)
                    publish(i);
            })
            { IsBackground = true };
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
    }

    private sealed class TerminalHandler : IEventHandler<BenchEvent>
    {
        private readonly long _expected;
        private readonly ManualResetEventSlim _completion;
        private long _count;

        public TerminalHandler(long expected, ManualResetEventSlim completion)
        {
            _expected = expected;
            _completion = completion;
        }

        public long Count => _count;

        public void OnEvent(BenchEvent data, long sequence, bool endOfBatch)
        {
            if (++_count == _expected)
                _completion.Set();
        }
    }

    private sealed class NetTerminalHandler : DNet.IEventHandler<BenchEvent>
    {
        private readonly long _expected;
        private readonly ManualResetEventSlim _completion;
        private long _count;

        public NetTerminalHandler(long expected, ManualResetEventSlim completion)
        {
            _expected = expected;
            _completion = completion;
        }

        public long Count => _count;

        public void OnEvent(BenchEvent data, long sequence, bool endOfBatch)
        {
            if (++_count == _expected)
                _completion.Set();
        }
    }
}

/// <summary>
/// 이 라이브러리에만 있는 게임 서버 기능의 비용 측정. 비교 대상이 없으므로 단독 측정입니다.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 3)]
public class GameServerScenarioBenchmarks
{
    [Params(1024, 4096)]
    public int RingSize { get; set; }

    private const int Operations = 500_000;

    [Benchmark(Baseline = true, Description = "기본 파이프라인 (기준선)")]
    public long Plain() => RunPipeline(useBatchHandler: false, maxBatch: int.MaxValue);

    [Benchmark(Description = "배치 핸들러 (span 수신)")]
    public long BatchHandler() => RunPipeline(useBatchHandler: true, maxBatch: int.MaxValue);

    [Benchmark(Description = "MaxBatchSize=64 (지연 상한)")]
    public long BoundedBatch() => RunPipeline(useBatchHandler: false, maxBatch: 64);

    [Benchmark(Description = "EventPoller (스레드 0개 추가)")]
    public long Poller()
    {
        var ring = RingBuffer<BenchEvent>.CreateSingleProducer(
            () => new BenchEvent(), RingSize, new YieldingWaitStrategy());

        using var poller = ring.NewPoller();

        long sum = 0;
        int published = 0;
        int consumed = 0;

        while (consumed < Operations)
        {
            int chunk = Math.Min(RingSize / 2, Operations - published);
            for (int i = 0; i < chunk; i++, published++)
            {
                long seq = ring.Next();
                ring[seq].Value = published;
                ring.Publish(seq);
            }

            poller.Poll((evt, seq, eob) => { sum += evt.Value; consumed++; return true; }, RingSize);
        }

        return sum;
    }

    private long RunPipeline(bool useBatchHandler, int maxBatch)
    {
        using var completion = new ManualResetEventSlim(false);

        var options = new DisruptorOptions
        {
            Name = "game-bench",
            AutoClearSlots = false,
            MaxBatchSize = maxBatch,
        };

        using var disruptor = new Dsl.Disruptor<BenchEvent>(
            () => new BenchEvent(), RingSize, ProducerType.Single, new YieldingWaitStrategy(), options);

        long sum = 0;
        long count = 0;

        if (useBatchHandler)
        {
            disruptor.HandleEventsWith(new SpanSumHandler(Operations, completion, v => sum = v, () => count));
        }
        else
        {
            disruptor.HandleEventsWith(new PlainSumHandler(Operations, completion));
        }

        var ring = disruptor.Start();

        for (int i = 0; i < Operations; i++)
        {
            long seq = ring.Next();
            ring[seq].Value = i;
            ring.Publish(seq);
        }

        completion.Wait(TimeSpan.FromSeconds(60));
        disruptor.Shutdown(TimeSpan.FromSeconds(10));
        return sum + count;
    }

    private sealed class PlainSumHandler : IEventHandler<BenchEvent>
    {
        private readonly long _expected;
        private readonly ManualResetEventSlim _completion;
        private long _count;

        public PlainSumHandler(long expected, ManualResetEventSlim completion)
        {
            _expected = expected;
            _completion = completion;
        }

        public long Sum { get; private set; }

        public void OnEvent(BenchEvent data, long sequence, bool endOfBatch)
        {
            Sum += data.Value;
            if (++_count == _expected)
                _completion.Set();
        }
    }

    private sealed class SpanSumHandler : IBatchEventHandler<BenchEvent>
    {
        private readonly long _expected;
        private readonly ManualResetEventSlim _completion;
        private readonly Action<long> _publishSum;
        private long _count;
        private long _sum;

        public SpanSumHandler(long expected, ManualResetEventSlim completion,
                              Action<long> publishSum, Func<long> _)
        {
            _expected = expected;
            _completion = completion;
            _publishSum = publishSum;
        }

        public void OnBatch(ReadOnlySpan<BenchEvent> events, long startSequence, bool endOfBatch)
        {
            // 조각 전체를 한 루프에서 처리 — 호출 경계가 배치당 1~2회다.
            for (int i = 0; i < events.Length; i++)
                _sum += events[i].Value;

            _count += events.Length;

            if (_count >= _expected)
            {
                _publishSum(_sum);
                _completion.Set();
            }
        }
    }
}
