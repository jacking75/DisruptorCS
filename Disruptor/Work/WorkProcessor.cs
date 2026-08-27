namespace Disruptor.Work;

/// <summary>
/// 워커 풀의 개별 실행 단위. 공유 <c>workSequence</c>를 CAS로 점유하여
/// 같은 이벤트를 두 워커가 처리하지 않도록 보장합니다.
/// </summary>
public sealed class WorkProcessor<T> : IEventProcessor, IDisposable where T : class
{
    private readonly RingBuffer<T> _ringBuffer;
    private readonly ISequenceBarrier _barrier;
    private readonly IWorkHandler<T> _handler;
    private readonly Sequence _workSequence;
    private readonly Sequence _sequence = new();
    private readonly CancellationTokenSource _halt = new();

    private IExceptionHandler<T> _exceptionHandler;
    private volatile int _running;

    public WorkProcessor(
        RingBuffer<T> ringBuffer,
        ISequenceBarrier sequenceBarrier,
        IWorkHandler<T> workHandler,
        Sequence workSequence,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(ringBuffer);
        ArgumentNullException.ThrowIfNull(sequenceBarrier);
        ArgumentNullException.ThrowIfNull(workHandler);
        ArgumentNullException.ThrowIfNull(workSequence);

        _ringBuffer = ringBuffer;
        _barrier = sequenceBarrier;
        _handler = workHandler;
        _workSequence = workSequence;
        _exceptionHandler = ResilientExceptionHandler<T>.Default;
        Name = name ?? workHandler.GetType().Name;

        HandlerValidation.AssertNotAsync(workHandler, nameof(IWorkHandler<T>.OnEvent));
    }

    public Sequence Sequence => _sequence;

    public string Name { get; }

    public bool IsRunning => _running == 1;

    public DisruptorMetrics? Metrics { get; set; }

    public event Action<IEventProcessor, Exception?>? Halted;

    public void SetExceptionHandler(IExceptionHandler<T> exceptionHandler)
    {
        ArgumentNullException.ThrowIfNull(exceptionHandler);
        _exceptionHandler = exceptionHandler;
    }

    public void Run(CancellationToken cancellationToken, CountdownEvent? startedGate = null)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            startedGate?.Signal();
            throw new InvalidOperationException($"'{Name}' 워커가 이미 실행 중입니다.");
        }

        using var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static s => ((CancellationTokenSource)s!).Cancel(), _halt)
            : default;

        bool signalled = false;

        try
        {
            try
            {
                _handler.OnStart();
            }
            catch (Exception ex)
            {
                _exceptionHandler.HandleOnStartException(ex);
            }

            startedGate?.Signal();
            signalled = true;

            ProcessEvents(_halt.Token);
        }
        finally
        {
            if (!signalled)
                startedGate?.Signal();

            try
            {
                _handler.OnShutdown();
            }
            catch (Exception ex)
            {
                _exceptionHandler.HandleOnShutdownException(ex);
            }

            _running = 0;
        }
    }

    private void ProcessEvents(CancellationToken ct)
    {
        long nextSequence = _sequence.Value + 1L;
        bool needsNewClaim = true;
        long cachedAvailable = -1L;

        while (!ct.IsCancellationRequested)
        {
            if (needsNewClaim)
            {
                needsNewClaim = false;

                // 워커들끼리 CAS로 다음 시퀀스를 경쟁적으로 점유한다.
                while (true)
                {
                    long claimed = _workSequence.Value;
                    nextSequence = claimed + 1L;

                    // 게이팅 목적상 "이전 시퀀스까지는 끝났다"고 알린다.
                    _sequence.SetValue(claimed);

                    if (_workSequence.CompareAndSet(claimed, nextSequence))
                        break;
                }
            }

            if (cachedAvailable >= nextSequence)
            {
                T @event = _ringBuffer[nextSequence];
                try
                {
                    _handler.OnEvent(@event);
                    _exceptionHandler.OnEventProcessed();
                    needsNewClaim = true;
                    Metrics?.RecordProcessed(Name, 1);
                }
                catch (Exception ex)
                {
                    Metrics?.RecordHandlerError(Name);

                    switch (SafeHandleException(ex, nextSequence, @event))
                    {
                        case EventExceptionAction.Skip:
                            needsNewClaim = true;
                            break;

                        case EventExceptionAction.Retry:
                            break; // 같은 시퀀스 재시도

                        case EventExceptionAction.Halt:
                            Halted?.Invoke(this, ex);
                            return;
                    }
                }
            }
            else
            {
                var result = _barrier.WaitFor(nextSequence, ct);

                if (result.IsCancelled)
                    break;

                if (result.IsTimeout)
                {
                    try
                    {
                        _handler.OnTimeout();
                    }
                    catch (Exception ex)
                    {
                        SafeHandleException(ex, nextSequence, null);
                    }
                    continue;
                }

                if (result.AvailableSequence < nextSequence)
                {
                    Thread.SpinWait(1);
                    continue;
                }

                cachedAvailable = result.AvailableSequence;
            }
        }

        // 정지 시, 점유했지만 처리하지 못한 시퀀스가 있으면 게이팅이 막히지 않도록 반영한다.
        _sequence.SetValue(nextSequence - 1L);
    }

    private EventExceptionAction SafeHandleException(Exception ex, long sequence, T? @event)
    {
        try
        {
            return _exceptionHandler.HandleEventException(ex, sequence, @event);
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
        if (!_halt.IsCancellationRequested)
            _halt.Cancel();

        _barrier.WaitStrategy.SignalAllWhenBlocking();
    }

    public void Dispose() => _halt.Dispose();
}
