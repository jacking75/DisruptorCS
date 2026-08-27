namespace Disruptor.Work;

/// <summary>
/// 값 타입 이벤트를 <b>나눠서</b> 처리하는 핸들러.
/// </summary>
/// <remarks>
/// 슬롯을 <c>ref</c> 로 받으므로 복사가 없습니다. 처리 순서는 보장되지 않습니다 —
/// 순서가 필요하면 <see cref="IValueEventHandler{T}"/>를 사용하세요.
/// </remarks>
public interface IValueWorkHandler<T> where T : struct
{
    void OnEvent(ref T evt);

    void OnStart() { }

    void OnShutdown() { }

    /// <summary>대기 타임아웃 시 호출됩니다. 배치 커밋 지점으로 사용하세요.</summary>
    void OnTimeout() { }
}

/// <summary>
/// 값 타입 워커 풀의 개별 실행 단위.
/// </summary>
/// <inheritdoc cref="WorkProcessor{T}" path="/summary"/>
public sealed class ValueWorkProcessor<T> : IEventProcessor, IDisposable where T : struct
{
    private readonly ValueRingBuffer<T> _ringBuffer;
    private readonly ISequenceBarrier _barrier;
    private readonly IValueWorkHandler<T> _handler;
    private readonly Sequence _workSequence;
    private readonly Sequence _sequence = new();
    private readonly CancellationTokenSource _halt = new();

    private IExceptionHandler<T> _exceptionHandler;
    private volatile int _running;
    private int _disposed;

    public ValueWorkProcessor(
        ValueRingBuffer<T> ringBuffer,
        ISequenceBarrier sequenceBarrier,
        IValueWorkHandler<T> workHandler,
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
        _exceptionHandler = ResilientExceptionHandler<T>.CreateDefault();
        Name = name ?? workHandler.GetType().Name;

        HandlerValidation.AssertNotAsync(workHandler, nameof(IValueWorkHandler<T>.OnEvent));
    }

    public Sequence Sequence => _sequence;

    public string Name { get; }

    public bool IsRunning => _running == 1;

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
            throw new InvalidOperationException($"'{Name}' 워커가 이미 실행 중입니다.");
        }

        using var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static s => TryCancel((CancellationTokenSource)s!), _halt)
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
                    _sequence.SetValue(claimed);

                    if (_workSequence.CompareAndSet(claimed, nextSequence))
                        break;
                }
            }

            if (cachedAvailable >= nextSequence)
            {
                try
                {
                    _handler.OnEvent(ref _ringBuffer[nextSequence]);
                    _exceptionHandler.OnEventProcessed();
                    needsNewClaim = true;
                    Recorder?.OnBatchCompleted(1);
                }
                catch (Exception ex)
                {
                    Recorder?.OnHandlerError();

                    switch (SafeHandleException(ex, nextSequence))
                    {
                        case EventExceptionAction.Skip:
                            needsNewClaim = true;
                            break;

                        case EventExceptionAction.Retry:
                            break;

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
                        SafeHandleException(ex, nextSequence);
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

/// <summary>
/// 값 타입 이벤트를 여러 워커에게 <b>분배</b>해서 처리하는 풀.
/// </summary>
/// <inheritdoc cref="WorkerPool{T}" path="/summary"/>
public sealed class ValueWorkerPool<T> : IDisposable where T : struct
{
    private readonly ValueRingBuffer<T> _ringBuffer;
    private readonly Sequence _workSequence = new();
    private readonly ValueWorkProcessor<T>[] _processors;
    private readonly List<Thread> _threads = new();
    private int _started;

    public ValueWorkerPool(
        ValueRingBuffer<T> ringBuffer,
        ISequenceBarrier barrier,
        Func<int, IValueWorkHandler<T>> workHandlerFactory,
        int workerCount,
        string? namePrefix = null)
    {
        ArgumentNullException.ThrowIfNull(ringBuffer);
        ArgumentNullException.ThrowIfNull(barrier);
        ArgumentNullException.ThrowIfNull(workHandlerFactory);
        if (workerCount < 1)
            throw new ArgumentOutOfRangeException(nameof(workerCount));

        _ringBuffer = ringBuffer;
        _processors = new ValueWorkProcessor<T>[workerCount];

        for (int i = 0; i < workerCount; i++)
        {
            var handler = workHandlerFactory(i);
            _processors[i] = new ValueWorkProcessor<T>(
                ringBuffer, barrier, handler, _workSequence,
                name: $"{namePrefix ?? handler.GetType().Name}-{i}");
        }
    }

    /// <summary>게이팅에 등록해야 할 시퀀스들.</summary>
    public Sequence[] Sequences => Array.ConvertAll(_processors, p => p.Sequence);

    public IReadOnlyList<ValueWorkProcessor<T>> Processors => _processors;

    public Sequence WorkSequence => _workSequence;

    public void SetExceptionHandler(IExceptionHandler<T> exceptionHandler)
    {
        foreach (var p in _processors)
            p.SetExceptionHandler(exceptionHandler);
    }

    public void SetMetrics(DisruptorMetrics? metrics)
    {
        foreach (var p in _processors)
            p.Recorder = metrics?.CreateRecorder(p.Name);
    }

    public void Start(CancellationToken cancellationToken, ProcessorThreadOptions? threadOptions = null)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("ValueWorkerPool이 이미 시작되었습니다.");

        long cursor = _ringBuffer.Cursor;
        _workSequence.SetValue(cursor);
        foreach (var p in _processors)
            p.Sequence.SetValue(cursor);

        var gate = new CountdownEvent(_processors.Length);

        for (int i = 0; i < _processors.Length; i++)
        {
            var thread = ProcessorThreadFactory.Create(
                _processors[i], cancellationToken, gate, threadOptions, i);

            _threads.Add(thread);
            thread.Start();
        }

        gate.Wait();
        gate.Dispose();
    }

    public void Halt()
    {
        foreach (var p in _processors)
            p.Halt();

        foreach (var t in _threads)
            t.Join(TimeSpan.FromSeconds(2));

        _threads.Clear();
    }

    public void Dispose()
    {
        Halt();
        foreach (var p in _processors)
            p.Dispose();
    }
}
