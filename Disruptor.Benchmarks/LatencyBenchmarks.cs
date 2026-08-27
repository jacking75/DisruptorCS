using System.Diagnostics;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;

namespace Disruptor.Benchmarks;

/// <summary>
/// 지연 분포 측정. 게임 서버는 평균이 아니라 <b>p99.9 / max</b> 가 중요하므로
/// BenchmarkDotNet의 평균 중심 리포트와 별도로 백분위를 직접 수집합니다.
/// </summary>
/// <remarks>
/// 릴리스 게이트는 처리량이 아니라 <c>p99.9</c>와 <c>&gt;15ms 건수</c>로 잡는 것이 게임 서버답습니다.
/// (Windows 기본 타이머 분해능 15.6ms — 60Hz 틱 예산 16.6ms를 통째로 소모)
/// </remarks>
public static class LatencyBenchmarks
{
    public sealed record Percentiles(
        string Name,
        long Samples,
        double P50Us,
        double P90Us,
        double P99Us,
        double P999Us,
        double P9999Us,
        double MaxMs,
        long Over1Ms,
        long Over5Ms,
        long Over15Ms,
        double Throughput)
    {
        public override string ToString()
            => $"{Name,-38} p50={P50Us,8:N1}us p99={P99Us,9:N1}us p99.9={P999Us,10:N1}us " +
               $"p99.99={P9999Us,10:N1}us max={MaxMs,7:F2}ms " +
               $">1ms={Over1Ms,6:N0} >5ms={Over5Ms,6:N0} >15ms={Over15Ms,6:N0} " +
               $"{Throughput,12:N0} ops/s";
    }

    /// <summary>
    /// 백프레셔가 걸린 상태에서 <c>Next()</c> 의 지연 분포를 측정합니다.
    /// </summary>
    public static Percentiles MeasureBackpressureLatency(
        string name,
        ProducerType producerType,
        int ringSize,
        int operations,
        int consumerSpinPerEvent,
        IProducerBackoff backoff)
    {
        var options = new DisruptorOptions
        {
            Name = name,
            AutoClearSlots = false,
            ProducerBackoff = backoff,
        };

        using var disruptor = new Dsl.Disruptor<BenchEvent>(
            () => new BenchEvent(), ringSize, producerType, new YieldingWaitStrategy(), options);

        disruptor.HandleEventsWith(new SpinHandler(consumerSpinPerEvent));
        var ring = disruptor.Start();

        var samples = new long[operations];
        double freq = Stopwatch.Frequency;

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < operations; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            long seq = ring.Next();
            samples[i] = Stopwatch.GetTimestamp() - t0;

            ring[seq].Value = i;
            ring.Publish(seq);
        }
        sw.Stop();

        disruptor.Shutdown(TimeSpan.FromSeconds(10));

        return Summarize(name, samples, freq, operations / sw.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// 유휴 상태에서 단발 게시의 end-to-end wake-up 지연을 측정합니다.
    /// </summary>
    public static Percentiles MeasureWakeupLatency(string name, IWaitStrategy waitStrategy, int iterations)
    {
        var options = new DisruptorOptions { Name = name, AutoClearSlots = false };

        using var disruptor = new Dsl.Disruptor<BenchEvent>(
            () => new BenchEvent(), 1024, ProducerType.Single, waitStrategy, options);

        var handler = new EchoHandler();
        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        Thread.Sleep(200);

        double freq = Stopwatch.Frequency;
        var samples = new List<long>(iterations);
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < iterations; i++)
        {
            handler.Reset();

            long seq = ring.Next();
            ring[seq].Value = Stopwatch.GetTimestamp();
            ring.Publish(seq);

            var spin = new SpinWait();
            long deadline = Stopwatch.GetTimestamp() + (long)(freq * 2);
            while (!handler.Received && Stopwatch.GetTimestamp() < deadline)
                spin.SpinOnce(sleep1Threshold: -1);

            if (handler.Received)
                samples.Add(handler.Delta);

            // 컨슈머가 확실히 대기 상태로 진입하도록 유휴 구간을 만든다.
            Thread.SpinWait(50_000);
        }

        sw.Stop();
        disruptor.Shutdown(TimeSpan.FromSeconds(5));

        return Summarize(name, samples.ToArray(), freq, iterations / sw.Elapsed.TotalSeconds);
    }

    private static Percentiles Summarize(string name, long[] samples, double freq, double throughput)
    {
        var sorted = samples.Where(x => x >= 0).OrderBy(x => x).ToArray();
        if (sorted.Length == 0)
            return new Percentiles(name, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, throughput);

        double At(double q) => sorted[(int)(q * (sorted.Length - 1))] * 1e6 / freq;
        long CountOver(double ms) => sorted.Count(x => x * 1e3 / freq > ms);

        return new Percentiles(
            name,
            sorted.Length,
            At(0.50), At(0.90), At(0.99), At(0.999), At(0.9999),
            sorted[^1] * 1e3 / freq,
            CountOver(1), CountOver(5), CountOver(15),
            throughput);
    }

    private sealed class SpinHandler : IEventHandler<BenchEvent>
    {
        private readonly int _spins;

        public SpinHandler(int spins) => _spins = spins;

        public void OnEvent(BenchEvent data, long sequence, bool endOfBatch)
        {
            if (_spins > 0)
                Thread.SpinWait(_spins);
        }
    }

    private sealed class EchoHandler : IEventHandler<BenchEvent>
    {
        private long _received;
        private long _delta;

        public bool Received => Volatile.Read(ref _received) == 1;
        public long Delta => Volatile.Read(ref _delta);

        public void Reset()
        {
            Volatile.Write(ref _delta, 0);
            Volatile.Write(ref _received, 0);
        }

        public void OnEvent(BenchEvent data, long sequence, bool endOfBatch)
        {
            Volatile.Write(ref _delta, Stopwatch.GetTimestamp() - data.Value);
            Volatile.Write(ref _received, 1);
        }
    }
}
