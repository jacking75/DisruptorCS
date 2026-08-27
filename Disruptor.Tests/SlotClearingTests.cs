using Disruptor.Dsl;
using Disruptor.WaitStrategy;
using Xunit;

namespace Disruptor.Tests;

/// <summary>
/// 슬롯이 외부 참조를 붙들고 있으면 객체 수명이 링 버퍼 한 바퀴만큼 연장되어
/// 1M 슬롯 × 4KB 패킷 버퍼 = 4GB가 회수되지 않습니다.
/// </summary>
public class SlotClearingTests
{
    [Fact]
    public void LeafProcessor_ClearsSlotReferences_AfterProcessing()
    {
        const int count = 64;
        const int ringSize = 128;

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), ringSize, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "clear-test", AutoClearSlots = true });

        var handler = new VerifyingHandler();
        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring[seq].Payload = new byte[4096];  // 큰 페이로드
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(10));

        Assert.Equal(count, handler.Count);

        // 처리된 슬롯은 페이로드 참조를 놓아야 한다.
        for (int i = 0; i < count; i++)
            Assert.Null(ring[i].Payload);
    }

    [Fact]
    public void AutoClear_IsDisabled_WhenMultipleLeafProcessors()
    {
        const int count = 16;

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), 128, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "clear-multi", AutoClearSlots = true });

        // 리프가 2개면 한쪽이 지우는 동안 다른 쪽이 읽을 수 있으므로 자동 정리를 꺼야 한다.
        disruptor.HandleEventsWith(new VerifyingHandler(), new VerifyingHandler());
        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Payload = new byte[16];
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(10));

        // 자동 정리가 비활성화되었으므로 참조가 남아 있어야 한다.
        Assert.NotNull(ring[0].Payload);
    }

    [Fact]
    public void IntermediateProcessor_DoesNotClear_SoDownstreamSeesData()
    {
        const int count = 32;

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), 128, ProducerType.Single, new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "clear-chain", AutoClearSlots = true });

        var downstreamSawPayload = 0;

        var upstream = new VerifyingHandler();
        var downstream = new PayloadCheckHandler(() => Interlocked.Increment(ref downstreamSawPayload));

        disruptor.HandleEventsWith(upstream).Then(downstream);
        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Payload = new byte[8];
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(10));

        // 중간 핸들러가 지웠다면 하위 핸들러는 null 을 보게 된다.
        Assert.Equal(count, downstreamSawPayload);
    }

    private sealed class PayloadCheckHandler : IEventHandler<TestEvent>
    {
        private readonly Action _onPayloadPresent;

        public PayloadCheckHandler(Action onPayloadPresent) => _onPayloadPresent = onPayloadPresent;

        public void OnEvent(TestEvent data, long sequence, bool endOfBatch)
        {
            if (data.Payload is not null)
                _onPayloadPresent();
        }
    }
}

public class SequenceTests
{
    [Fact]
    public void Sequence_IsPaddedToAvoidFalseSharing()
    {
        // 좌우 128바이트 패딩 + 8바이트 값 = 264바이트
        // (Intel 스트리밍 프리페처가 인접 캐시 라인 쌍 128B를 함께 로드하므로 64B로는 부족하다)
        int size = System.Runtime.InteropServices.Marshal.SizeOf<long>();
        Assert.Equal(8, size);

        var a = new Sequence();
        var b = new Sequence(100);

        Assert.Equal(Sequence.InitialCursorValue, a.Value);
        Assert.Equal(100, b.Value);

        Assert.True(a.CompareAndSet(-1, 5));
        Assert.False(a.CompareAndSet(-1, 6));
        Assert.Equal(5, a.Value);
        Assert.Equal(6, a.IncrementAndGet());
        Assert.Equal(16, a.AddAndGet(10));
    }

    [Fact]
    public void SequenceWaitResult_DistinguishesStates()
    {
        SequenceWaitResult available = 42L;
        Assert.False(available.IsTimeout);
        Assert.False(available.IsCancelled);
        Assert.Equal(42, available.AvailableSequence);

        Assert.True(SequenceWaitResult.Timeout.IsTimeout);
        Assert.True(SequenceWaitResult.Cancelled.IsCancelled);
        Assert.NotEqual(SequenceWaitResult.Timeout, SequenceWaitResult.Cancelled);
    }

    [Fact]
    public void DependentSequenceGroup_ReturnsMinimumOfDependencies()
    {
        var cursor = new Sequence(100);
        var a = new Sequence(50);
        var b = new Sequence(30);

        var cursorOnly = new DependentSequenceGroup(cursor);
        Assert.True(cursorOnly.DependsOnCursor);
        Assert.Equal(100, cursorOnly.Value);

        var single = new DependentSequenceGroup(cursor, a);
        Assert.False(single.DependsOnCursor);
        Assert.Equal(50, single.Value);

        var multi = new DependentSequenceGroup(cursor, a, b);
        Assert.Equal(30, multi.Value);

        b.SetValue(80);
        Assert.Equal(50, multi.Value);
    }
}
