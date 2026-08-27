using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Disruptor.WaitStrategy;

namespace Disruptor;

/// <summary>
/// 값 타입 이벤트를 인라인으로 저장하는 링 버퍼.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RingBuffer{T}"/>는 슬롯이 <b>참조 배열</b>이므로 이벤트 하나를 읽을 때마다
/// (1) 배열 슬롯 로드 → (2) 참조를 따라가 객체 로드 라는 2단계 접근이 발생하고,
/// (2)는 거의 확실히 캐시 미스입니다. 배치로 1,000건을 처리하면 1,000번의 포인터 추적이 일어납니다.
/// </para>
/// <para>
/// 값 타입 이벤트는 배열에 인라인으로 놓이므로 순차 접근이 되고 하드웨어 프리페처가 정확히 동작합니다.
/// 더 중요하게는, 구조체에 <b>참조 필드가 없으면 배열 전체가 GC 스캔 대상에서 제외</b>되어
/// 1M 슬롯 링 버퍼가 Gen2 마킹 비용에 전혀 기여하지 않습니다.
/// </para>
/// <para>
/// <b>슬롯 간 false sharing 주의</b>: 여러 컨슈머가 인접 슬롯을 동시에 다루면 캐시 라인이 핑퐁합니다.
/// 구조체 크기를 캐시 라인의 배수(64B, 128B)로 맞추거나 패딩을 넣으세요.
/// </para>
/// </remarks>
public sealed class ValueRingBuffer<T> : IRingBufferCore where T : struct
{
    private readonly T[] _entries;
    private readonly int _indexMask;
    private readonly ISequencer _sequencer;

    private ValueRingBuffer(ISequencer sequencer, Func<T>? eventFactory)
    {
        _sequencer = sequencer;
        _indexMask = sequencer.BufferSize - 1;
        _entries = new T[sequencer.BufferSize];

        if (eventFactory is not null)
        {
            for (int i = 0; i < _entries.Length; i++)
                _entries[i] = eventFactory();
        }
    }

    public static ValueRingBuffer<T> Create(
        ProducerType producerType,
        int bufferSize,
        IWaitStrategy waitStrategy,
        Func<T>? eventFactory = null,
        SequencerOptions? options = null)
    {
        ISequencer sequencer = producerType switch
        {
            ProducerType.Single => new SingleProducerSequencer(bufferSize, waitStrategy, options),
            ProducerType.Multi => new MultiProducerSequencer(bufferSize, waitStrategy, options),
            _ => throw new ArgumentOutOfRangeException(nameof(producerType))
        };

        return new ValueRingBuffer<T>(sequencer, eventFactory);
    }

    public static ValueRingBuffer<T> CreateSingleProducer(
        int bufferSize, IWaitStrategy waitStrategy, Func<T>? eventFactory = null, SequencerOptions? options = null)
        => Create(ProducerType.Single, bufferSize, waitStrategy, eventFactory, options);

    public static ValueRingBuffer<T> CreateMultiProducer(
        int bufferSize, IWaitStrategy waitStrategy, Func<T>? eventFactory = null, SequencerOptions? options = null)
        => Create(ProducerType.Multi, bufferSize, waitStrategy, eventFactory, options);

    /// <summary>슬롯을 <c>ref</c>로 반환합니다 — 복사가 없습니다.</summary>
    public ref T this[long sequence]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_entries),
                              (nint)(uint)((int)sequence & _indexMask));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Next(int n = 1) => _sequencer.Next(n);

    public long Next(CancellationToken cancellationToken) => _sequencer.Next(1, cancellationToken);

    public long Next(int n, CancellationToken cancellationToken) => _sequencer.Next(n, cancellationToken);

    public bool TryNext(out long sequence, int n = 1) => _sequencer.TryNext(out sequence, n);

    public bool TryNext(out long sequence, TimeSpan timeout) => _sequencer.TryNext(out sequence, 1, timeout);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Publish(long sequence) => _sequencer.Publish(sequence);

    public void Publish(long lo, long hi) => _sequencer.Publish(lo, hi);

    /// <summary>이벤트를 게시합니다. translator는 슬롯을 ref로 받습니다.</summary>
    public void PublishEvent(ValueEventTranslator translator)
    {
        long sequence = _sequencer.Next();
        try
        {
            translator(ref this[sequence], sequence);
        }
        finally
        {
            _sequencer.Publish(sequence);
        }
    }

    /// <summary>인자와 함께 이벤트를 게시합니다.</summary>
    public void PublishEvent<TArg>(ValueEventTranslator<TArg> translator, TArg arg)
    {
        long sequence = _sequencer.Next();
        try
        {
            translator(ref this[sequence], sequence, arg);
        }
        finally
        {
            _sequencer.Publish(sequence);
        }
    }

    /// <summary>RAII 스타일 이벤트 게시 (using 패턴).</summary>
    public ValueEventScope PublishEventScope()
        => new(this, _sequencer.Next());

    /// <summary>RAII 스타일 이벤트 게시 시도.</summary>
    public ValueEventScope TryPublishEventScope(TimeSpan timeout)
        => _sequencer.TryNext(out long sequence, 1, timeout)
            ? new ValueEventScope(this, sequence)
            : default;

    public delegate void ValueEventTranslator(ref T evt, long sequence);

    public delegate void ValueEventTranslator<in TArg>(ref T evt, long sequence, TArg arg);

    public int BufferSize => _sequencer.BufferSize;

    public long Cursor => _sequencer.Cursor;

    public long RemainingCapacity => _sequencer.GetRemainingCapacity();

    public double Utilization => 1.0 - (double)RemainingCapacity / BufferSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsCongested(double threshold = 0.8)
        => RemainingCapacity < BufferSize * (1.0 - threshold);

    public bool HasAvailableCapacity(int n) => _sequencer.HasAvailableCapacity(n);

    public void AddGatingSequences(params Sequence[] gatingSequences)
        => _sequencer.AddGatingSequences(gatingSequences);

    public bool RemoveGatingSequence(Sequence sequence) => _sequencer.RemoveGatingSequence(sequence);

    public Sequence[] GetGatingSequences() => _sequencer.GetGatingSequences();

    public void SetGatingSequenceLabel(Sequence sequence, string label)
        => _sequencer.SetGatingSequenceLabel(sequence, label);

    public ISequenceBarrier NewBarrier(params Sequence[] sequencesToTrack)
        => _sequencer.NewBarrier(sequencesToTrack);

    public long GetMinimumGatingSequence() => _sequencer.GetMinimumSequence();

    public bool IsPublished(long sequence) => _sequencer.IsAvailable(sequence);

    public long GetHighestPublishedSequence(long lowerBound, long availableSequence)
        => _sequencer.GetHighestPublishedSequence(lowerBound, availableSequence);

    /// <summary>슬롯을 <c>default</c>로 초기화합니다. 구조체가 참조 필드를 가질 때 사용합니다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void ClearSlot(long sequence) => this[sequence] = default;

    /// <summary>
    /// RAII 스타일 값 이벤트 게시 스코프.
    /// </summary>
    public readonly ref struct ValueEventScope
    {
        private readonly ValueRingBuffer<T>? _ringBuffer;
        private readonly long _sequence;

        internal ValueEventScope(ValueRingBuffer<T> ringBuffer, long sequence)
        {
            _ringBuffer = ringBuffer;
            _sequence = sequence;
        }

        public bool IsValid => _ringBuffer is not null;

        public long Sequence => _sequence;

        public ref T Event => ref (_ringBuffer
            ?? throw new InvalidOperationException("시퀀스 점유에 실패한 스코프입니다. IsValid를 먼저 확인하세요."))[_sequence];

        public void Dispose() => _ringBuffer?.Publish(_sequence);
    }
}
