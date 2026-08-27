using Disruptor.Dsl;
using Disruptor.WaitStrategy;
using Xunit;

namespace Disruptor.Tests;

/// <summary>
/// §4.2 EventPoller — 전용 스레드 없이 호출자 스레드에서 소비하는 경로.
/// </summary>
public class EventPollerTests
{
    [Theory]
    [InlineData(ProducerType.Single)]
    [InlineData(ProducerType.Multi)]
    public void Poll_ConsumesAllPublishedEvents_InOrder(ProducerType producerType)
    {
        const int count = 10_000;

        var ring = RingBuffer<TestEvent>.Create(
            producerType, () => new TestEvent(), 1024, new YieldingWaitStrategy());

        using var poller = ring.NewPoller();

        var seen = new List<long>(count);
        int published = 0;

        while (published < count)
        {
            // 링이 가득 차기 전에 조금씩 게시하고 그때마다 폴링한다.
            for (int i = 0; i < 100 && published < count; i++, published++)
            {
                long seq = ring.Next();
                ring[seq].Sequence = seq;
                ring.Publish(seq);
            }

            poller.Poll((evt, seq, eob) =>
            {
                Assert.Equal(seq, evt.Sequence);
                seen.Add(seq);
                return true;
            }, maxEvents: 1024);
        }

        // 남은 것 마저 소비
        while (poller.Poll((evt, seq, eob) => { seen.Add(seq); return true; }, 1024) == PollState.Processing
               && seen.Count < count)
        {
        }

        Assert.Equal(count, seen.Count);
        for (int i = 0; i < count; i++)
            Assert.Equal(i, seen[i]);
    }

    [Fact]
    public void Poll_ReturnsIdle_WhenNothingPublished()
    {
        var ring = RingBuffer<TestEvent>.CreateSingleProducer(
            () => new TestEvent(), 64, new YieldingWaitStrategy());

        using var poller = ring.NewPoller();

        Assert.Equal(PollState.Idle, poller.Poll((_, _, _) => true));
    }

    [Fact]
    public void Poll_RespectsMaxEvents_SoFrameBudgetIsHonored()
    {
        var ring = RingBuffer<TestEvent>.CreateSingleProducer(
            () => new TestEvent(), 1024, new YieldingWaitStrategy());

        using var poller = ring.NewPoller();

        for (int i = 0; i < 500; i++)
            ring.Publish(ring.Next());

        int processed = 0;
        poller.Poll((_, _, _) => { processed++; return true; }, maxEvents: 100);

        Assert.Equal(100, processed);
        Assert.Equal(400, poller.Backlog);
    }

    [Fact]
    public void Poll_StopsEarly_WhenHandlerReturnsFalse()
    {
        var ring = RingBuffer<TestEvent>.CreateSingleProducer(
            () => new TestEvent(), 1024, new YieldingWaitStrategy());

        using var poller = ring.NewPoller();

        for (int i = 0; i < 100; i++)
            ring.Publish(ring.Next());

        int processed = 0;
        poller.Poll((_, _, _) => { processed++; return processed < 10; });

        Assert.Equal(10, processed);

        // 중단된 이벤트까지는 처리된 것으로 기록되어야 한다.
        Assert.Equal(9, poller.Sequence.Value);
    }

    [Fact]
    public void Poll_RecordsProgress_EvenWhenHandlerThrows()
    {
        var ring = RingBuffer<TestEvent>.CreateSingleProducer(
            () => new TestEvent(), 1024, new YieldingWaitStrategy());

        using var poller = ring.NewPoller();

        for (int i = 0; i < 100; i++)
            ring.Publish(ring.Next());

        Assert.Throws<InvalidOperationException>(() =>
            poller.Poll((_, seq, _) =>
            {
                if (seq == 5) throw new InvalidOperationException();
                return true;
            }));

        // 예외가 나도 게이팅이 멈추면 안 된다 — 처리 완료분(0..4)까지는 반영되어야 한다.
        Assert.Equal(4, poller.Sequence.Value);
    }

    [Fact]
    public void Poller_ParticipatesInGating_AndReleasesOnDispose()
    {
        var ring = RingBuffer<TestEvent>.CreateSingleProducer(
            () => new TestEvent(), 8, new YieldingWaitStrategy());

        var poller = ring.NewPoller();

        // 폴러가 게이팅에 등록되었으므로 폴링하지 않으면 버퍼가 찬다.
        for (int i = 0; i < 8; i++)
            ring.Publish(ring.Next());

        Assert.False(ring.TryNext(out _));

        // Dispose 하면 게이팅에서 빠져 프로듀서가 다시 진행할 수 있다.
        poller.Dispose();
        Assert.True(ring.TryNext(out _));
    }

    [Fact]
    public void Poller_AfterHandlerStage_SeesOnlyProcessedEvents()
    {
        const int count = 2_000;

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), 1024, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "poller-chain", AutoClearSlots = false });

        var upstream = new VerifyingHandler();
        var group = disruptor.HandleEventsWith(upstream);
        var ring = disruptor.Start();

        using var poller = ring.NewPoller(group.Sequences);

        int seen = 0;
        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);

            poller.Poll((evt, s, _) =>
            {
                // 폴러가 본 시퀀스는 상위 핸들러가 이미 처리한 것이어야 한다.
                Assert.True(upstream.LastSequence >= s);
                seen++;
                return true;
            });
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (seen < count && sw.Elapsed < TimeSpan.FromSeconds(10))
            poller.Poll((_, _, _) => { seen++; return true; }, 1024);

        disruptor.Shutdown(TimeSpan.FromSeconds(10));

        Assert.Equal(count, seen);
    }

    [Fact]
    public void ValuePoller_DeliversEventsByRef()
    {
        var ring = ValueRingBuffer<TestValueEvent>.CreateSingleProducer(1024, new YieldingWaitStrategy());
        using var poller = ring.NewPoller();

        for (int i = 0; i < 200; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring[seq].Value = i * 3;
            ring.Publish(seq);
        }

        long sum = 0;
        int seen = 0;
        poller.Poll((ref TestValueEvent evt, long seq, bool eob) =>
        {
            Assert.Equal(seq, evt.Sequence);
            sum += evt.Value;
            seen++;
            return true;
        }, maxEvents: 1024);

        Assert.Equal(200, seen);
        Assert.Equal(Enumerable.Range(0, 200).Sum(i => (long)(i * 3)), sum);
    }

    [Fact]
    public void Poll_AfterDispose_Throws()
    {
        var ring = RingBuffer<TestEvent>.CreateSingleProducer(
            () => new TestEvent(), 64, new YieldingWaitStrategy());

        var poller = ring.NewPoller();
        poller.Dispose();

        Assert.Throws<ObjectDisposedException>(() => poller.Poll((_, _, _) => true));
    }
}
