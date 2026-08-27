using Disruptor.WaitStrategy;

namespace Disruptor.Dsl;

/// <summary>
/// 값 타입 이벤트용 Disruptor DSL.
/// 슬롯이 배열에 인라인으로 놓이므로 포인터 추적과 GC 스캔이 사라집니다.
/// </summary>
public sealed class ValueDisruptor<T> : DisruptorBase where T : struct
{
    private readonly ValueRingBuffer<T> _ringBuffer;
    private readonly List<ValueBatchEventProcessor<T>> _batchProcessors = new();

    private IExceptionHandler<T>? _defaultExceptionHandler;

    public ValueDisruptor(
        int ringBufferSize,
        ProducerType producerType = ProducerType.Single,
        IWaitStrategy? waitStrategy = null,
        DisruptorOptions? options = null,
        Func<T>? eventFactory = null)
        : base(options ?? DisruptorOptions.Default)
    {
        waitStrategy ??= Options.CreateDefaultWaitStrategy();

        _ringBuffer = ValueRingBuffer<T>.Create(
            producerType, ringBufferSize, waitStrategy, eventFactory,
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

    public ValueRingBuffer<T> RingBuffer => _ringBuffer;

    protected override IRingBufferCore Core => _ringBuffer;

    public void SetDefaultExceptionHandler(IExceptionHandler<T> exceptionHandler)
    {
        ArgumentNullException.ThrowIfNull(exceptionHandler);
        _defaultExceptionHandler = exceptionHandler;

        foreach (var processor in _batchProcessors)
            processor.SetExceptionHandler(exceptionHandler);
    }

    public ValueEventHandlerGroup HandleEventsWith(params IValueEventHandler<T>[] handlers)
        => CreateEventProcessors([], handlers);

    /// <summary>배치 핸들러를 등록합니다. 이벤트 구간을 쓰기 가능한 span 으로 받습니다.</summary>
    public ValueEventHandlerGroup HandleEventsWith(params IValueBatchEventHandler<T>[] handlers)
        => CreateBatchProcessors([], handlers);

    public sealed class ValueEventHandlerGroup
    {
        private readonly ValueDisruptor<T> _disruptor;

        internal ValueEventHandlerGroup(ValueDisruptor<T> disruptor, Sequence[] sequences)
        {
            _disruptor = disruptor;
            Sequences = sequences;
        }

        public Sequence[] Sequences { get; }

        public ValueEventHandlerGroup Then(params IValueEventHandler<T>[] handlers)
            => _disruptor.CreateEventProcessors(Sequences, handlers);

        /// <summary>이 그룹이 완료된 후에 실행될 배치 핸들러를 등록합니다.</summary>
        public ValueEventHandlerGroup Then(params IValueBatchEventHandler<T>[] handlers)
            => _disruptor.CreateBatchProcessors(Sequences, handlers);

        /// <inheritdoc cref="Disruptor{T}.EventHandlerGroup.And"/>
        public ValueEventHandlerGroup And(ValueEventHandlerGroup other)
        {
            ArgumentNullException.ThrowIfNull(other);

            var merged = new Sequence[Sequences.Length + other.Sequences.Length];
            Sequences.CopyTo(merged, 0);
            other.Sequences.CopyTo(merged, Sequences.Length);

            return new ValueEventHandlerGroup(_disruptor, merged);
        }
    }

    private ValueEventHandlerGroup CreateBatchProcessors(
        Sequence[] barrierSequences,
        IValueBatchEventHandler<T>[] handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        if (handlers.Length == 0)
            throw new ArgumentException("최소 1개의 핸들러가 필요합니다.", nameof(handlers));

        EnsureNotStarted();

        var processorSequences = new Sequence[handlers.Length];

        for (int i = 0; i < handlers.Length; i++)
        {
            var barrier = _ringBuffer.NewBarrier(barrierSequences);
            var processor = new ValueBatchEventProcessor<T>(_ringBuffer, barrier, handlers[i])
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

        return new ValueEventHandlerGroup(this, processorSequences);
    }

    private ValueEventHandlerGroup CreateEventProcessors(
        Sequence[] barrierSequences,
        IValueEventHandler<T>[] handlers)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        if (handlers.Length == 0)
            throw new ArgumentException("최소 1개의 핸들러가 필요합니다.", nameof(handlers));

        EnsureNotStarted();

        var processorSequences = new Sequence[handlers.Length];

        for (int i = 0; i < handlers.Length; i++)
        {
            var barrier = _ringBuffer.NewBarrier(barrierSequences);
            var processor = new ValueBatchEventProcessor<T>(_ringBuffer, barrier, handlers[i])
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

        return new ValueEventHandlerGroup(this, processorSequences);
    }

    public ValueRingBuffer<T> Start()
    {
        StartProcessorThreads();
        return _ringBuffer;
    }

    protected override void OnBeforeStart()
    {
        if (!Options.AutoClearSlots)
            return;

        var gating = _ringBuffer.GetGatingSequences();
        var leaves = _batchProcessors.Where(p => Array.IndexOf(gating, p.Sequence) >= 0).ToList();

        if (leaves.Count == 1)
            leaves[0].ClearAfterProcess = true;
    }
}
