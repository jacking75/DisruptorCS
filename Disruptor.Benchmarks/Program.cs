using BenchmarkDotNet.Running;
using Disruptor;
using Disruptor.Benchmarks;
using Disruptor.WaitStrategy;

if (args.Length > 0 && args[0].Equals("latency", StringComparison.OrdinalIgnoreCase))
{
    RunLatencySuite();
    return;
}

if (args.Length > 0 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
{
    RunLatencySuite();
    Console.WriteLine();
}

BenchmarkSwitcher.FromAssembly(typeof(UnicastBenchmarks).Assembly).Run(args);

static void RunLatencySuite()
{
    Console.WriteLine("=== 지연 분포 측정 ===");
    Console.WriteLine("게임 서버는 평균이 아니라 tail latency 가 중요합니다.");
    Console.WriteLine("60Hz 틱 예산 = 16.6ms → '>15ms' 열이 0이 아니면 틱이 통째로 날아갑니다.\n");

    Console.WriteLine("--- [1] 백프레셔 발생 시 Next() 지연 (ring=1024, 느린 컨슈머) ---");
    Console.WriteLine(LatencyBenchmarks.MeasureBackpressureLatency(
        "SP / SpinOnlyBackoff", ProducerType.Single, 1024, 300_000, 300, SpinOnlyBackoff.Instance));
    Console.WriteLine(LatencyBenchmarks.MeasureBackpressureLatency(
        "SP / SleepingBackoff", ProducerType.Single, 1024, 300_000, 300, SleepingBackoff.Instance));
    Console.WriteLine(LatencyBenchmarks.MeasureBackpressureLatency(
        "MP / SpinOnlyBackoff", ProducerType.Multi, 1024, 300_000, 300, SpinOnlyBackoff.Instance));

    Console.WriteLine("\n--- [2] 유휴 후 단발 게시 wake-up 지연 (20,000회) ---");
    Console.WriteLine(LatencyBenchmarks.MeasureWakeupLatency(
        "BlockingWaitStrategy", new BlockingWaitStrategy(), 20_000));
    Console.WriteLine(LatencyBenchmarks.MeasureWakeupLatency(
        "TimeoutBlocking(1ms)", new TimeoutBlockingWaitStrategy(TimeSpan.FromMilliseconds(1)), 20_000));
    Console.WriteLine(LatencyBenchmarks.MeasureWakeupLatency(
        "YieldingWaitStrategy", new YieldingWaitStrategy(), 20_000));
    Console.WriteLine(LatencyBenchmarks.MeasureWakeupLatency(
        "BusySpinWaitStrategy", new BusySpinWaitStrategy(), 20_000));
    Console.WriteLine(LatencyBenchmarks.MeasureWakeupLatency(
        "PhasedBackoff(spin 100us→blocking)",
        PhasedBackoffWaitStrategy.WithBlocking(
            TimeSpan.FromMicroseconds(100), TimeSpan.FromMicroseconds(100)),
        20_000));
}
