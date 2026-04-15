namespace Disruptor;

/// <summary>
/// 배치 이벤트 프로세서. 링 버퍼에서 이벤트를 읽어 핸들러에게 전달합니다.
/// 각 프로세서는 하나의 스레드에서 실행됩니다.
/// </summary>
public sealed class BatchEventProcessor<T> where T : class
{
    private readonly RingBuffer<T> _ringBuffer;
    private readonly ISequenceBarrier _sequenceBarrier;
    private readonly IEventHandler<T> _eventHandler;
    private readonly Sequence _sequence = new();
    private IExceptionHandler<T> _exceptionHandler;
    private volatile int _running;

    public BatchEventProcessor(
        RingBuffer<T> ringBuffer,
        ISequenceBarrier sequenceBarrier,
        IEventHandler<T> eventHandler)
    {
        _ringBuffer = ringBuffer;
        _sequenceBarrier = sequenceBarrier;
        _eventHandler = eventHandler;
        _exceptionHandler = new FatalExceptionHandler<T>();
    }

    /// <summary>이 프로세서의 시퀀스.</summary>
    public Sequence Sequence => _sequence;

    /// <summary>예외 핸들러를 설정합니다.</summary>
    public void SetExceptionHandler(IExceptionHandler<T> exceptionHandler)
    {
        _exceptionHandler = exceptionHandler;
    }

    /// <summary>실행 중인지 여부.</summary>
    public bool IsRunning => _running == 1;

    /// <summary>이벤트 루프를 실행합니다. Thread나 Task에서 호출하세요.</summary>
    public void Run()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
            throw new InvalidOperationException("Already running");

        _sequenceBarrier.ClearAlert();

        try
        {
            try
            {
                _eventHandler.OnStart();
            }
            catch (Exception ex)
            {
                _exceptionHandler?.HandleOnStartException(ex);
            }

            ProcessEvents();
        }
        finally
        {
            try
            {
                _eventHandler.OnShutdown();
            }
            catch (Exception ex)
            {
                _exceptionHandler?.HandleOnShutdownException(ex);
            }

            _running = 0;
        }
    }

    private void ProcessEvents()
    {
        long nextSequence = _sequence.Value + 1L;

        while (true)
        {
            try
            {
                long availableSequence = _sequenceBarrier.WaitFor(nextSequence);

                while (nextSequence <= availableSequence)
                {
                    T @event = _ringBuffer[nextSequence];
                    _eventHandler.OnEvent(@event, nextSequence,
                                          nextSequence == availableSequence);
                    nextSequence++;
                }

                _sequence.SetValue(availableSequence);
            }
            catch (AlertException)
            {
                if (_running == 0)
                    break;
            }
            catch (Exception ex)
            {
                _exceptionHandler.HandleEventException(ex, nextSequence,
                                                        _ringBuffer[nextSequence]);
                _sequence.SetValue(nextSequence);
                nextSequence++;
            }
        }
    }

    /// <summary>프로세서를 정지합니다.</summary>
    public void Halt()
    {
        _running = 0;
        _sequenceBarrier.Alert();
    }
}
