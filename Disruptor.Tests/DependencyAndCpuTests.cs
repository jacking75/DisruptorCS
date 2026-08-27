using System.Diagnostics;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;
using Xunit;
using Xunit.Abstractions;

namespace Disruptor.Tests;

/// <summary>
/// 의존성 그래프의 순서 보장과, 블로킹 전략의 CPU 예산을 검증합니다.
/// </summary>
public class DependencyAndCpuTests
{
    private readonly ITestOutputHelper _output;

    public DependencyAndCpuTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Downstream_NeverOvertakes_Upstream()
    {
        const int count = 20_000;

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), 1024, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "dep-test", AutoClearSlots = false });

        var upstream = new VerifyingHandler();
        var group = disruptor.HandleEventsWith(upstream);

        // 하위 핸들러는 상위 핸들러의 시퀀스를 직접 관찰해 위반을 검출한다.
        var downstream = new DependencyCheckHandler(group.Sequences[0]);
        group.Then(downstream);

        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(20));

        Assert.Equal(count, upstream.Count);
        Assert.Equal(count, downstream.Count);
        Assert.Equal(0, downstream.Violations);
    }

    [Fact]
    public void Diamond_AllPathsSeeAllEvents_InOrder()
    {
        const int count = 20_000;

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), 1024, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "diamond-test", AutoClearSlots = false });

        var journal = new VerifyingHandler();
        var replication = new VerifyingHandler();
        var business = new VerifyingHandler();

        disruptor.HandleEventsWith(journal, replication).Then(business);

        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(20));

        Assert.Equal(count, journal.Count);
        Assert.Equal(count, replication.Count);
        Assert.Equal(count, business.Count);
        Assert.Equal(0, business.Mismatches);
        Assert.Equal(0, business.OutOfOrder);
    }

    /// <summary>
    /// BlockingWaitStrategy를 쓰는데 의존성 체인 하위 핸들러가 hot-spin 하면
    /// 코어 하나를 통째로 태웁니다 (컨테이너 2 vCPU면 절반이 증발).
    /// </summary>
    [Fact]
    [Trait("Category", "Cpu")]
    public void BlockingStrategy_DoesNotBurnCpu_WithSlowUpstream()
    {
        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), 1024, ProducerType.Single, new BlockingWaitStrategy(),
            new DisruptorOptions { Name = "cpu-test", AutoClearSlots = false });

        var slow = new SlowHandler(sleepMs: 10);
        var downstream = new VerifyingHandler();
        disruptor.HandleEventsWith(slow).Then(downstream);

        var ring = disruptor.Start();

        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var sw = Stopwatch.StartNew();

        const int events = 200;
        for (int i = 0; i < events; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
            Thread.Sleep(1);
        }

        while (downstream.Count < events && sw.Elapsed < TimeSpan.FromSeconds(20))
            Thread.Sleep(10);

        sw.Stop();
        double cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
        double cores = cpuMs / sw.Elapsed.TotalMilliseconds;

        disruptor.Shutdown(TimeSpan.FromSeconds(5));

        _output.WriteLine(
            $"events={downstream.Count} elapsed={sw.Elapsed.TotalMilliseconds:F0}ms " +
            $"cpu={cpuMs:F0}ms → {cores:F2} 코어");

        Assert.Equal(events, downstream.Count);
        Assert.True(cores < 0.5,
            $"BlockingWaitStrategy인데 CPU를 {cores:F2}코어 사용했습니다 — " +
            "하위 컨슈머가 상위를 hot-spin으로 기다리고 있습니다.");
    }

    [Fact]
    [Trait("Category", "Cpu")]
    public void TimeoutStrategy_InvokesOnTimeout_WhenIdle()
    {
        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), 64, ProducerType.Single,
            new TimeoutBlockingWaitStrategy(TimeSpan.FromMilliseconds(10)),
            new DisruptorOptions { Name = "timeout-test", AutoClearSlots = false });

        var handler = new VerifyingHandler();
        disruptor.HandleEventsWith(handler);
        disruptor.Start();

        Thread.Sleep(300);

        disruptor.Shutdown(TimeSpan.FromSeconds(2));

        _output.WriteLine($"timeouts={handler.Timeouts}");
        Assert.True(handler.Timeouts > 5,
            $"유휴 300ms 동안 OnTimeout이 {handler.Timeouts}회만 호출되었습니다 (10ms 간격 기대).");
    }
}
