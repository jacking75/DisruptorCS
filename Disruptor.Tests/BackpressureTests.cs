using Disruptor.WaitStrategy;
using Xunit;

namespace Disruptor.Tests;

/// <summary>
/// 링 버퍼의 근본 계약 — "미소비 슬롯은 절대 덮어쓰지 않는다" — 를 검증합니다.
/// </summary>
/// <remarks>
/// 이 스위트가 없어서 <c>MultiProducerSequencer.Next()</c>의
/// <c>do…while</c> + <c>continue</c> 이식 버그가 오래 살아남았습니다.
/// (백프레셔가 완전히 무력화되어 이벤트 99.3%가 유실되고 컨슈머가 영구 정지)
/// </remarks>
public class BackpressureTests
{
    [Theory]
    [InlineData(ProducerType.Single)]
    [InlineData(ProducerType.Multi)]
    public void Next_MustBlock_WhenBufferIsFull(ProducerType producerType)
    {
        const int ringSize = 8;

        // 채우기와 블로킹 호출이 서로 다른 스레드에서 일어나므로
        // DEBUG 전용 단일 프로듀서 스레드 검증은 끈다 (그 검증은 아래 전용 테스트에서 다룬다).
        var ring = RingBuffer<TestEvent>.Create(
            producerType, () => new TestEvent(), ringSize, new YieldingWaitStrategy(),
            new SequencerOptions { ValidateSingleProducerThread = false });

        // 아무것도 소비하지 않는 게이팅 시퀀스
        var gating = new Sequence();
        ring.AddGatingSequences(gating);

        // 버퍼를 정확히 채운다
        for (int i = 0; i < ringSize; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        Assert.Equal(ringSize - 1, ring.Cursor);
        Assert.Equal(0, ring.RemainingCapacity);

        // 한 칸 더 요청하면 반드시 블록되어야 한다
        var blocked = BlockingCall.Start(() => ring.Next());

        Assert.False(blocked.CompletedWithin(TimeSpan.FromMilliseconds(500)),
            "버퍼가 가득 찼는데 Next()가 반환되었습니다 — 백프레셔 계약 위반 " +
            "(미소비 슬롯을 덮어쓰게 되어 컨슈머가 영구 정지합니다).");

        // 한 칸 비우면 진행되어야 한다
        gating.SetValue(0);
        Assert.True(blocked.CompletedWithin(TimeSpan.FromSeconds(5)), "슬롯을 비웠는데도 Next()가 진행되지 않았습니다.");
        Assert.Equal(ringSize, blocked.Result);
    }

    [Theory]
    [InlineData(ProducerType.Single)]
    [InlineData(ProducerType.Multi)]
    public void TryNext_ReturnsFalse_WhenBufferIsFull(ProducerType producerType)
    {
        const int ringSize = 8;

        var ring = RingBuffer<TestEvent>.Create(
            producerType, () => new TestEvent(), ringSize, new YieldingWaitStrategy());
        ring.AddGatingSequences(new Sequence());

        for (int i = 0; i < ringSize; i++)
            ring.Publish(ring.Next());

        Assert.False(ring.TryNext(out _));
        Assert.False(ring.HasAvailableCapacity(1));
    }

    [Theory]
    [InlineData(ProducerType.Single)]
    [InlineData(ProducerType.Multi)]
    public void TryNext_WithTimeout_FailsWithinExpectedTime(ProducerType producerType)
    {
        var ring = RingBuffer<TestEvent>.Create(
            producerType, () => new TestEvent(), 8, new YieldingWaitStrategy());
        ring.AddGatingSequences(new Sequence());

        for (int i = 0; i < 8; i++)
            ring.Publish(ring.Next());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool acquired = ring.TryNext(out _, TimeSpan.FromMilliseconds(100));
        sw.Stop();

        Assert.False(acquired);
        Assert.InRange(sw.ElapsedMilliseconds, 50, 2000);
    }

    [Theory]
    [InlineData(ProducerType.Single)]
    [InlineData(ProducerType.Multi)]
    public void Next_WithCancellation_ThrowsInsteadOfHangingForever(ProducerType producerType)
    {
        var ring = RingBuffer<TestEvent>.Create(
            producerType, () => new TestEvent(), 8, new YieldingWaitStrategy());
        ring.AddGatingSequences(new Sequence());

        for (int i = 0; i < 8; i++)
            ring.Publish(ring.Next());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        Assert.Throws<OperationCanceledException>(() => ring.Next(cts.Token));
    }

    [Theory]
    [InlineData(ProducerType.Single)]
    [InlineData(ProducerType.Multi)]
    public void RemainingCapacity_TracksConsumption(ProducerType producerType)
    {
        const int ringSize = 16;

        var ring = RingBuffer<TestEvent>.Create(
            producerType, () => new TestEvent(), ringSize, new YieldingWaitStrategy());
        var gating = new Sequence();
        ring.AddGatingSequences(gating);

        Assert.Equal(ringSize, ring.RemainingCapacity);

        for (int i = 0; i < 4; i++)
            ring.Publish(ring.Next());

        Assert.Equal(ringSize - 4, ring.RemainingCapacity);

        gating.SetValue(1); // 2건 소비
        Assert.Equal(ringSize - 2, ring.RemainingCapacity);
    }

    [Fact]
    public void ProducerBackoff_CanFailFast_InsteadOfBlockingForever()
    {
        var options = new SequencerOptions
        {
            PipelineName = "test",
            Backoff = new TimeBoundedBackoff(TimeSpan.FromMilliseconds(100)),
        };

        var ring = RingBuffer<TestEvent>.Create(
            ProducerType.Multi, () => new TestEvent(), 8, new YieldingWaitStrategy(), options);
        ring.AddGatingSequences(new Sequence());

        for (int i = 0; i < 8; i++)
            ring.Publish(ring.Next());

        Assert.Throws<TimeoutException>(() => ring.Next());
    }

    /// <summary>
    /// ProducerType.Single 을 여러 스레드에서 쓰면 Release 에서는 조용히 데이터가 깨진다.
    /// DEBUG 빌드는 이를 즉시 예외로 잡아야 한다.
    /// </summary>
    [Fact]
    public void SingleProducer_MisuseFromMultipleThreads_IsDetectedInDebugBuilds()
    {
        var ring = RingBuffer<TestEvent>.CreateSingleProducer(
            () => new TestEvent(), 1024, new YieldingWaitStrategy());

        ring.Publish(ring.Next());   // 이 스레드가 "그 프로듀서"로 등록된다

        Exception? captured = null;
        var other = new Thread(() =>
        {
            try { ring.Publish(ring.Next()); }
            catch (Exception ex) { captured = ex; }
        })
        { IsBackground = true };

        other.Start();
        other.Join(TimeSpan.FromSeconds(5));

#if DEBUG
        Assert.IsType<InvalidOperationException>(captured);
        Assert.Contains("ProducerType.Multi", captured!.Message);
#else
        // Release 에서는 검사가 제거되므로 예외가 나지 않는다 (성능 우선).
        Assert.Null(captured);
#endif
    }

    [Fact]
    public void StallDetection_ReportsSlowestHandler()
    {
        StallInfo? captured = null;

        var options = new SequencerOptions
        {
            PipelineName = "stall-test",
            StallThreshold = TimeSpan.FromMilliseconds(50),
            OnStall = info => captured = info,
            Backoff = new TimeBoundedBackoff(TimeSpan.FromMilliseconds(400)),
        };

        var ring = RingBuffer<TestEvent>.Create(
            ProducerType.Single, () => new TestEvent(), 8, new YieldingWaitStrategy(), options);

        var gating = new Sequence();
        ring.AddGatingSequences(gating);
        ring.SetGatingSequenceLabel(gating, "SlowDbHandler");

        for (int i = 0; i < 8; i++)
            ring.Publish(ring.Next());

        Assert.Throws<TimeoutException>(() => ring.Next());

        Assert.NotNull(captured);
        Assert.Equal("stall-test", captured!.Value.PipelineName);
        Assert.Equal("SlowDbHandler", captured.Value.SlowestHandler);
        Assert.True(captured.Value.ElapsedMs >= 50);
    }
}
