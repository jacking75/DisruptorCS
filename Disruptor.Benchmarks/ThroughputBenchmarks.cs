using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Disruptor;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;

namespace Disruptor.Benchmarks;

public sealed class BenchEvent
{
    public long Value;
    public long Sequence;
}

/// <summary>
/// Unicast 1P → 1C. Channel&lt;T&gt; / ConcurrentQueue&lt;T&gt; 와 비교합니다.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, warmupCount: 2, iterationCount: 5)]
public class UnicastBenchmarks
{
    private const int Operations = 1_000_000;

    [Params(1024, 65536)]
    public int RingSize { get; set; }

    [Benchmark(Baseline = true, Description = "Channel<T> (bounded SPSC)")]
    public long Channel()
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
            // Channel 은 슬롯 재사용 개념이 없으므로 매번 객체를 만들어야 한다 (GC 압력의 원인).
            var evt = new BenchEvent { Value = i };
            while (!channel.Writer.TryWrite(evt))
                Thread.SpinWait(1);
        }

        channel.Writer.Complete();
        consumer.GetAwaiter().GetResult();
        return sum;
    }

    [Benchmark(Description = "Disruptor SP (Yielding)")]
    public long DisruptorSingleProducer()
    {
        using var completion = new ManualResetEventSlim(false);
        var handler = new SumHandler(Operations, completion);

        using var disruptor = new Dsl.Disruptor<BenchEvent>(
            () => new BenchEvent(), RingSize, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "bench", AutoClearSlots = false });

        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        for (int i = 0; i < Operations; i++)
        {
            long seq = ring.Next();
            ring[seq].Value = i;   // 슬롯 재사용 — 할당 없음
            ring.Publish(seq);
        }

        completion.Wait(TimeSpan.FromSeconds(30));
        disruptor.Shutdown(TimeSpan.FromSeconds(5));
        return handler.Sum;
    }

    [Benchmark(Description = "ValueDisruptor SP (Yielding)")]
    public long ValueDisruptorSingleProducer()
    {
        using var completion = new ManualResetEventSlim(false);
        var handler = new ValueSumHandler(Operations, completion);

        using var disruptor = new ValueDisruptor<BenchValueEvent>(
            RingSize, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "bench-value", AutoClearSlots = false });

        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        for (int i = 0; i < Operations; i++)
        {
            long seq = ring.Next();
            ring[seq].Value = i;
            ring.Publish(seq);
        }

        completion.Wait(TimeSpan.FromSeconds(30));
        disruptor.Shutdown(TimeSpan.FromSeconds(5));
        return handler.Sum;
    }

    public struct BenchValueEvent
    {
        public long Value;
        public long Sequence;
        public long Reserved0;
        public long Reserved1; // 32바이트 — 캐시 라인에 2개
    }

    private sealed class SumHandler : IEventHandler<BenchEvent>
    {
        private readonly long _expected;
        private readonly ManualResetEventSlim _completion;
        private long _count;

        public SumHandler(long expected, ManualResetEventSlim completion)
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

    private sealed class ValueSumHandler : IValueEventHandler<BenchValueEvent>
    {
        private readonly long _expected;
        private readonly ManualResetEventSlim _completion;
        private long _count;

        public ValueSumHandler(long expected, ManualResetEventSlim completion)
        {
            _expected = expected;
            _completion = completion;
        }

        public long Sum { get; private set; }

        public void OnEvent(ref BenchValueEvent data, long sequence, bool endOfBatch)
        {
            Sum += data.Value;
            if (++_count == _expected)
                _completion.Set();
        }
    }
}

/// <summary>
/// Multicast 1P → 3C. Channel 은 소비자마다 별도 채널에 복제해야 합니다.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, warmupCount: 2, iterationCount: 5)]
public class MulticastBenchmarks
{
    private const int Operations = 500_000;
    private const int RingSize = 16384;

    [Benchmark(Baseline = true, Description = "Channel<T> x3 (복제 필요)")]
    public long ThreeChannels()
    {
        var channels = new Channel<BenchEvent>[3];
        var tasks = new Task<long>[3];

        for (int c = 0; c < 3; c++)
        {
            channels[c] = System.Threading.Channels.Channel.CreateBounded<BenchEvent>(
                new BoundedChannelOptions(RingSize) { SingleReader = true, SingleWriter = true });

            var reader = channels[c].Reader;
            tasks[c] = Task.Run(async () =>
            {
                long sum = 0;
                await foreach (var e in reader.ReadAllAsync())
                    sum += e.Value;
                return sum;
            });
        }

        for (int i = 0; i < Operations; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                var evt = new BenchEvent { Value = i };   // 소비자마다 복사본
                while (!channels[c].Writer.TryWrite(evt))
                    Thread.SpinWait(1);
            }
        }

        foreach (var ch in channels) ch.Writer.Complete();
        Task.WaitAll(tasks);
        return tasks.Sum(t => t.Result);
    }

    [Benchmark(Description = "Disruptor multicast (복제 없음)")]
    public long DisruptorMulticast()
    {
        using var completion = new CountdownEvent(3);
        var handlers = new[]
        {
            new CountingHandler(Operations, completion),
            new CountingHandler(Operations, completion),
            new CountingHandler(Operations, completion),
        };

        using var disruptor = new Dsl.Disruptor<BenchEvent>(
            () => new BenchEvent(), RingSize, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "bench-multicast", AutoClearSlots = false });

        disruptor.HandleEventsWith(handlers[0], handlers[1], handlers[2]);
        var ring = disruptor.Start();

        for (int i = 0; i < Operations; i++)
        {
            long seq = ring.Next();
            ring[seq].Value = i;
            ring.Publish(seq);
        }

        completion.Wait(TimeSpan.FromSeconds(30));
        disruptor.Shutdown(TimeSpan.FromSeconds(5));
        return handlers.Sum(h => h.Sum);
    }

    private sealed class CountingHandler : IEventHandler<BenchEvent>
    {
        private readonly long _expected;
        private readonly CountdownEvent _completion;
        private long _count;

        public CountingHandler(long expected, CountdownEvent completion)
        {
            _expected = expected;
            _completion = completion;
        }

        public long Sum { get; private set; }

        public void OnEvent(BenchEvent data, long sequence, bool endOfBatch)
        {
            Sum += data.Value;
            if (++_count == _expected)
                _completion.Signal();
        }
    }
}

/// <summary>
/// 다중 프로듀서 + 작은 링 버퍼 = 백프레셔가 상시 발생하는 구간.
/// 이식 버그가 있던 바로 그 조건입니다.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, warmupCount: 2, iterationCount: 5)]
public class MultiProducerBackpressureBenchmarks
{
    private const int PerProducer = 100_000;

    [Params(2, 4, 8)]
    public int Producers { get; set; }

    [Params(1024)]
    public int RingSize { get; set; }

    [Benchmark(Description = "Disruptor MP (백프레셔 유발)")]
    public long DisruptorMultiProducer()
    {
        long expected = (long)Producers * PerProducer;
        using var completion = new ManualResetEventSlim(false);
        var handler = new TerminalHandler(expected, completion);

        using var disruptor = new Dsl.Disruptor<BenchEvent>(
            () => new BenchEvent(), RingSize, ProducerType.Multi, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "bench-mp", AutoClearSlots = false });

        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        var threads = new Thread[Producers];
        for (int p = 0; p < Producers; p++)
        {
            threads[p] = new Thread(() =>
            {
                for (int i = 0; i < PerProducer; i++)
                {
                    long seq = ring.Next();
                    ring[seq].Value = i;
                    ring.Publish(seq);
                }
            })
            { IsBackground = true };
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        if (!completion.Wait(TimeSpan.FromSeconds(30)))
            throw new InvalidOperationException(
                $"컨슈머가 정지했습니다: {handler.Count}/{expected} — 백프레셔 결함 회귀입니다.");

        disruptor.Shutdown(TimeSpan.FromSeconds(5));
        return handler.Count;
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
}
