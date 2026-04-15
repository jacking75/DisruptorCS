using Disruptor.WaitStrategy;

namespace Disruptor.Dsl;

/// <summary>
/// Disruptor DSL. 간편하게 링 버퍼와 컨슈머를 설정할 수 있습니다.
/// </summary>
public sealed class Disruptor<T> : IDisposable where T : class
{
    private readonly RingBuffer<T> _ringBuffer;
    private readonly List<BatchEventProcessor<T>> _processors = new();
    private readonly List<Thread> _threads = new();
    private IExceptionHandler<T>? _defaultExceptionHandler;
    private bool _started;

    public Disruptor(
        Func<T> eventFactory,
        int ringBufferSize,
        ProducerType producerType = ProducerType.Single,
        IWaitStrategy? waitStrategy = null)
    {
        waitStrategy ??= new BlockingWaitStrategy();
        _ringBuffer = RingBuffer<T>.Create(producerType, eventFactory,
                                            ringBufferSize, waitStrategy);
    }

    /// <summary>링 버퍼에 접근합니다.</summary>
    public RingBuffer<T> RingBuffer => _ringBuffer;

    /// <summary>
    /// 기본 예외 핸들러를 설정합니다. Start() 호출 전에 설정해야 합니다.
    /// 이후 등록되는 모든 프로세서에 적용됩니다.
    /// </summary>
    public void SetDefaultExceptionHandler(IExceptionHandler<T> exceptionHandler)
    {
        _defaultExceptionHandler = exceptionHandler;

        // 이미 등록된 프로세서에도 적용
        foreach (var processor in _processors)
        {
            processor.SetExceptionHandler(exceptionHandler);
        }
    }

    /// <summary>
    /// 이벤트 핸들러를 등록합니다. 반환값으로 의존성 체인을 구성할 수 있습니다.
    /// </summary>
    public EventHandlerGroup HandleEventsWith(params IEventHandler<T>[] handlers)
    {
        return CreateEventProcessors(Array.Empty<Sequence>(), handlers);
    }

    /// <summary>
    /// 의존성이 있는 이벤트 핸들러 그룹.
    /// </summary>
    public sealed class EventHandlerGroup
    {
        private readonly Disruptor<T> _disruptor;
        private readonly Sequence[] _sequences;

        internal EventHandlerGroup(Disruptor<T> disruptor, Sequence[] sequences)
        {
            _disruptor = disruptor;
            _sequences = sequences;
        }

        /// <summary>
        /// 이 그룹이 완료된 후에 실행될 핸들러를 등록합니다.
        /// </summary>
        public EventHandlerGroup Then(params IEventHandler<T>[] handlers)
        {
            return _disruptor.CreateEventProcessors(_sequences, handlers);
        }
    }

    private EventHandlerGroup CreateEventProcessors(
        Sequence[] barrierSequences,
        IEventHandler<T>[] handlers)
    {
        if (_started)
            throw new InvalidOperationException(
                "Cannot add handlers after the Disruptor has started");

        var processorSequences = new Sequence[handlers.Length];

        for (int i = 0; i < handlers.Length; i++)
        {
            var barrier = _ringBuffer.NewBarrier(barrierSequences);
            var processor = new BatchEventProcessor<T>(
                _ringBuffer, barrier, handlers[i]);

            if (_defaultExceptionHandler != null)
                processor.SetExceptionHandler(_defaultExceptionHandler);

            _processors.Add(processor);
            processorSequences[i] = processor.Sequence;
        }

        // 마지막 단계의 시퀀스만 게이팅 시퀀스로 등록
        // (이전 단계의 것은 제거 — 리프 노드 최적화)
        foreach (var seq in barrierSequences)
        {
            _ringBuffer.RemoveGatingSequence(seq);
        }
        _ringBuffer.AddGatingSequences(processorSequences);

        return new EventHandlerGroup(this, processorSequences);
    }

    /// <summary>Disruptor를 시작합니다.</summary>
    public RingBuffer<T> Start()
    {
        if (_started)
            throw new InvalidOperationException("Already started");

        _started = true;

        foreach (var processor in _processors)
        {
            var thread = new Thread(processor.Run)
            {
                IsBackground = true,
                Name = $"Disruptor-{typeof(T).Name}-{_threads.Count}"
            };
            _threads.Add(thread);
            thread.Start();
        }

        return _ringBuffer;
    }

    /// <summary>
    /// Disruptor를 정지합니다.
    /// 타임아웃 내에서 미처리 이벤트를 모두 소비한 후 종료합니다.
    /// </summary>
    public void Shutdown(TimeSpan? timeout = null)
    {
        if (!_started)
            return;

        timeout ??= TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow + timeout.Value;

        // Phase 1: 링 버퍼의 미처리 이벤트를 모두 소비할 때까지 대기
        DrainProcessors(deadline);

        // Phase 2: 프로세서 중지
        foreach (var processor in _processors)
        {
            processor.Halt();
        }

        // Phase 3: 스레드 종료 대기
        foreach (var thread in _threads)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
                thread.Join(remaining);
        }

        _started = false;
    }

    /// <summary>
    /// 모든 프로세서가 커서까지 소비할 때까지 대기합니다.
    /// </summary>
    private void DrainProcessors(DateTime deadline)
    {
        long cursor = _ringBuffer.Cursor;
        if (cursor < 0)
            return; // 게시된 이벤트 없음

        while (DateTime.UtcNow < deadline)
        {
            bool allCaughtUp = true;
            foreach (var processor in _processors)
            {
                if (processor.Sequence.Value < cursor)
                {
                    allCaughtUp = false;
                    break;
                }
            }
            if (allCaughtUp)
                break;
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        Shutdown();
    }
}
