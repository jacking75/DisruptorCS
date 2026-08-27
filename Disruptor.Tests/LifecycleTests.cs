using System.Diagnostics;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;
using Xunit;
using Xunit.Abstractions;

namespace Disruptor.Tests;

/// <summary>
/// 시작/종료 계약과 예외 정책을 검증합니다.
/// </summary>
public class LifecycleTests
{
    private readonly ITestOutputHelper _output;

    public LifecycleTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Shutdown_ImmediatelyAfterStart_CompletesQuickly()
    {
        var sw = Stopwatch.StartNew();

        using (var disruptor = new Dsl.Disruptor<TestEvent>(
                   () => new TestEvent(), 1024, ProducerType.Single, new BlockingWaitStrategy()))
        {
            disruptor.HandleEventsWith(new NoopHandler());
            disruptor.Start();
        }

        sw.Stop();
        _output.WriteLine($"Start→Shutdown: {sw.ElapsedMilliseconds}ms");

        // Start()가 프로세서 준비를 기다리지 않으면 정지 신호를 잃고 타임아웃(5초)까지 지연된다.
        Assert.True(sw.ElapsedMilliseconds < 1000,
            $"Start 직후 Shutdown이 {sw.ElapsedMilliseconds}ms 걸렸습니다 — Start/Shutdown 경쟁이 있습니다.");
    }

    [Fact]
    public void Shutdown_DrainsPendingEvents()
    {
        const int count = 10_000;
        var handler = new VerifyingHandler();

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), 4096, ProducerType.Single, new BlockingWaitStrategy(),
            new DisruptorOptions { Name = "drain-test", AutoClearSlots = false });

        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        for (int i = 0; i < count; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        disruptor.Shutdown(TimeSpan.FromSeconds(10));

        Assert.Equal(count, handler.Count);
    }

    [Fact]
    public void Start_Twice_Throws()
    {
        using var disruptor = new Dsl.Disruptor<TestEvent>(() => new TestEvent(), 64);
        disruptor.HandleEventsWith(new NoopHandler());
        disruptor.Start();

        Assert.Throws<InvalidOperationException>(() => disruptor.Start());
    }

    [Fact]
    public void HandleEventsWith_AfterStart_Throws()
    {
        using var disruptor = new Dsl.Disruptor<TestEvent>(() => new TestEvent(), 64);
        disruptor.HandleEventsWith(new NoopHandler());
        disruptor.Start();

        Assert.Throws<InvalidOperationException>(() => disruptor.HandleEventsWith(new NoopHandler()));
    }

    [Fact]
    public void Shutdown_IsIdempotent()
    {
        var disruptor = new Dsl.Disruptor<TestEvent>(() => new TestEvent(), 64);
        disruptor.HandleEventsWith(new NoopHandler());
        disruptor.Start();

        disruptor.Shutdown(TimeSpan.FromSeconds(2));
        disruptor.Shutdown(TimeSpan.FromSeconds(2));
        disruptor.Dispose();
    }

    /// <summary>
    /// 핸들러 예외가 프로세스를 죽이면 안 된다 — 게임 서버에서 가장 중요한 계약.
    /// </summary>
    [Fact]
    public void HandlerException_DoesNotKillProcess_AndSkipsEvent()
    {
        const int count = 100;
        var errors = new List<long>();

        var handler = new ThrowingHandler(seq => seq % 10 == 0);

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), 1024, ProducerType.Single, new BlockingWaitStrategy(),
            new DisruptorOptions { Name = "exception-test", AutoClearSlots = false });

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

        // 10건은 예외, 90건은 정상 처리되어야 한다. 프로세스는 살아 있다.
        Assert.Equal(10, errors.Count);
        Assert.Equal(90, handler.Processed);
    }

    [Fact]
    public void HaltAction_StopsOnlyThatProcessor_AndRaisesEvent()
    {
        IEventProcessor? haltedProcessor = null;
        Exception? haltedException = null;

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), 1024, ProducerType.Single, new BlockingWaitStrategy(),
            new DisruptorOptions { Name = "halt-test", AutoClearSlots = false });

        disruptor.SetDefaultExceptionHandler(new HaltOnExceptionHandler<TestEvent>((_, _, _) => { }));
        disruptor.ProcessorHalted += (p, ex) => { haltedProcessor = p; haltedException = ex; };

        var handler = new ThrowingHandler(seq => seq == 5);
        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        for (int i = 0; i < 10; i++)
        {
            long seq = ring.Next();
            ring[seq].Sequence = seq;
            ring.Publish(seq);
        }

        // 프로세서 정지 이벤트를 기다린다
        var sw = Stopwatch.StartNew();
        while (haltedProcessor is null && sw.Elapsed < TimeSpan.FromSeconds(5))
            Thread.Sleep(10);

        Assert.NotNull(haltedProcessor);
        Assert.IsType<InvalidOperationException>(haltedException);
        Assert.Equal(5, handler.Processed); // 0..4 만 처리

        disruptor.Shutdown(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void RetryAction_ReprocessesSameEvent()
    {
        int attempts = 0;
        int failures = 0;

        var handler = new DelegateHandler((data, seq, eob) =>
        {
            if (seq != 3)
                return;

            attempts++;
            if (attempts <= 2)
            {
                failures++;
                throw new InvalidOperationException("일시적 실패");
            }
        });

        using var disruptor = new Dsl.Disruptor<TestEvent>(
            () => new TestEvent(), 1024, ProducerType.Single, new BlockingWaitStrategy(),
            new DisruptorOptions { Name = "retry-test", AutoClearSlots = false });

        disruptor.SetDefaultExceptionHandler(new RetryTwiceHandler());
        disruptor.HandleEventsWith(handler);
        var ring = disruptor.Start();

        for (int i = 0; i < 10; i++)
            ring.Publish(ring.Next());

        disruptor.Shutdown(TimeSpan.FromSeconds(5));

        Assert.Equal(2, failures);         // 두 번 실패
        Assert.Equal(3, attempts);         // 세 번째 시도에서 성공
        Assert.Equal(10, handler.Count);   // 재시도 끝에 전부 처리
    }

    [Fact]
    public void OnStartAndOnShutdown_AreInvokedExactlyOnce()
    {
        var handler = new LifecycleTrackingHandler();

        using (var disruptor = new Dsl.Disruptor<TestEvent>(
                   () => new TestEvent(), 64, ProducerType.Single, new BlockingWaitStrategy()))
        {
            disruptor.HandleEventsWith(handler);
            disruptor.Start();
            Assert.Equal(1, handler.Starts);   // Start()는 준비 완료를 기다리므로 이미 1이어야 한다
        }

        Assert.Equal(1, handler.Starts);
        Assert.Equal(1, handler.Shutdowns);
    }

    private sealed class DelegateHandler : IEventHandler<TestEvent>
    {
        private readonly Action<TestEvent, long, bool> _action;
        private long _count;

        public DelegateHandler(Action<TestEvent, long, bool> action) => _action = action;

        public long Count => Volatile.Read(ref _count);

        public void OnEvent(TestEvent data, long sequence, bool endOfBatch)
        {
            _action(data, sequence, endOfBatch);
            Interlocked.Increment(ref _count);
        }
    }

    private sealed class RetryTwiceHandler : IExceptionHandler<TestEvent>
    {
        public EventExceptionAction HandleEventException(Exception ex, long sequence, TestEvent? @event)
            => EventExceptionAction.Retry;

        public void HandleOnStartException(Exception ex) { }

        public void HandleOnShutdownException(Exception ex) { }
    }

    private sealed class LifecycleTrackingHandler : IEventHandler<TestEvent>
    {
        private int _starts;
        private int _shutdowns;

        public int Starts => Volatile.Read(ref _starts);
        public int Shutdowns => Volatile.Read(ref _shutdowns);

        public void OnStart() => Interlocked.Increment(ref _starts);
        public void OnShutdown() => Interlocked.Increment(ref _shutdowns);
        public void OnEvent(TestEvent data, long sequence, bool endOfBatch) { }
    }
}
