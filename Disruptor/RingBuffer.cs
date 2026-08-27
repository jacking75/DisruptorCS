using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Disruptor.WaitStrategy;

namespace Disruptor;

public enum ProducerType
{
    Single,
    Multi
}

/// <summary>
/// 고성능 링 버퍼. 이벤트를 미리 할당하여 GC 압력을 최소화합니다.
/// </summary>
public sealed class RingBuffer<T> : IRingBufferCore where T : class
{
    private readonly T[] _entries;
    private readonly int _indexMask;
    private readonly ISequencer _sequencer;

    // 게시 경로에서 인터페이스 디스패치를 제거한다 (SequencerDispatcher 주석 참조).
    private readonly SequencerDispatcher _dispatcher;

    private RingBuffer(ISequencer sequencer, Func<T> eventFactory, bool usePinnedHeap)
    {
        ArgumentNullException.ThrowIfNull(eventFactory);

        _sequencer = sequencer;
        _dispatcher = new SequencerDispatcher(sequencer);
        _indexMask = sequencer.BufferSize - 1;

        // 이벤트 미리 할당.
        // POH 를 쓰면 큰 배열이 LOH 조각화에 기여하지 않는다 (SequencerOptions.UsePinnedObjectHeap 참조).
        _entries = usePinnedHeap
            ? GC.AllocateArray<T>(sequencer.BufferSize, pinned: true)
            : new T[sequencer.BufferSize];
        for (int i = 0; i < sequencer.BufferSize; i++)
        {
            _entries[i] = eventFactory()
                          ?? throw new InvalidOperationException("eventFactory가 null을 반환했습니다.");
        }
    }

    /// <summary>링 버퍼를 생성합니다.</summary>
    public static RingBuffer<T> Create(
        ProducerType producerType,
        Func<T> eventFactory,
        int bufferSize,
        IWaitStrategy waitStrategy,
        SequencerOptions? options = null)
    {
        ISequencer sequencer = producerType switch
        {
            ProducerType.Single => new SingleProducerSequencer(bufferSize, waitStrategy, options),
            ProducerType.Multi => new MultiProducerSequencer(bufferSize, waitStrategy, options),
            _ => throw new ArgumentOutOfRangeException(nameof(producerType))
        };

        return new RingBuffer<T>(sequencer, eventFactory, options?.UsePinnedObjectHeap ?? false);
    }

    /// <summary>단일 프로듀서 링 버퍼를 생성합니다.</summary>
    public static RingBuffer<T> CreateSingleProducer(
        Func<T> eventFactory, int bufferSize, IWaitStrategy waitStrategy, SequencerOptions? options = null)
        => Create(ProducerType.Single, eventFactory, bufferSize, waitStrategy, options);

    /// <summary>다중 프로듀서 링 버퍼를 생성합니다.</summary>
    public static RingBuffer<T> CreateMultiProducer(
        Func<T> eventFactory, int bufferSize, IWaitStrategy waitStrategy, SequencerOptions? options = null)
        => Create(ProducerType.Multi, eventFactory, bufferSize, waitStrategy, options);

    /// <summary>시퀀스 번호로 이벤트를 가져옵니다.</summary>
    /// <remarks>
    /// 인덱스는 <c>sequence &amp; (bufferSize - 1)</c>로 계산되고 bufferSize는 2의 거듭제곱임이
    /// 생성자에서 강제되므로 항상 유효합니다. 따라서 배열 경계 검사는 순수한 낭비이며 제거합니다.
    /// </remarks>
    public T this[long sequence]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_entries),
                          (nint)(uint)((int)sequence & _indexMask));
    }

    /// <summary>다음 시퀀스를 할당받습니다. 용량이 없으면 대기합니다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Next(int n = 1) => n == 1 ? _dispatcher.Next() : _dispatcher.Next(n);

    /// <summary>다음 시퀀스를 할당받습니다. 취소 시 <see cref="OperationCanceledException"/>을 던집니다.</summary>
    public long Next(CancellationToken cancellationToken) => _dispatcher.Next(1, cancellationToken);

    /// <summary>다음 시퀀스를 할당받습니다. 취소 시 <see cref="OperationCanceledException"/>을 던집니다.</summary>
    public long Next(int n, CancellationToken cancellationToken) => _dispatcher.Next(n, cancellationToken);

    /// <summary>Non-blocking 시퀀스 할당 시도.</summary>
    public bool TryNext(out long sequence, int n = 1) => _dispatcher.TryNext(out sequence, n);

    /// <summary>타임아웃까지 시퀀스 할당을 시도합니다.</summary>
    public bool TryNext(out long sequence, TimeSpan timeout) => _dispatcher.TryNext(out sequence, 1, timeout);

    /// <summary>타임아웃까지 시퀀스 할당을 시도합니다.</summary>
    public bool TryNext(out long sequence, int n, TimeSpan timeout) => _dispatcher.TryNext(out sequence, n, timeout);

    /// <summary>시퀀스를 게시합니다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Publish(long sequence) => _dispatcher.Publish(sequence);

    /// <summary>범위를 게시합니다.</summary>
    public void Publish(long lo, long hi) => _dispatcher.Publish(lo, hi);

    /// <summary>
    /// 연속된 슬롯 조각을 span 으로 노출합니다. 랩 경계를 넘지 않는 범위여야 합니다.
    /// </summary>
    /// <remarks>호출자(<see cref="BatchEventProcessor{T}"/>)가 랩 분할을 보장합니다.</remarks>
    internal ReadOnlySpan<T> GetSegment(long startSequence, long endSequenceInclusive)
    {
        int startIndex = (int)startSequence & _indexMask;
        int length = (int)(endSequenceInclusive - startSequence + 1);
        return new ReadOnlySpan<T>(_entries, startIndex, length);
    }

    /// <summary>
    /// 전용 스레드 없이 호출자의 스레드에서 이벤트를 당겨 처리하는 폴러를 만듭니다.
    /// </summary>
    /// <remarks>
    /// 게임 메인 루프 · Unity <c>Update()</c> · 기존 프레임 루프와의 통합 지점입니다.
    /// "Disruptor 를 쓰려면 스레드 모델을 바꿔야 한다"는 제약이 사라집니다.
    /// </remarks>
    /// <param name="dependentSequences">
    /// 이 폴러가 기다릴 상위 핸들러 시퀀스. 비우면 프로듀서 커서를 직접 따라갑니다.
    /// </param>
    public EventPoller<T> NewPoller(params Sequence[] dependentSequences)
        => new(this, CursorSequence, dependentSequences ?? []);

    /// <summary>이벤트를 게시합니다 (translator 패턴).</summary>
    /// <remarks>
    /// <b>캡처하는 람다를 넘기지 마세요.</b> 게시마다 클로저와 델리게이트가 할당되어
    /// "정상 경로 할당 0" 계약이 깨집니다. <c>static</c> 람다 + <see cref="PublishEvent{TArg}"/>
    /// 오버로드나, 할당이 전혀 없는
    /// <see cref="PublishEvent{TTranslator, TArg}(TTranslator, TArg)"/>를 사용하세요.
    /// <code>
    /// // 나쁨: i 를 캡처 -> 게시마다 할당
    /// ring.PublishEvent((evt, seq) => evt.Id = i);
    /// // 좋음: static 람다 + 인자 전달
    /// ring.PublishEvent(static (evt, seq, id) => evt.Id = id, i);
    /// </code>
    /// </remarks>
    public void PublishEvent(Action<T, long> translator)
    {
        long sequence = _dispatcher.Next();
        try
        {
            translator(this[sequence], sequence);
        }
        finally
        {
            _dispatcher.Publish(sequence);
        }
    }

    /// <summary>인자와 함께 이벤트를 게시합니다.</summary>
    public void PublishEvent<TArg>(Action<T, long, TArg> translator, TArg arg)
    {
        long sequence = _dispatcher.Next();
        try
        {
            translator(this[sequence], sequence, arg);
        }
        finally
        {
            _dispatcher.Publish(sequence);
        }
    }

    /// <summary>
    /// 할당이 전혀 없는 게시. <typeparamref name="TTranslator"/>가 struct 이므로
    /// 델리게이트도 클로저도 만들어지지 않고 <c>Translate</c> 가 인라인됩니다.
    /// </summary>
    /// <example>
    /// <code>
    /// readonly struct FillOrder : IEventTranslator&lt;OrderEvent, long&gt;
    /// {
    ///     public void Translate(OrderEvent evt, long sequence, long id) => evt.OrderId = id;
    /// }
    /// ring.PublishEvent(default(FillOrder), orderId);
    /// </code>
    /// </example>
    public void PublishEvent<TTranslator, TArg>(TTranslator translator, TArg arg)
        where TTranslator : struct, IEventTranslator<T, TArg>
    {
        long sequence = _dispatcher.Next();
        try
        {
            translator.Translate(this[sequence], sequence, arg);
        }
        finally
        {
            _dispatcher.Publish(sequence);
        }
    }

    /// <summary>여러 이벤트를 한 번에 게시합니다. 게시 신호가 1회만 발생해 배치 효율이 높습니다.</summary>
    public void PublishEvents<TArg>(ReadOnlySpan<TArg> args, Action<T, long, TArg> translator)
    {
        if (args.Length == 0)
            return;

        long hi = _dispatcher.Next(args.Length);
        long lo = hi - args.Length + 1;
        try
        {
            for (int i = 0; i < args.Length; i++)
                translator(this[lo + i], lo + i, args[i]);
        }
        finally
        {
            _dispatcher.Publish(lo, hi);
        }
    }

    /// <summary>RAII 스타일 이벤트 게시 (using 패턴).</summary>
    public EventScope PublishEventScope()
    {
        long sequence = _dispatcher.Next();
        return new EventScope(this, sequence);
    }

    /// <summary>RAII 스타일 이벤트 게시 시도. 실패하면 <see cref="EventScope.IsValid"/>가 false입니다.</summary>
    public EventScope TryPublishEventScope(TimeSpan timeout)
        => _dispatcher.TryNext(out long sequence, 1, timeout)
            ? new EventScope(this, sequence)
            : default;

    public int BufferSize => _sequencer.BufferSize;

    /// <summary>
    /// 현재 커서. 다중 프로듀서에서는 "점유된" 최대치이므로 게시 여부와 다를 수 있습니다.
    /// </summary>
    public long Cursor => _sequencer.Cursor;

    /// <summary>지금까지 연속으로 게시된 최고 시퀀스.</summary>
    /// <remarks>
    /// <b>비싼 프로퍼티입니다.</b> 다중 프로듀서에서는 게이팅 최솟값부터 커서까지 슬롯을 하나씩
    /// 스캔하므로 O(뒤처진 이벤트 수)입니다. 모니터링 코드에서 주기적으로 폴링하지 마세요 —
    /// 대시보드용 지표는 <see cref="Cursor"/> 와 <see cref="Utilization"/> 로 충분합니다.
    /// </remarks>
    public long PublishedCursor => _sequencer.GetHighestPublishedSequence(_sequencer.GetMinimumSequence() + 1, _sequencer.Cursor);

    /// <summary>지금 게시 가능한 슬롯 수.</summary>
    public long RemainingCapacity => _sequencer.GetRemainingCapacity();

    /// <summary>링 버퍼 사용률 (0~1).</summary>
    public double Utilization => 1.0 - (double)RemainingCapacity / BufferSize;

    /// <summary>버퍼가 지정 비율 이상 찼는가. 사전 드롭 판정에 사용합니다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsCongested(double threshold = 0.8)
        => RemainingCapacity < BufferSize * (1.0 - threshold);

    /// <summary>n개의 슬롯을 지금 게시할 수 있는가.</summary>
    public bool HasAvailableCapacity(int n) => _sequencer.HasAvailableCapacity(n);

    public void AddGatingSequences(params Sequence[] gatingSequences)
        => _sequencer.AddGatingSequences(gatingSequences);

    public bool RemoveGatingSequence(Sequence sequence)
        => _sequencer.RemoveGatingSequence(sequence);

    public Sequence[] GetGatingSequences() => _sequencer.GetGatingSequences();

    public void SetGatingSequenceLabel(Sequence sequence, string label)
        => _sequencer.SetGatingSequenceLabel(sequence, label);

    public ISequenceBarrier NewBarrier(params Sequence[] sequencesToTrack)
        => _sequencer.NewBarrier(sequencesToTrack);

    public long GetMinimumGatingSequence() => _sequencer.GetMinimumSequence();

    public bool IsPublished(long sequence) => _sequencer.IsAvailable(sequence);

    public long GetHighestPublishedSequence(long lowerBound, long availableSequence)
        => _dispatcher.GetHighestPublishedSequence(lowerBound, availableSequence);

    internal ISequencer Sequencer => _sequencer;

    /// <summary>프로듀서 커서 시퀀스 객체. 폴러/배리어 구성에 사용합니다.</summary>
    internal Sequence CursorSequence => _sequencer.CursorSequence;

    /// <summary>
    /// RAII 스타일 이벤트 게시를 위한 구조체. using과 함께 사용합니다.
    /// </summary>
    public readonly struct EventScope : IDisposable
    {
        private readonly RingBuffer<T>? _ringBuffer;
        private readonly long _sequence;

        internal EventScope(RingBuffer<T> ringBuffer, long sequence)
        {
            _ringBuffer = ringBuffer;
            _sequence = sequence;
        }

        /// <summary>시퀀스 점유에 성공했는가.</summary>
        public bool IsValid => _ringBuffer is not null;

        /// <summary>점유한 슬롯. 값 타입 스코프의 <c>Event</c> 와 이름을 맞춘 형태입니다.</summary>
        public T Event => _ringBuffer is null
            ? throw new InvalidOperationException("시퀀스 점유에 실패한 스코프입니다. IsValid를 먼저 확인하세요.")
            : _ringBuffer[_sequence];

        /// <summary>점유한 슬롯을 가져옵니다.</summary>
        /// <remarks>
        /// <see cref="Event"/> 프로퍼티와 동일합니다. 값 타입 스코프(<c>ValueEventScope.Event</c>)가
        /// 프로퍼티이므로 그쪽에 이름을 맞췄고, 이 메서드는 기존 코드 호환을 위해 남겨 둡니다.
        /// </remarks>
        [Obsolete("Event 프로퍼티를 사용하세요. 값 타입 스코프와 형태를 맞추기 위한 변경입니다.")]
        public T GetEvent() => Event;

        public long Sequence => _sequence;

        public void Dispose() => _ringBuffer?.Publish(_sequence);
    }
}
