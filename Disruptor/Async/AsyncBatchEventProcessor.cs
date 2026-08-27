using System.Runtime.CompilerServices;

namespace Disruptor.Async;

/// <summary>
/// 배치를 <c>await</c> 로 처리하는 핸들러.
/// </summary>
/// <remarks>
/// <para>
/// <b>순서 보장</b>: <see cref="OnBatchAsync"/> 를 await 하는 동안 프로세서는 시퀀스를 전진시키지 않으므로
/// <b>배치 단위 순서는 유지됩니다.</b> 배치 내부를 병렬화하는 것은 핸들러의 책임이며,
/// 그렇게 하면 배치 안에서의 순서는 보장되지 않습니다.
/// </para>
/// <para>
/// <b>span 을 받지 않는 이유</b>: <c>ref struct</c> 는 <c>await</c> 를 건너 살아남을 수 없습니다.
/// 대신 시퀀스 범위를 받고 링 버퍼에서 직접 읽으세요 — 슬롯은 시퀀스가 전진하기 전까지 유효합니다.
/// </para>
/// </remarks>
public interface IAsyncBatchEventHandler<T> where T : class
{
    /// <summary>
    /// [<paramref name="fromSequence"/>, <paramref name="toSequence"/>] 구간을 처리합니다.
    /// </summary>
    /// <param name="ringBuffer">슬롯을 읽을 링 버퍼</param>
    /// <param name="fromSequence">처리 시작 시퀀스 (포함)</param>
    /// <param name="toSequence">처리 종료 시퀀스 (포함)</param>
    /// <param name="cancellationToken">종료 신호</param>
    ValueTask OnBatchAsync(RingBuffer<T> ringBuffer, long fromSequence, long toSequence,
                           CancellationToken cancellationToken);

    ValueTask OnTimeoutAsync(long lastProcessedSequence, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    ValueTask OnStartAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    ValueTask OnShutdownAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// <c>Task</c> 기반으로 실행되는 프로세서. 전용 스레드를 점유하지 않습니다.
/// </summary>
/// <remarks>
/// <see cref="AsyncWaitStrategy"/> 와 함께 사용해야 실제로 스레드를 놓습니다.
/// 다른 전략과 쓰면 동기 대기로 폴백하므로 스레드를 계속 점유합니다.
/// </remarks>
public sealed class AsyncBatchEventProcessor<T> : IEventProcessor, IDisposable where T : class
{
    private readonly RingBuffer<T> _ringBuffer;
    private readonly ISequenceBarrier _barrier;
    private readonly IAsyncBatchEventHandler<T> _handler;
    private readonly IAsyncWaitStrategy? _asyncStrategy;
    private readonly Sequence _sequence = new();
    private readonly CancellationTokenSource _halt = new();
    private readonly bool _signalNeeded;

    private IExceptionHandler<T> _exceptionHandler;
    private volatile int _running;
    private int _disposed;

    public AsyncBatchEventProcessor(
        RingBuffer<T> ringBuffer,
        ISequenceBarrier sequenceBarrier,
        IAsyncBatchEventHandler<T> handler,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(ringBuffer);
        ArgumentNullException.ThrowIfNull(sequenceBarrier);
        ArgumentNullException.ThrowIfNull(handler);

        _ringBuffer = ringBuffer;
        _barrier = sequenceBarrier;
        _handler = handler;
        _asyncStrategy = sequenceBarrier.WaitStrategy as IAsyncWaitStrategy;
        _signalNeeded = sequenceBarrier.WaitStrategy.IsBlockingStrategy;
        _exceptionHandler = ResilientExceptionHandler<T>.CreateDefault();
        Name = name ?? handler.GetType().Name;
    }

    public Sequence Sequence => _sequence;

    public string Name { get; }

    public bool IsRunning => _running == 1;

    /// <inheritdoc cref="BatchEventProcessor{T}.MaxBatchSize"/>
    public int MaxBatchSize { get; set; } = int.MaxValue;

    public HandlerRecorder? Recorder { get; set; }

    public event Action<IEventProcessor, Exception?>? Halted;

    public void SetExceptionHandler(IExceptionHandler<T> exceptionHandler)
    {
        ArgumentNullException.ThrowIfNull(exceptionHandler);
        _exceptionHandler = exceptionHandler;
    }

    /// <summary>
    /// 동기 진입점. <see cref="IEventProcessor"/> 계약을 위해 제공하며 내부적으로 비동기 루프를 기다립니다.
    /// </summary>
    public void Run(CancellationToken cancellationToken, CountdownEvent? startedGate = null)
        => RunAsync(cancellationToken, startedGate).GetAwaiter().GetResult();

    /// <summary>비동기 이벤트 루프. 이것을 직접 호출하면 스레드를 점유하지 않습니다.</summary>
    public async Task RunAsync(CancellationToken cancellationToken, CountdownEvent? startedGate = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            startedGate?.Signal();
            throw new InvalidOperationException($"'{Name}' 프로세서가 이미 실행 중입니다.");
        }

        using CancellationTokenRegistration registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static s => TryCancel((CancellationTokenSource)s!), _halt)
            : default;

        bool signalled = false;

        try
        {
            try
            {
                await _handler.OnStartAsync(_halt.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _exceptionHandler.HandleOnStartException(ex);
            }

            startedGate?.Signal();
            signalled = true;

            await ProcessEventsAsync(_halt.Token).ConfigureAwait(false);
        }
        finally
        {
            if (!signalled)
                startedGate?.Signal();

            try
            {
                await _handler.OnShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _exceptionHandler.HandleOnShutdownException(ex);
            }

            _running = 0;
        }
    }

    private async Task ProcessEventsAsync(CancellationToken ct)
    {
        long nextSequence = _sequence.Value + 1L;

        while (!ct.IsCancellationRequested)
        {
            SequenceWaitResult waitResult = _asyncStrategy is not null
                ? await _asyncStrategy.WaitForAsync(nextSequence, _barrier.Dependents, ct).ConfigureAwait(false)
                : _barrier.WaitFor(nextSequence, ct);

            if (waitResult.IsCancelled)
                break;

            if (waitResult.IsTimeout)
            {
                await SafeTimeoutAsync(nextSequence - 1, ct).ConfigureAwait(false);
                continue;
            }

            long available = waitResult.AvailableSequence;

            // 비동기 전략은 배리어를 우회하므로 MP 연속 게시 상한을 여기서 직접 적용한다.
            if (_asyncStrategy is not null && _barrier.Dependents.DependsOnCursor)
                available = _ringBuffer.GetHighestPublishedSequence(nextSequence, available);

            if (available < nextSequence)
            {
                await Task.Yield();
                continue;
            }

            if (available - nextSequence + 1 > MaxBatchSize)
                available = nextSequence + MaxBatchSize - 1;

            long batchSize = available - nextSequence + 1;

            try
            {
                await _handler.OnBatchAsync(_ringBuffer, nextSequence, available, ct).ConfigureAwait(false);
                nextSequence = available + 1;
                _exceptionHandler.OnEventProcessed();
            }
            catch (Exception ex)
            {
                Recorder?.OnHandlerError();

                switch (SafeHandleException(ex, nextSequence))
                {
                    case EventExceptionAction.Skip:
                        nextSequence = available + 1; // 조각 전체 스킵
                        break;

                    case EventExceptionAction.Retry:
                        continue; // 같은 구간 재시도

                    default:
                        _sequence.SetValue(nextSequence - 1);
                        Halted?.Invoke(this, ex);
                        return;
                }
            }

            _sequence.SetValue(available);
            Recorder?.OnBatchCompleted(batchSize);

            if (_signalNeeded)
                _barrier.WaitStrategy.SignalAllWhenBlocking();
        }
    }

    private async ValueTask SafeTimeoutAsync(long lastProcessed, CancellationToken ct)
    {
        try
        {
            await _handler.OnTimeoutAsync(lastProcessed, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SafeHandleException(ex, lastProcessed);
        }
    }

    private EventExceptionAction SafeHandleException(Exception ex, long sequence)
    {
        try
        {
            return _exceptionHandler.HandleEventException(ex, sequence, null);
        }
        catch (Exception handlerFailure)
        {
            Console.Error.WriteLine(
                $"[Disruptor][ERROR] '{Name}'의 예외 핸들러가 실패했습니다: {handlerFailure}");
            return EventExceptionAction.Skip;
        }
    }

    public void Halt()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        TryCancel(_halt);
        _barrier.WaitStrategy.SignalAllWhenBlocking();
    }

    private static void TryCancel(CancellationTokenSource cts)
    {
        try
        {
            if (!cts.IsCancellationRequested)
                cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _halt.Dispose();
    }
}

/// <summary>
/// 링 버퍼의 이벤트를 <see cref="IAsyncEnumerable{T}"/> 로 노출하는 브리지.
/// </summary>
/// <remarks>
/// <b>성능 경로가 아니라 통합 경로입니다.</b> 슬롯 참조를 <c>await</c> 너머로 유출할 수 없으므로
/// 스냅샷 복사가 발생합니다. 대시보드, 어드민 스트림, 테스트에 사용하세요 —
/// 실제 파이프라인에는 동기 핸들러나 <see cref="AsyncBatchEventProcessor{T}"/> 를 쓰세요.
/// </remarks>
public static class AsyncBridge
{
    /// <summary>
    /// 링 버퍼의 이벤트를 스냅샷으로 변환해 비동기 스트림으로 노출합니다.
    /// </summary>
    /// <param name="ring">구독할 링 버퍼</param>
    /// <param name="snapshot">슬롯을 불변 스냅샷으로 복사하는 함수</param>
    /// <param name="pollInterval">이벤트가 없을 때의 폴링 간격</param>
    /// <param name="maxEventsPerPoll">한 번에 당겨올 최대 건수</param>
    /// <param name="cancellationToken">종료 신호</param>
    public static async IAsyncEnumerable<TSnapshot> ReadAllAsync<T, TSnapshot>(
        this RingBuffer<T> ring,
        Func<T, TSnapshot> snapshot,
        TimeSpan? pollInterval = null,
        int maxEventsPerPoll = 256,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where T : class
    {
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentNullException.ThrowIfNull(snapshot);

        TimeSpan interval = pollInterval ?? TimeSpan.FromMilliseconds(1);

        using EventPoller<T> poller = ring.NewPoller();
        var buffer = new List<TSnapshot>(Math.Min(maxEventsPerPoll, 256));

        while (!cancellationToken.IsCancellationRequested)
        {
            buffer.Clear();

            poller.Poll((evt, seq, eob) =>
            {
                buffer.Add(snapshot(evt));
                return true;
            }, maxEventsPerPoll);

            if (buffer.Count == 0)
            {
                try
                {
                    await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                continue;
            }

            foreach (TSnapshot item in buffer)
                yield return item;
        }
    }
}
