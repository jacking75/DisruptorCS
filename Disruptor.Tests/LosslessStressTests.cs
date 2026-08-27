using System.Diagnostics;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;
using Xunit;
using Xunit.Abstractions;

namespace Disruptor.Tests;

/// <summary>
/// 게시한 이벤트가 정확히 한 번씩 모두 소비되는지 검증합니다.
/// </summary>
/// <remarks>
/// <b>타임아웃을 반드시 실패로 처리합니다.</b> 기존 예제는
/// <c>allDone.Wait(10초)</c>의 반환값을 검사하지 않고 처리량을 출력해서,
/// 이벤트 99%가 유실되어도 "성공"처럼 보였습니다.
/// </remarks>
[Trait("Category", "Stress")]
public class LosslessStressTests
{
    private readonly ITestOutputHelper _output;

    public LosslessStressTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<int, int, int> Configurations => new()
    {
        // producers, eventsPerProducer, ringSize
        //  링을 작게 잡아 백프레셔를 강제로 유발한다 — 여기가 결함이 드러나는 지점이다.
        { 1, 200_000, 1024 },
        { 1, 200_000,   64 },
        { 4,  50_000, 1024 },
        { 8,  50_000, 1024 },
        { 8,  50_000,   64 },
        { 16, 20_000,   64 },
    };

    [Theory]
    [MemberData(nameof(Configurations))]
    public void AllPublishedEvents_AreConsumedExactlyOnce(int producers, int perProducer, int ringSize)
    {
        long expected = (long)producers * perProducer;
        var producerType = producers == 1 ? ProducerType.Single : ProducerType.Multi;

        using var completion = new ManualResetEventSlim(false);
        var handler = new VerifyingHandler(expected, completion);

        var ring = RingBuffer<TestEvent>.Create(
            producerType, () => new TestEvent(), ringSize, new YieldingWaitStrategy());

        var barrier = ring.NewBarrier();
        using var processor = new BatchEventProcessor<TestEvent>(ring, barrier, handler);
        ring.AddGatingSequences(processor.Sequence);

        using var cts = new CancellationTokenSource();
        var consumerThread = new Thread(() => processor.Run(cts.Token)) { IsBackground = true };
        consumerThread.Start();

        var sw = Stopwatch.StartNew();
        var threads = new Thread[producers];

        for (int p = 0; p < producers; p++)
        {
            threads[p] = new Thread(() =>
            {
                for (int i = 0; i < perProducer; i++)
                {
                    long seq = ring.Next();
                    ring[seq].Sequence = seq;   // 슬롯에 자기 시퀀스를 기록
                    ring[seq].Value = i;
                    ring.Publish(seq);
                }
            })
            { IsBackground = true };
        }

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        bool completed = completion.Wait(TimeSpan.FromSeconds(30));
        sw.Stop();

        cts.Cancel();
        processor.Halt();
        consumerThread.Join(TimeSpan.FromSeconds(2));

        _output.WriteLine(
            $"producers={producers} ring={ringSize} " +
            $"consumed={handler.Count:N0}/{expected:N0} " +
            $"elapsed={sw.Elapsed.TotalMilliseconds:F0}ms " +
            $"throughput={handler.Count / sw.Elapsed.TotalSeconds:N0} ops/s " +
            $"maxBatch={handler.MaxBatchSize:N0}");

        Assert.True(completed,
            $"타임아웃: 기대 {expected:N0}건 중 {handler.Count:N0}건만 소비됨 " +
            $"(유실 {expected - handler.Count:N0}건). 컨슈머가 정지했을 가능성이 큽니다.");

        Assert.Equal(expected, handler.Count);
        Assert.Equal(0, handler.Mismatches);
        Assert.Equal(0, handler.OutOfOrder);
        Assert.Equal(expected - 1, handler.LastSequence);
    }

    [Theory]
    [InlineData(ProducerType.Single)]
    [InlineData(ProducerType.Multi)]
    public void Multicast_EveryHandlerSeesEveryEvent(ProducerType producerType)
    {
        const int count = 50_000;
        const int ringSize = 256;

        var handlers = new[]
        {
            new VerifyingHandler(),
            new VerifyingHandler(),
            new VerifyingHandler(),
        };

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), ringSize, producerType, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "multicast-test", AutoClearSlots = false });

        disruptor.HandleEventsWith(handlers[0], handlers[1], handlers[2]);
        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(20));

        foreach (var h in handlers)
        {
            Assert.Equal(count, h.Count);
            Assert.Equal(0, h.Mismatches);
            Assert.Equal(0, h.OutOfOrder);
        }
    }

    [Fact]
    public void BatchEffect_ProducesLargeBatches_WhenConsumerFallsBehind()
    {
        const int count = 100_000;
        var handler = new VerifyingHandler();

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), 8192, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "batch-test", AutoClearSlots = false });

        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(20));

        _output.WriteLine($"batchStarts={handler.BatchStarts:N0} maxBatch={handler.MaxBatchSize:N0}");

        Assert.Equal(count, handler.Count);

        // 배치 효과의 불변식: 배치 시작 횟수가 이벤트 수보다 적고, 큰 배치가 실제로 발생한다.
        // (정확한 배치 크기는 Debug/Release 와 스케줄러에 따라 크게 달라지므로 임계값을 느슨하게 잡는다)
        Assert.True(handler.BatchStarts < count,
            $"배치 효과가 동작하지 않습니다 (batchStarts={handler.BatchStarts}, events={count}).");
        Assert.True(handler.MaxBatchSize >= 16,
            $"큰 배치가 형성되지 않았습니다 (maxBatch={handler.MaxBatchSize}).");
    }
}
