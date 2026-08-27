namespace Disruptor;

/// <summary>
/// 값 타입 이벤트를 처리하는 배치 프로세서.
/// </summary>
public sealed class ValueBatchEventProcessor<T> : IEventProcessor, IDisposable where T : struct
{
    private readonly ValueRingBuffer<T> _ringBuffer;
    private readonly ISequenceBarrier _barrier;
    private readonly IValueEventHandler<T> _eventHandler;
    private readonly Sequence _sequence = new();
    private readonly CancellationTokenSource _halt = new();
    private readonly bool _signalNeeded;

    private IExceptionHandler<T> _exceptionHandler;
    private volatile int _running;

    public ValueBatchEventProcessor(
        ValueRingBuffer<T> ringBuffer,
        ISequenceBarrier sequenceBarrier,
        IValueEventHandler<T> eventHandler,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(ringBuffer);
        ArgumentNullException.ThrowIfNull(sequenceBarrier);
        ArgumentNullException.ThrowIfNull(eventHandler);

        _ringBuffer = ringBuffer;
        _barrier = sequenceBarrier;
        _eventHandler = eventHandler;
        _exceptionHandler = ResilientExceptionHandler<T>.Default;
        _signalNeeded = sequenceBarrier.WaitStrategy.IsBlockingStrategy;
        Name = name ?? eventHandler.GetType().Name;

        HandlerValidation.AssertNotAsync(eventHandler, nameof(IValueEventHandler<T>.OnEvent));
    }

    public Sequence Sequence => _sequence;

    public string Name { get; }

    public bool IsRunning => _running == 1;

    /// <summary>처리 후 슬롯을 <c>default</c>로 초기화할지 여부. 리프 프로세서에만 설정하세요.</summary>
    public bool ClearAfterProcess { get; set; }

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
            throw new InvalidOperationException($"'{Name}' 프로세서가 이미 실행 중입니다.");
        }

        using var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static s => ((CancellationTokenSource)s!).Cancel(), _halt)
            : default;

        bool signalled = false;

        try
        {
            try
            {
                _eventHandler.OnStart();
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
                _eventHandler.OnShutdown();
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

        while (!ct.IsCancellationRequested)
        {
            var waitResult = _barrier.WaitFor(nextSequence, ct);

            if (waitResult.IsCancelled)
                break;

            if (waitResult.IsTimeout)
            {
                SafeInvoke(() => _eventHandler.OnTimeout(nextSequence - 1), nextSequence);
                continue;
            }

            long available = waitResult.AvailableSequence;

            if (available < nextSequence)
            {
                Thread.SpinWait(1);
                continue;
            }

            long batchSize = available - nextSequence + 1;
            SafeInvoke(() => _eventHandler.OnBatchStart(batchSize), nextSequence);

            while (nextSequence <= available)
            {
                try
                {
                    _eventHandler.OnEvent(ref _ringBuffer[nextSequence], nextSequence, nextSequence == available);

                    if (ClearAfterProcess)
                        _ringBuffer.ClearSlot(nextSequence);

                    _exceptionHandler.OnEventProcessed();
                    nextSequence++;
                }
                catch (Exception ex)
                {
                    Metrics?.RecordHandlerError(Name);

                    switch (SafeHandleException(ex, nextSequence))
                    {
                        case EventExceptionAction.Skip:
                            nextSequence++;
                            break;

                        case EventExceptionAction.Retry:
                            break;

                        case EventExceptionAction.Halt:
                            _sequence.SetValue(nextSequence - 1);
                            Halted?.Invoke(this, ex);
                            return;
                    }
                }
            }

            _sequence.SetValue(available);
            Metrics?.RecordProcessed(Name, batchSize);
            Metrics?.RecordBatch(Name, batchSize);

            if (_signalNeeded)
                _barrier.WaitStrategy.SignalAllWhenBlocking();
        }
    }

    private void SafeInvoke(Action action, long sequence)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            SafeHandleException(ex, sequence);
        }
    }

    private EventExceptionAction SafeHandleException(Exception ex, long sequence)
    {
        try
        {
            return _exceptionHandler.HandleEventException(ex, sequence, default);
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
