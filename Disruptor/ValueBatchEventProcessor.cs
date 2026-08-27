namespace Disruptor;

/// <summary>
/// 값 타입 이벤트를 처리하는 배치 프로세서.
/// </summary>
/// <inheritdoc cref="BatchEventProcessor{T}" path="/remarks"/>
public sealed class ValueBatchEventProcessor<T> : IEventProcessor, IDisposable where T : struct
{
    private readonly ValueRingBuffer<T> _ringBuffer;
    private readonly ISequenceBarrier _barrier;
    private readonly IValueEventHandler<T>? _eventHandler;
    private readonly IValueBatchEventHandler<T>? _batchHandler;
    private readonly Sequence _sequence = new();
    private readonly CancellationTokenSource _halt = new();
    private readonly bool _signalNeeded;

    private IExceptionHandler<T> _exceptionHandler;
    private volatile int _running;
    private int _disposed;
    private Exception? _lastFault;

    /// <inheritdoc cref="BatchEventProcessor{T}" path="/remarks"/>
    private readonly bool _handlerImplementsBatchStart;
    private bool _hasPendingFailure;

    public ValueBatchEventProcessor(
        ValueRingBuffer<T> ringBuffer,
        ISequenceBarrier sequenceBarrier,
        IValueEventHandler<T> eventHandler,
        string? name = null)
        : this(ringBuffer, sequenceBarrier, name ?? eventHandler?.GetType().Name)
    {
        ArgumentNullException.ThrowIfNull(eventHandler);
        _eventHandler = eventHandler;
        _handlerImplementsBatchStart = HandlerValidation.ImplementsHook(
            eventHandler, typeof(IValueEventHandler<T>), nameof(IValueEventHandler<T>.OnBatchStart));

        HandlerValidation.AssertNotAsync(eventHandler, nameof(IValueEventHandler<T>.OnEvent));
    }

    public ValueBatchEventProcessor(
        ValueRingBuffer<T> ringBuffer,
        ISequenceBarrier sequenceBarrier,
        IValueBatchEventHandler<T> batchHandler,
        string? name = null)
        : this(ringBuffer, sequenceBarrier, name ?? batchHandler?.GetType().Name)
    {
        ArgumentNullException.ThrowIfNull(batchHandler);
        _batchHandler = batchHandler;
        HandlerValidation.AssertNotAsync(batchHandler, nameof(IValueBatchEventHandler<T>.OnBatch));
    }

    private ValueBatchEventProcessor(ValueRingBuffer<T> ringBuffer, ISequenceBarrier sequenceBarrier, string? name)
    {
        ArgumentNullException.ThrowIfNull(ringBuffer);
        ArgumentNullException.ThrowIfNull(sequenceBarrier);

        _ringBuffer = ringBuffer;
        _barrier = sequenceBarrier;
        _exceptionHandler = ResilientExceptionHandler<T>.CreateDefault();
        _signalNeeded = sequenceBarrier.WaitStrategy.IsBlockingStrategy;
        Name = name ?? nameof(ValueBatchEventProcessor<T>);
    }

    public Sequence Sequence => _sequence;

    public string Name { get; }

    public bool IsRunning => _running == 1;

    /// <summary>처리 후 슬롯을 <c>default</c>로 초기화할지 여부. 리프 프로세서에만 설정하세요.</summary>
    public bool ClearAfterProcess { get; set; }

    /// <inheritdoc cref="BatchEventProcessor{T}.MaxBatchSize"/>
    public int MaxBatchSize { get; set; } = int.MaxValue;

    public HandlerRecorder? Recorder { get; set; }

    public event Action<IEventProcessor, Exception?>? Halted;

    public void SetExceptionHandler(IExceptionHandler<T> exceptionHandler)
    {
        ArgumentNullException.ThrowIfNull(exceptionHandler);
        _exceptionHandler = exceptionHandler;
    }

    public void Run(CancellationToken cancellationToken, CountdownEvent? startedGate = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            startedGate?.Signal();
            throw new InvalidOperationException($"'{Name}' 프로세서가 이미 실행 중입니다.");
        }

        using var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static s => TryCancel((CancellationTokenSource)s!), _halt)
            : default;

        bool signalled = false;

        try
        {
            try
            {
                if (_batchHandler is not null) _batchHandler.OnStart();
                else _eventHandler!.OnStart();
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
                if (_batchHandler is not null) _batchHandler.OnShutdown();
                else _eventHandler!.OnShutdown();
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
        var idleSpin = new SpinWait();

        while (!ct.IsCancellationRequested)
        {
            var waitResult = _barrier.WaitFor(nextSequence, ct);

            if (waitResult.IsCancelled)
                break;

            if (waitResult.IsTimeout)
            {
                InvokeTimeout(nextSequence - 1);
                continue;
            }

            long available = waitResult.AvailableSequence;

            if (available < nextSequence)
            {
                idleSpin.SpinOnce(sleep1Threshold: -1);
                continue;
            }

            idleSpin = default;

            long batchSize = available - nextSequence + 1;
            if (batchSize > MaxBatchSize)
            {
                available = nextSequence + MaxBatchSize - 1;
                batchSize = MaxBatchSize;
            }

            InvokeBatchStart(batchSize);

            while (nextSequence <= available)
            {
                if (ProcessSegment(ref nextSequence, available))
                    continue;

                _sequence.SetValue(nextSequence - 1);
                Halted?.Invoke(this, _lastFault);
                return;
            }

            _sequence.SetValue(available);

            if (_hasPendingFailure)
            {
                _exceptionHandler.OnEventProcessed();
                _hasPendingFailure = false;
            }

            Recorder?.OnBatchCompleted(batchSize);

            if (_signalNeeded)
                _barrier.WaitStrategy.SignalAllWhenBlocking();
        }
    }

    private bool ProcessSegment(ref long nextSequence, long available)
    {
        long current = nextSequence;

        try
        {
            if (_batchHandler is not null)
            {
                DispatchBatch(_batchHandler, current, available);
                current = available + 1;
            }
            else if (ClearAfterProcess)
            {
                var handler = _eventHandler!;
                while (current <= available)
                {
                    handler.OnEvent(ref _ringBuffer[current], current, current == available);
                    _ringBuffer.ClearSlot(current);
                    current++;
                }
            }
            else
            {
                var handler = _eventHandler!;
                while (current <= available)
                {
                    handler.OnEvent(ref _ringBuffer[current], current, current == available);
                    current++;
                }
            }

            nextSequence = current;
            return true;
        }
        catch (Exception ex)
        {
            Recorder?.OnHandlerError();
            _hasPendingFailure = true;

            switch (SafeHandleException(ex, current))
            {
                case EventExceptionAction.Skip:
                    nextSequence = _batchHandler is not null ? available + 1 : current + 1;
                    return true;

                case EventExceptionAction.Retry:
                    nextSequence = current;
                    return true;

                default:
                    _lastFault = ex;
                    nextSequence = current;
                    return false;
            }
        }
    }

    /// <summary>배치를 랩 경계에서 최대 2개의 연속 조각으로 나눠 전달합니다.</summary>
    private void DispatchBatch(IValueBatchEventHandler<T> handler, long from, long to)
    {
        int bufferSize = _ringBuffer.BufferSize;
        long current = from;

        while (current <= to)
        {
            int startIndex = (int)current & (bufferSize - 1);
            long segmentEnd = Math.Min(to, current + (bufferSize - startIndex) - 1);

            handler.OnBatch(_ringBuffer.GetSegment(current, segmentEnd),
                            current,
                            endOfBatch: segmentEnd == to);

            current = segmentEnd + 1;
        }
    }

    private void InvokeBatchStart(long batchSize)
    {
        if (_batchHandler is not null || !_handlerImplementsBatchStart)
            return;

        try
        {
            _eventHandler!.OnBatchStart(batchSize);
        }
        catch (Exception ex)
        {
            SafeHandleException(ex, _sequence.Value);
        }
    }

    private void InvokeTimeout(long lastProcessed)
    {
        try
        {
            if (_batchHandler is not null) _batchHandler.OnTimeout(lastProcessed);
            else _eventHandler!.OnTimeout(lastProcessed);
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
