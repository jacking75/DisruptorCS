namespace Disruptor;

/// <summary>
/// 배치 이벤트 프로세서. 링 버퍼에서 이벤트를 읽어 핸들러에게 전달합니다.
/// 각 프로세서는 하나의 스레드에서 실행됩니다.
/// </summary>
/// <remarks>
/// 이벤트 루프는 <b>배치 단위</b>로 구성되어 있습니다 — try/catch, 실패 스트릭 리셋,
/// <see cref="IClearable"/> 검사가 모두 배치 경계에서 일어납니다. 이벤트마다 이것들을 지불하면
/// try 경계가 JIT 의 코드 이동·레지스터 할당을 제약해 소비 처리량이 크게 떨어집니다
/// (실측: per-event 구성 339M ops/s → 배치 구성 649M ops/s, +87%).
/// </remarks>
public sealed class BatchEventProcessor<T> : IEventProcessor, IDisposable where T : class
{
    /// <summary>
    /// <typeparamref name="T"/>가 <see cref="IClearable"/>을 구현하는지의 정적 캐시.
    /// 이벤트마다 타입 검사를 반복하지 않기 위한 것입니다.
    /// </summary>
    private static readonly bool TypeIsClearable = typeof(IClearable).IsAssignableFrom(typeof(T));

    private readonly RingBuffer<T> _ringBuffer;
    private readonly ISequenceBarrier _barrier;
    private readonly IEventHandler<T>? _eventHandler;
    private readonly IBatchEventHandler<T>? _batchHandler;
    private readonly Sequence _sequence = new();
    private readonly CancellationTokenSource _halt = new();
    private readonly bool _signalNeeded;

    private IExceptionHandler<T> _exceptionHandler;
    private volatile int _running;
    private int _disposed;
    private Exception? _lastFault;

    /// <summary>핸들러가 OnBatchStart 를 직접 구현했는가. DIM 이면 호출 자체를 건너뛴다.</summary>
    private readonly bool _handlerImplementsBatchStart;

    /// <summary>
    /// 아직 리셋되지 않은 실패가 있는가.
    /// </summary>
    /// <remarks>
    /// <c>OnEventProcessed()</c> 는 실패 스트릭을 리셋하기 위한 것이므로, 실패가 한 번도 없었다면
    /// 호출할 이유가 없습니다. 이 훅도 DIM 인 경우가 많아 배치마다 호출하면 고정 비용이 됩니다.
    /// </remarks>
    private bool _hasPendingFailure;

    public BatchEventProcessor(
        RingBuffer<T> ringBuffer,
        ISequenceBarrier sequenceBarrier,
        IEventHandler<T> eventHandler,
        string? name = null)
        : this(ringBuffer, sequenceBarrier, name ?? eventHandler?.GetType().Name)
    {
        ArgumentNullException.ThrowIfNull(eventHandler);
        _eventHandler = eventHandler;
        _handlerImplementsBatchStart = HandlerValidation.ImplementsHook(
            eventHandler, typeof(IEventHandler<T>), nameof(IEventHandler<T>.OnBatchStart));

        HandlerValidation.AssertNotAsync(eventHandler, nameof(IEventHandler<T>.OnEvent));
    }

    public BatchEventProcessor(
        RingBuffer<T> ringBuffer,
        ISequenceBarrier sequenceBarrier,
        IBatchEventHandler<T> batchHandler,
        string? name = null)
        : this(ringBuffer, sequenceBarrier, name ?? batchHandler?.GetType().Name)
    {
        ArgumentNullException.ThrowIfNull(batchHandler);
        _batchHandler = batchHandler;
        HandlerValidation.AssertNotAsync(batchHandler, nameof(IBatchEventHandler<T>.OnBatch));
    }

    private BatchEventProcessor(RingBuffer<T> ringBuffer, ISequenceBarrier sequenceBarrier, string? name)
    {
        ArgumentNullException.ThrowIfNull(ringBuffer);
        ArgumentNullException.ThrowIfNull(sequenceBarrier);

        _ringBuffer = ringBuffer;
        _barrier = sequenceBarrier;
        _exceptionHandler = ResilientExceptionHandler<T>.CreateDefault();
        _signalNeeded = sequenceBarrier.WaitStrategy.IsBlockingStrategy;
        Name = name ?? nameof(BatchEventProcessor<T>);
    }

    /// <summary>이 프로세서의 시퀀스.</summary>
    public Sequence Sequence => _sequence;

    public string Name { get; }

    /// <summary>이 프로세서가 감싼 핸들러 (이벤트 단위 핸들러일 때만 non-null).</summary>
    public IEventHandler<T>? Handler => _eventHandler;

    /// <summary>실행 중인지 여부.</summary>
    public bool IsRunning => _running == 1;

    /// <summary>
    /// 이벤트 처리 후 슬롯의 외부 참조를 해제할지 여부.
    /// 파이프라인의 마지막(리프) 프로세서에만 설정해야 합니다.
    /// </summary>
    public bool ClearAfterProcess { get; set; }

    /// <summary>
    /// 한 번에 처리할 최대 이벤트 수. 기본값은 무제한입니다.
    /// </summary>
    /// <remarks>
    /// 배치 효과는 처리량에 유리하지만, 뒤처진 컨슈머가 깨어나면 쌓인 전부를 한 배치로 처리하므로
    /// 그동안 <see cref="IEventHandler{T}.OnTimeout"/>을 받지 못합니다. 틱 드라이버를 겸하는
    /// 핸들러라면 배치 하나가 틱을 밀어낼 수 있습니다. 이 값을 설정하면
    /// <b>배치 상한 = 지연 상한</b>이 되어 최악 지연이 계산 가능해집니다
    /// (권장: 링 크기의 1/4).
    /// </remarks>
    public int MaxBatchSize { get; set; } = int.MaxValue;

    /// <summary>선택적 메트릭 기록기.</summary>
    public HandlerRecorder? Recorder { get; set; }

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
                InvokeStart();
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
                InvokeShutdown();
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
                // Multi 프로듀서: 앞선 시퀀스가 아직 게시되지 않았다.
                // 진전이 없는 동안만 에스컬레이션하고(Sleep(1) 은 하지 않는다), 진전이 생기면 리셋한다.
                idleSpin.SpinOnce(sleep1Threshold: -1);
                continue;
            }

            idleSpin = default;

            // §2.7: 배치 상한을 적용해 지연 상한을 확보한다. 나머지는 다음 루프에서 처리된다.
            long batchSize = available - nextSequence + 1;
            if (batchSize > MaxBatchSize)
            {
                available = nextSequence + MaxBatchSize - 1;
                batchSize = MaxBatchSize;
            }

            InvokeBatchStart(batchSize);

            // 배치를 처리한다. 예외가 나면 nextSequence 는 실패 지점에 맞춰 조정되고,
            // 이 루프를 다시 돌아 배치의 나머지 구간을 이어서 처리한다.
            while (nextSequence <= available)
            {
                // Check between attempts, not per event on the successful hot path.
                // A persistent Retry must not keep a halted processor spinning forever.
                if (ct.IsCancellationRequested)
                {
                    _sequence.SetValue(nextSequence - 1);
                    return;
                }

                if (ProcessSegment(ref nextSequence, available))
                    continue;

                // Halt 요청: 완료된 지점까지만 반영하고 종료한다.
                _sequence.SetValue(nextSequence - 1);
                Halted?.Invoke(this, _lastFault);
                return;
            }

            _sequence.SetValue(available);

            Recorder?.OnBatchCompleted(batchSize);

            // 나를 기다리며 잠든 하위 컨슈머를 깨운다.
            // 이것이 없으면 의존성 체인의 하위 핸들러가 블로킹하지 못하고 hot-spin 한다.
            if (_signalNeeded)
                _barrier.WaitStrategy.SignalAllWhenBlocking();
        }
    }

    /// <summary>
    /// [nextSequence, available] 구간을 try 1회로 처리합니다.
    /// </summary>
    /// <returns>계속 진행하려면 true, Halt 요청이면 false.</returns>
    private bool ProcessSegment(ref long nextSequence, long available)
    {
        if (_batchHandler is not null)
            return ProcessBatchSegment(ref nextSequence, available);

        long current = nextSequence;

        try
        {
            if (TypeIsClearable && ClearAfterProcess)
            {
                // 분기를 루프 밖으로 — JIT 가 두 개의 타이트 루프를 각각 최적화한다.
                var handler = _eventHandler!;
                while (current <= available)
                {
                    T evt = _ringBuffer[current];
                    handler.OnEvent(evt, current, current == available);
                    ((IClearable)evt).Clear(); // 정적으로 보장된 캐스트 — 런타임 검사 없음
                    current++;
                }
            }
            else
            {
                var handler = _eventHandler!;
                while (current <= available)
                {
                    handler.OnEvent(_ringBuffer[current], current, current == available);
                    current++;
                }
            }

            ResetFailureAfterSuccess();
            nextSequence = current;
            return true;
        }
        catch (Exception ex)
        {
            // A successful prefix separates failures even inside the same batch.
            if (current > nextSequence)
                ResetFailureAfterSuccess();

            Recorder?.OnHandlerError();
            _hasPendingFailure = true;

            switch (SafeHandleException(ex, current, _ringBuffer[current]))
            {
                case EventExceptionAction.Skip:
                    nextSequence = current + 1;
                    return true;

                case EventExceptionAction.Retry:
                    nextSequence = current; // 같은 지점부터 다시
                    return true;

                default:
                    _lastFault = ex;
                    nextSequence = current;
                    return false;
            }
        }
    }

    private void ResetFailureAfterSuccess()
    {
        if (!_hasPendingFailure) return;
        _exceptionHandler.OnEventProcessed();
        _hasPendingFailure = false;
    }

    // One invocation is one contiguous segment. Keep its recovery boundary separate
    // from the entire available range, or a failure after wrap replays completed work.
    private bool ProcessBatchSegment(ref long nextSequence, long available)
    {
        long current = nextSequence;
        int bufferSize = _ringBuffer.BufferSize;
        int startIndex = (int)current & (bufferSize - 1);
        long segmentEnd = Math.Min(available, current + bufferSize - startIndex - 1);

        try
        {
            _batchHandler!.OnBatch(_ringBuffer.GetSegment(current, segmentEnd), current, segmentEnd == available);
            ResetFailureAfterSuccess();
        }
        catch (Exception ex)
        {
            Recorder?.OnHandlerError();
            _hasPendingFailure = true;
            switch (SafeHandleException(ex, current, _ringBuffer[current]))
            {
                case EventExceptionAction.Skip:
                    break;
                case EventExceptionAction.Retry:
                    return true; // Leave the failing segment and its payloads intact.
                default:
                    _lastFault = ex;
                    return false;
            }
        }

        if (TypeIsClearable && ClearAfterProcess)
        {
            // Cleanup is not business processing. Retrying the callback after a cleanup
            // failure could repeat side effects or return an already returned pooled buffer.
            try
            {
                while (nextSequence <= segmentEnd)
                {
                    ((IClearable)_ringBuffer[nextSequence]).Clear();
                    nextSequence++;
                }
            }
            catch (Exception ex)
            {
                Recorder?.OnHandlerError();
                SafeHandleException(ex, nextSequence, _ringBuffer[nextSequence]);
                _lastFault = ex;
                return false;
            }
        }
        else
        {
            nextSequence = segmentEnd + 1;
        }
        return true;
    }

    private void InvokeStart()
    {
        if (_batchHandler is not null) _batchHandler.OnStart();
        else _eventHandler!.OnStart();
    }

    private void InvokeShutdown()
    {
        if (_batchHandler is not null) _batchHandler.OnShutdown();
        else _eventHandler!.OnShutdown();
    }

    private void InvokeBatchStart(long batchSize)
    {
        // 배치 핸들러는 OnBatch 자체가 배치 시작 통지다.
        // 이벤트 핸들러가 훅을 구현하지 않았다면 DIM 디스패치를 지불할 이유가 없다.
        if (_batchHandler is not null || !_handlerImplementsBatchStart)
            return;

        try
        {
            _eventHandler!.OnBatchStart(batchSize);
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
            if (_batchHandler is not null) _batchHandler.OnTimeout(lastProcessed);
            else _eventHandler!.OnTimeout(lastProcessed);
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

    /// <summary>프로세서를 정지합니다. Dispose 이후 호출해도 안전합니다.</summary>
    public void Halt()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        TryCancel(_halt);

        // 블로킹 대기 중이면 즉시 깨운다.
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
            // Dispose 와의 경쟁 창. 이미 정리된 상태이므로 무시해도 안전하다.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _halt.Dispose();
    }
}
