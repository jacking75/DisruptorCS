using Disruptor.WaitStrategy;
using Disruptor.Work;

namespace Disruptor.Dsl;

/// <summary>
/// Disruptor DSL. 간편하게 링 버퍼와 컨슈머를 설정할 수 있습니다.
/// </summary>
public sealed class Disruptor<T> : DisruptorBase where T : class
{
    private readonly RingBuffer<T> _ringBuffer;
    private readonly List<BatchEventProcessor<T>> _batchProcessors = new();
    private readonly List<WorkerPool<T>> _workerPools = new();

    private IExceptionHandler<T>? _defaultExceptionHandler;

    public Disruptor(
        Func<T> eventFactory,
        int ringBufferSize,
        ProducerType producerType = ProducerType.Single,
        IWaitStrategy? waitStrategy = null,
        DisruptorOptions? options = null)
        : base(options ?? DisruptorOptions.Default)
    {
        waitStrategy ??= Options.CreateDefaultWaitStrategy();

        _ringBuffer = RingBuffer<T>.Create(
            producerType, eventFactory, ringBufferSize, waitStrategy,
            new SequencerOptions
            {
                PipelineName = Options.Name,
                Backoff = Options.ProducerBackoff,
                StallThreshold = Options.StallThreshold,
                StallReportInterval = Options.StallReportInterval,
                OnStall = info =>
                {
                    Metrics?.RecordStall(info);
                    Options.OnStall?.Invoke(info);
                },
                ValidateSingleProducerThread = Options.ValidateSingleProducerThread,
            });
    }

    /// <summary>링 버퍼에 접근합니다.</summary>
    public RingBuffer<T> RingBuffer => _ringBuffer;

    protected override IRingBufferCore Core => _ringBuffer;

    protected override bool HasRegisteredWork => base.HasRegisteredWork || _workerPools.Count > 0;

    /// <summary>
    /// 기본 예외 핸들러를 설정합니다. 이후 등록되는 모든 프로세서에 적용되며,
    /// 이미 등록된 프로세서에도 소급 적용됩니다.
    /// </summary>
    public void SetDefaultExceptionHandler(IExceptionHandler<T> exceptionHandler)
    {
        ArgumentNullException.ThrowIfNull(exceptionHandler);
        _defaultExceptionHandler = exceptionHandler;

        foreach (var processor in _batchProcessors)
            processor.SetExceptionHandler(exceptionHandler);

        foreach (var pool in _workerPools)
            pool.SetExceptionHandler(exceptionHandler);
    }

    /// <summary>
    /// 이벤트 핸들러를 등록합니다. 반환값으로 의존성 체인을 구성할 수 있습니다.
    /// </summary>
    public EventHandlerGroup HandleEventsWith(params IEventHandler<T>[] handlers)
        => CreateEventProcessors([], handlers);

    /// <summary>
    /// 배치 핸들러를 등록합니다. 이벤트 구간을 span 으로 통째로 받습니다.
    /// </summary>
    public EventHandlerGroup HandleEventsWith(params IBatchEventHandler<T>[] handlers)
        => CreateBatchProcessors([], handlers);

    /// <summary>이벤트를 여러 워커에게 분배하는 워커 풀을 등록합니다.</summary>
    public EventHandlerGroup HandleEventsWithWorkerPool(
        int workerCount, Func<int, IWorkHandler<T>> workHandlerFactory)
        => CreateWorkerPool([], workerCount, workHandlerFactory);

    /// <summary>의존성이 있는 이벤트 핸들러 그룹.</summary>
    public sealed class EventHandlerGroup
    {
        private readonly Disruptor<T> _disruptor;

        internal EventHandlerGroup(Disruptor<T> disruptor, Sequence[] sequences)
        {
            _disruptor = disruptor;
            Sequences = sequences;
        }

        /// <summary>이 그룹의 시퀀스들.</summary>
        public Sequence[] Sequences { get; }

        /// <summary>이 그룹이 완료된 후에 실행될 핸들러를 등록합니다.</summary>
        public EventHandlerGroup Then(params IEventHandler<T>[] handlers)
            => _disruptor.CreateEventProcessors(Sequences, handlers);

        /// <summary>이 그룹이 완료된 후에 실행될 배치 핸들러를 등록합니다.</summary>
        public EventHandlerGroup Then(params IBatchEventHandler<T>[] handlers)
            => _disruptor.CreateBatchProcessors(Sequences, handlers);

        /// <summary>이 그룹이 완료된 후에 실행될 워커 풀을 등록합니다.</summary>
        public EventHandlerGroup ThenWorkerPool(int workerCount, Func<int, IWorkHandler<T>> workHandlerFactory)
            => _disruptor.CreateWorkerPool(Sequences, workerCount, workHandlerFactory);

        /// <summary>
        /// 두 그룹을 합칩니다. 이후 <see cref="Then(IEventHandler{T}[])"/>는 <b>양쪽 모두</b>를 기다립니다.
        /// </summary>
        /// <example>
        /// <code>
        /// var a = disruptor.HandleEventsWith(journal);
        /// var b = disruptor.HandleEventsWith(antiCheat);
        /// a.And(b).Then(logic);     // journal 과 antiCheat 이 모두 끝난 뒤 logic
        /// </code>
        /// </example>
        public EventHandlerGroup And(EventHandlerGroup other)
        {
            ArgumentNullException.ThrowIfNull(other);

            var merged = new Sequence[Sequences.Length + other.Sequences.Length];
            Sequences.CopyTo(merged, 0);
            other.Sequences.CopyTo(merged, Sequences.Length);

            return new EventHandlerGroup(_disruptor, merged);
        }
    }

    private EventHandlerGroup CreateEventProcessors(
        Sequence[] barrierSequences,
        IEventHandler<T>[] handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        if (handlers.Length == 0)
            throw new ArgumentException("최소 1개의 핸들러가 필요합니다.", nameof(handlers));

        EnsureNotStarted();

        var processorSequences = new Sequence[handlers.Length];

        for (int i = 0; i < handlers.Length; i++)
        {
            var barrier = _ringBuffer.NewBarrier(barrierSequences);
            var processor = new BatchEventProcessor<T>(_ringBuffer, barrier, handlers[i])
            {
                MaxBatchSize = Options.MaxBatchSize,
            };
            processor.Recorder = Metrics?.CreateRecorder(processor.Name);

            if (_defaultExceptionHandler is not null)
                processor.SetExceptionHandler(_defaultExceptionHandler);

            processor.Halted += RaiseProcessorHalted;

            _batchProcessors.Add(processor);
            RegisteredProcessors.Add(processor);
            processorSequences[i] = processor.Sequence;

            RegisterProcessorDiagnostics(processor);
        }

        UpdateGatingSequences(barrierSequences, processorSequences);

        return new EventHandlerGroup(this, processorSequences);
    }

    private EventHandlerGroup CreateBatchProcessors(
        Sequence[] barrierSequences,
        IBatchEventHandler<T>[] handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        if (handlers.Length == 0)
            throw new ArgumentException("최소 1개의 핸들러가 필요합니다.", nameof(handlers));

        EnsureNotStarted();

        var processorSequences = new Sequence[handlers.Length];

        for (int i = 0; i < handlers.Length; i++)
        {
            var barrier = _ringBuffer.NewBarrier(barrierSequences);
            var processor = new BatchEventProcessor<T>(_ringBuffer, barrier, handlers[i])
            {
                MaxBatchSize = Options.MaxBatchSize,
            };
            processor.Recorder = Metrics?.CreateRecorder(processor.Name);

            if (_defaultExceptionHandler is not null)
                processor.SetExceptionHandler(_defaultExceptionHandler);

            processor.Halted += RaiseProcessorHalted;

            _batchProcessors.Add(processor);
            RegisteredProcessors.Add(processor);
            processorSequences[i] = processor.Sequence;

            RegisterProcessorDiagnostics(processor);
        }

        UpdateGatingSequences(barrierSequences, processorSequences);

        return new EventHandlerGroup(this, processorSequences);
    }

    private EventHandlerGroup CreateWorkerPool(
        Sequence[] barrierSequences,
        int workerCount,
        Func<int, IWorkHandler<T>> workHandlerFactory)
    {
        EnsureNotStarted();

        var barrier = _ringBuffer.NewBarrier(barrierSequences);
        var pool = new WorkerPool<T>(_ringBuffer, barrier, workHandlerFactory, workerCount);

        if (_defaultExceptionHandler is not null)
            pool.SetExceptionHandler(_defaultExceptionHandler);

        pool.SetMetrics(Metrics);

        foreach (var processor in pool.Processors)
        {
            processor.Halted += RaiseProcessorHalted;
            _ringBuffer.SetGatingSequenceLabel(processor.Sequence, processor.Name);
        }

        _workerPools.Add(pool);

        var poolSequences = pool.Sequences;
        UpdateGatingSequences(barrierSequences, poolSequences);

        return new EventHandlerGroup(this, poolSequences);
    }

    /// <summary>Disruptor를 시작합니다. 모든 프로세서가 이벤트 루프에 진입한 뒤 반환합니다.</summary>
    public RingBuffer<T> Start()
    {
        StartProcessorThreads();
        return _ringBuffer;
    }

    protected override void OnBeforeStart() => ConfigureSlotClearing();

    protected override void OnAfterStart()
    {
        foreach (var pool in _workerPools)
            pool.Start(ShutdownSource.Token, Options.ThreadOptions);
    }

    protected override void OnHalt()
    {
        foreach (var pool in _workerPools)
            pool.Halt();
    }

    protected override long MinimumProcessorSequence()
    {
        long min = base.MinimumProcessorSequence();

        foreach (var pool in _workerPools)
        {
            foreach (var seq in pool.Sequences)
                min = Math.Min(min, seq.Value);
        }

        return min;
    }

    /// <summary>
    /// 슬롯 자동 정리를 설정합니다.
    /// 리프 프로세서가 정확히 하나일 때만 활성화합니다(여러 개면 정리 시점에 경쟁이 발생).
    /// </summary>
    private void ConfigureSlotClearing()
    {
        if (!Options.AutoClearSlots)
            return;

        var gating = _ringBuffer.GetGatingSequences();
        var leaves = _batchProcessors.Where(p => Array.IndexOf(gating, p.Sequence) >= 0).ToList();
        bool workerPoolIsLeaf = _workerPools.Any(pool => pool.Sequences.Any(s => Array.IndexOf(gating, s) >= 0));

        if (leaves.Count == 1 && !workerPoolIsLeaf)
        {
            leaves[0].ClearAfterProcess = true;
        }
        else if (leaves.Count > 1 || workerPoolIsLeaf)
        {
            string reason = workerPoolIsLeaf
                ? "워커 풀이 마지막 단계라"
                : $"마지막 단계 핸들러가 {leaves.Count}개라";

            Console.Error.WriteLine(
                $"[Disruptor][WARN] '{Name}': {reason} 슬롯 자동 정리를 비활성화합니다 " +
                "(동시에 읽는 핸들러가 있으면 정리 시점에 경쟁이 발생). " +
                "마지막 핸들러에서 직접 참조를 해제하세요 " +
                "— 그렇지 않으면 객체 수명이 링 버퍼 한 바퀴만큼 연장됩니다.");
        }
    }

    public override void Dispose()
    {
        base.Dispose();

        foreach (var pool in _workerPools)
            pool.Dispose();
    }
}
