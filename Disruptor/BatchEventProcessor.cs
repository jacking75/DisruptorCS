namespace Disruptor;

/// <summary>
/// 배치 이벤트 프로세서. 링 버퍼에서 이벤트를 읽어 핸들러에게 전달합니다.
/// 각 프로세서는 하나의 스레드에서 실행됩니다.
/// </summary>
public sealed class BatchEventProcessor<T> : IEventProcessor, IDisposable where T : class
{
    private readonly RingBuffer<T> _ringBuffer;
    private readonly ISequenceBarrier _barrier;
    private readonly IEventHandler<T> _eventHandler;
    private readonly Sequence _sequence = new();
    private readonly CancellationTokenSource _halt = new();
    private readonly bool _signalNeeded;

    private IExceptionHandler<T> _exceptionHandler;
    private volatile int _running;

    public BatchEventProcessor(
        RingBuffer<T> ringBuffer,
        ISequenceBarrier sequenceBarrier,
        IEventHandler<T> eventHandler,
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

        HandlerValidation.AssertNotAsync(eventHandler, nameof(IEventHandler<T>.OnEvent));
    }

    /// <summary>이 프로세서의 시퀀스.</summary>
    public Sequence Sequence => _sequence;

    public string Name { get; }

    /// <summary>이 프로세서가 감싼 핸들러.</summary>
    public IEventHandler<T> Handler => _eventHandler;

    /// <summary>실행 중인지 여부.</summary>
    public bool IsRunning => _running == 1;

    /// <summary>
    /// 이벤트 처리 후 슬롯의 외부 참조를 해제할지 여부.
    /// 파이프라인의 마지막(리프) 프로세서에만 설정해야 합니다.
    /// </summary>
    public bool ClearAfterProcess { get; set; }

    /// <summary>선택적 메트릭 수집기.</summary>
    public DisruptorMetrics? Metrics { get; set; }

    /// <summary>
    /// 프로세서가 <see cref="EventExceptionAction.Halt"/>로 정지했을 때 발생합니다.
    /// 이 프로세서의 시퀀스가 게이팅에 남아 있으면 프로듀서가 곧 영구 블록되므로
    /// 반드시 구독해서 서버를 계획적으로 드레인해야 합니다.
    /// </summary>
    public event Action<IEventProcessor, Exception?>? Halted;

    /// <summary>예외 핸들러를 설정합니다.</summary>
    public void SetExceptionHandler(IExceptionHandler<T> exceptionHandler)
    {
        ArgumentNullException.ThrowIfNull(exceptionHandler);
        _exceptionHandler = exceptionHandler;
    }

    /// <summary>이벤트 루프를 실행합니다. Thread나 Task에서 호출하세요.</summary>
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

            // 이벤트 루프 진입 직전에 "준비 완료"를 알린다.
            // 이것이 없으면 Start() 직후 Shutdown() 호출 시 정지 신호를 잃어버린다.
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
                InvokeTimeout(nextSequence - 1);
                continue;
            }

            long available = waitResult.AvailableSequence;

            if (available < nextSequence)
            {
                // Multi 프로듀서: 앞선 시퀀스가 아직 게시되지 않았다. 짧게 대기 후 재확인.
                Thread.SpinWait(1);
                continue;
            }

            long batchSize = available - nextSequence + 1;
            InvokeBatchStart(batchSize);

            while (nextSequence <= available)
            {
                T @event = _ringBuffer[nextSequence];
                try
                {
                    _eventHandler.OnEvent(@event, nextSequence, nextSequence == available);

                    if (ClearAfterProcess && @event is IClearable clearable)
                        clearable.Clear();

                    _exceptionHandler.OnEventProcessed();
                    nextSequence++;
                }
                catch (Exception ex)
                {
                    Metrics?.RecordHandlerError(Name);

                    switch (SafeHandleException(ex, nextSequence, @event))
                    {
                        case EventExceptionAction.Skip:
                            nextSequence++;
                            break;

                        case EventExceptionAction.Retry:
                            // nextSequence 유지 -> 같은 이벤트 재시도
                            break;

                        case EventExceptionAction.Halt:
                            _sequence.SetValue(nextSequence - 1);
                            Halted?.Invoke(this, ex);
                            return; // 프로세스는 살린다
                    }
                }
            }

            _sequence.SetValue(available);
            Metrics?.RecordProcessed(Name, batchSize);
            Metrics?.RecordBatch(Name, batchSize);

            // 나를 기다리며 잠든 하위 컨슈머를 깨운다.
            // 이것이 없으면 의존성 체인의 하위 핸들러가 블로킹하지 못하고 hot-spin 한다.
            if (_signalNeeded)
                _barrier.WaitStrategy.SignalAllWhenBlocking();
        }
    }

    private void InvokeBatchStart(long batchSize)
    {
        try
        {
            _eventHandler.OnBatchStart(batchSize);
        }
        catch (Exception ex)
        {
            SafeHandleException(ex, _sequence.Value, null);
        }
    }

    private void InvokeTimeout(long lastProcessed)
    {
        try
        {
            _eventHandler.OnTimeout(lastProcessed);
        }
        catch (Exception ex)
        {
            SafeHandleException(ex, lastProcessed, null);
        }
    }

    private EventExceptionAction SafeHandleException(Exception ex, long sequence, T? @event)
    {
        try
        {
            return _exceptionHandler.HandleEventException(ex, sequence, @event);
        }
        catch (Exception handlerFailure)
        {
            // 예외 핸들러 자체가 실패해도 프로세스를 죽이지 않는다.
            Console.Error.WriteLine(
                $"[Disruptor][ERROR] '{Name}'의 예외 핸들러가 실패했습니다: {handlerFailure}");
            return EventExceptionAction.Skip;
        }
    }

    /// <summary>프로세서를 정지합니다.</summary>
    public void Halt()
    {
        if (!_halt.IsCancellationRequested)
            _halt.Cancel();

        // 블로킹 대기 중이면 즉시 깨운다.
        _barrier.WaitStrategy.SignalAllWhenBlocking();
    }

    public void Dispose() => _halt.Dispose();
}
