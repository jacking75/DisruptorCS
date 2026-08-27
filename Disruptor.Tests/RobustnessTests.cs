using System.Diagnostics;
using Disruptor.Dsl;
using Disruptor.Game;
using Disruptor.WaitStrategy;
using Xunit;
using Xunit.Abstractions;

namespace Disruptor.Tests;

/// <summary>
/// §3.3 예외 핸들러 격리 · §3.4 스톨 rate-limit · §3.5 수명주기 가드.
/// </summary>
public class RobustnessTests
{
    private readonly ITestOutputHelper _output;

    public RobustnessTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 정적 싱글턴 기본 핸들러를 공유하면 프로세서 A 의 실패 스트릭을 프로세서 B 의 성공이
    /// 리셋해버려, 무한 오류 루프 감지가 영영 발동하지 않습니다.
    /// </summary>
    [Fact]
    public void DefaultExceptionHandler_IsNotSharedBetweenProcessors()
    {
        var a = ResilientExceptionHandler<TestEvent>.CreateDefault();
        var b = ResilientExceptionHandler<TestEvent>.CreateDefault();

        Assert.NotSame(a, b);

        a.HandleEventException(new InvalidOperationException(), 0, null);
        a.HandleEventException(new InvalidOperationException(), 1, null);

        Assert.Equal(2, a.ConsecutiveFailures);
        Assert.Equal(0, b.ConsecutiveFailures); // 격리되어 있다

        b.OnEventProcessed();
        Assert.Equal(2, a.ConsecutiveFailures); // b 의 성공이 a 를 리셋하지 않는다
    }

    [Fact]
    public void ConsecutiveFailures_TriggerHalt_PerProcessor()
    {
        const int limit = 5;
        var handler = new ResilientExceptionHandler<TestEvent>((_, _, _) => { }, maxConsecutiveFailures: limit);

        for (int i = 1; i < limit; i++)
            Assert.Equal(EventExceptionAction.Skip, handler.HandleEventException(new Exception(), i, null));

        Assert.Equal(EventExceptionAction.Halt, handler.HandleEventException(new Exception(), limit, null));
    }

    [Fact]
    public void SuccessfulBatch_ResetsFailureStreak()
    {
        var handler = new ResilientExceptionHandler<TestEvent>((_, _, _) => { }, maxConsecutiveFailures: 3);

        handler.HandleEventException(new Exception(), 0, null);
        handler.HandleEventException(new Exception(), 1, null);
        Assert.Equal(2, handler.ConsecutiveFailures);

        handler.OnEventProcessed();
        Assert.Equal(0, handler.ConsecutiveFailures);

        // 스트릭이 리셋되었으므로 다시 임계치까지 여유가 있다.
        Assert.Equal(EventExceptionAction.Skip, handler.HandleEventException(new Exception(), 2, null));
    }

    /// <summary>
    /// 지속 정체에서 게시 건수만큼 콜백이 발동하면 로그 폭주가 정체를 더 악화시킵니다.
    /// </summary>
    [Fact]
    public void StallReports_AreRateLimited()
    {
        int reports = 0;

        var options = new SequencerOptions
        {
            PipelineName = "rate-limit",
            StallThreshold = TimeSpan.FromMilliseconds(10),
            StallReportInterval = TimeSpan.FromSeconds(30), // 사실상 1회만 허용
            OnStall = _ => Interlocked.Increment(ref reports),
            Backoff = new TimeBoundedBackoff(TimeSpan.FromMilliseconds(60)),
        };

        var ring = RingBuffer<TestEvent>.Create(
            ProducerType.Single, () => new TestEvent(), 8, new YieldingWaitStrategy(), options);
        ring.AddGatingSequences(new Sequence());

        for (int i = 0; i < 8; i++)
            ring.Publish(ring.Next());

        // 여러 번 시도해도 보고는 1회로 제한되어야 한다.
        for (int i = 0; i < 5; i++)
            Assert.Throws<TimeoutException>(() => ring.Next());

        _output.WriteLine($"스톨 보고 {reports}회 (5회 시도)");
        Assert.Equal(1, reports);
    }

    [Fact]
    public void StallDetection_StillWorks_WithSpinOnlyBackoff()
    {
        StallInfo? captured = null;

        var options = new SequencerOptions
        {
            PipelineName = "stall-spin",
            StallThreshold = TimeSpan.FromMilliseconds(20),
            OnStall = info => captured = info,

            // 채우기와 블로킹 호출이 서로 다른 스레드에서 일어나므로
            // DEBUG 전용 단일 프로듀서 스레드 검증은 끈다 (그 검증은 BackpressureTests 가 다룬다).
            ValidateSingleProducerThread = false,
        };

        var ring = RingBuffer<TestEvent>.Create(
            ProducerType.Single, () => new TestEvent(), 8, new YieldingWaitStrategy(), options);

        var gating = new Sequence();
        ring.AddGatingSequences(gating);
        ring.SetGatingSequenceLabel(gating, "SlowHandler");

        for (int i = 0; i < 8; i++)
            ring.Publish(ring.Next());

        // 별도 스레드에서 블록시키고, 잠시 후 풀어준다.
        var blocked = BlockingCall.Start(() => ring.Next());
        Thread.Sleep(200);
        gating.SetValue(0);

        Assert.True(blocked.CompletedWithin(TimeSpan.FromSeconds(5)));
        Assert.Null(blocked.Exception);   // Next() 가 예외로 끝났다면 스톨 측정이 성립하지 않는다
        Assert.NotNull(captured);
        Assert.Equal("SlowHandler", captured!.Value.SlowestHandler);
    }

    [Fact]
    public void Halt_AfterDispose_DoesNotThrow()
    {
        var ring = RingBuffer<TestEvent>.CreateSingleProducer(
            () => new TestEvent(), 64, new YieldingWaitStrategy());
        var barrier = ring.NewBarrier();
        var processor = new BatchEventProcessor<TestEvent>(ring, barrier, new NoopHandler());

        processor.Dispose();

        // Dispose 후 Halt 는 조용히 무시되어야 한다 (ObjectDisposedException 금지).
        processor.Halt();
        processor.Halt();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var ring = RingBuffer<TestEvent>.CreateSingleProducer(
            () => new TestEvent(), 64, new YieldingWaitStrategy());
        var barrier = ring.NewBarrier();
        var processor = new BatchEventProcessor<TestEvent>(ring, barrier, new NoopHandler());

        processor.Dispose();
        processor.Dispose();
        processor.Dispose();
    }

    [Fact]
    public void Run_AfterDispose_Throws()
    {
        var ring = RingBuffer<TestEvent>.CreateSingleProducer(
            () => new TestEvent(), 64, new YieldingWaitStrategy());
        var barrier = ring.NewBarrier();
        var processor = new BatchEventProcessor<TestEvent>(ring, barrier, new NoopHandler());

        processor.Dispose();

        Assert.Throws<ObjectDisposedException>(() => processor.Run(CancellationToken.None));
    }

    [Fact]
    public void BatchMiddleException_ContinuesWithRemainderOfBatch()
    {
        const int count = 200;
        var processed = new List<long>();
        var errors = new List<long>();

        var handler = new RecordingHandler(processed, failAt: 50);

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), 1024, ProducerType.Single, new BlockingWaitStrategy(),
            new DisruptorOptions { Name = "batch-middle", AutoClearSlots = false });

        disruptor.SetDefaultExceptionHandler(new ResilientExceptionHandler<TestEvent>(
            (ex, seq, evt) => { lock (errors) errors.Add(seq); }));

        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(10));

        // 실패한 시퀀스 1건만 스킵되고 나머지는 전부 처리되어야 한다.
        Assert.Single(errors);
        Assert.Equal(50, errors[0]);
        Assert.Equal(count - 1, processed.Count);
        Assert.DoesNotContain(50L, processed);

        // 배치 중간에서 실패했어도 이후 구간이 순서대로 이어져야 한다.
        for (int i = 1; i < processed.Count; i++)
            Assert.True(processed[i] > processed[i - 1]);
    }

    private sealed class RecordingHandler : IEventHandler<TestEvent>
    {
        private readonly List<long> _processed;
        private readonly long _failAt;

        public RecordingHandler(List<long> processed, long failAt)
        {
            _processed = processed;
            _failAt = failAt;
        }

        public void OnEvent(TestEvent data, long sequence, bool endOfBatch)
        {
            if (sequence == _failAt)
                throw new InvalidOperationException($"의도적 예외 at {sequence}");

            _processed.Add(sequence);
        }
    }
}

/// <summary>
/// §3.6 Sharded + Block 정책 조합 가드.
/// </summary>
public class ShardGuardTests
{
    private sealed class FailingHandler : IEventHandler<TestEvent>
    {
        public void OnEvent(TestEvent data, long sequence, bool endOfBatch)
            => throw new InvalidOperationException("항상 실패");
    }

    [Fact]
    public void HealthSnapshot_ReportsPerShardState()
    {
        using var sharded = new ShardedDisruptor<TestEvent>(
            shardCount: 4, ringSizePerShard: 64,
            () => new TestEvent(),
            _ => new NoopHandler(),
            ProducerType.Multi,
            _ => new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "health", AutoClearSlots = false });

        sharded.Start();

        for (int i = 0; i < 100; i++)
            sharded.Publish(i, static (evt, seq) => evt.Sequence = seq);

        ShardHealth[] health = sharded.HealthSnapshot();

        Assert.Equal(4, health.Length);
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(i, health[i].Shard);
            Assert.False(health[i].Halted);
            Assert.InRange(health[i].Utilization, 0.0, 1.0);
        }

        sharded.Shutdown(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// 정지한 샤드에 Block 정책으로 게시하면 영구 대기합니다. fail-fast 되어야 합니다.
    /// </summary>
    [Fact]
    public void PublishToHaltedShard_FailsFast_InsteadOfBlockingForever()
    {
        using var sharded = new ShardedDisruptor<TestEvent>(
            shardCount: 2, ringSizePerShard: 16,
            () => new TestEvent(),
            _ => new FailingHandler(),
            ProducerType.Multi,
            _ => new YieldingWaitStrategy(),
            new DisruptorOptions { Name = "halted-shard", AutoClearSlots = false });

        // 첫 예외에서 즉시 정지시킨다.
        foreach (var shard in sharded.Shards)
            shard.SetDefaultExceptionHandler(new HaltOnExceptionHandler<TestEvent>((_, _, _) => { }));

        sharded.Start();

        // 각 샤드에 하나씩 보내 정지를 유발한다.
        sharded.TryPublish(0, static (evt, seq) => evt.Sequence = seq);
        sharded.TryPublish(1, static (evt, seq) => evt.Sequence = seq);

        var sw = Stopwatch.StartNew();
        while (!(sharded.IsShardHalted(sharded.ShardOf(0)) && sharded.IsShardHalted(sharded.ShardOf(1)))
               && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(10);
        }

        Assert.True(sharded.IsShardHalted(sharded.ShardOf(0)));

        // TryPublish 는 블록되지 않고 false 를 반환해야 한다.
        Assert.False(sharded.TryPublish(0, static (evt, seq) => evt.Sequence = seq));

        // Publish 는 명확한 예외로 거부해야 한다 (영구 대기 금지).
        Assert.Throws<ShardHaltedException>(() =>
            sharded.Publish(0, static (evt, seq) => evt.Sequence = seq));

        sharded.Shutdown(TimeSpan.FromMilliseconds(500));
    }
}

/// <summary>
/// §3.2 고분해능 타이머 스코프.
/// </summary>
public class HighResolutionTimerScopeTests
{
    [Fact]
    public void Scope_IsReferenceCounted_AndSafeToNest()
    {
        int before = HighResolutionTimerScope.ActiveScopeCount;

        using (var outer = new HighResolutionTimerScope())
        {
            using (var inner = new HighResolutionTimerScope())
            {
                if (OperatingSystem.IsWindows())
                {
                    Assert.True(outer.IsActive);
                    Assert.True(inner.IsActive);
                    Assert.Equal(before + 2, HighResolutionTimerScope.ActiveScopeCount);
                }
            }
        }

        Assert.Equal(before, HighResolutionTimerScope.ActiveScopeCount);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        int before = HighResolutionTimerScope.ActiveScopeCount;

        var scope = new HighResolutionTimerScope();
        scope.Dispose();
        scope.Dispose();

        Assert.Equal(before, HighResolutionTimerScope.ActiveScopeCount);
    }
}

/// <summary>
/// §2.5 StripedCounter — 쓰기 경합 없는 카운터.
/// </summary>
public class StripedCounterTests
{
    [Fact]
    public void SumsAcrossAllThreads()
    {
        var counter = new StripedCounter();
        const int threads = 8;
        const int perThread = 100_000;

        var workers = new Thread[threads];
        for (int t = 0; t < threads; t++)
        {
            workers[t] = new Thread(() =>
            {
                for (int i = 0; i < perThread; i++)
                    counter.Increment();
            })
            { IsBackground = true };
        }

        foreach (var w in workers) w.Start();
        foreach (var w in workers) w.Join();

        Assert.Equal((long)threads * perThread, counter.Value);
    }

    [Fact]
    public void Reset_ClearsAllStripes()
    {
        var counter = new StripedCounter();
        counter.Add(1000);
        Assert.Equal(1000, counter.Value);

        counter.Reset();
        Assert.Equal(0, counter.Value);
    }

    [Fact]
    public void StripeCount_IsPowerOfTwo()
    {
        var counter = new StripedCounter();
        Assert.True((counter.Stripes & (counter.Stripes - 1)) == 0);
    }
}
