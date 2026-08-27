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

    private RingBuffer(ISequencer sequencer, Func<T> eventFactory)
    {
        ArgumentNullException.ThrowIfNull(eventFactory);

        _sequencer = sequencer;
        _indexMask = sequencer.BufferSize - 1;

        // 이벤트 미리 할당
        _entries = new T[sequencer.BufferSize];
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

        return new RingBuffer<T>(sequencer, eventFactory);
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
    public long Next(int n = 1) => _sequencer.Next(n);

    /// <summary>다음 시퀀스를 할당받습니다. 취소 시 <see cref="OperationCanceledException"/>을 던집니다.</summary>
    public long Next(CancellationToken cancellationToken) => _sequencer.Next(1, cancellationToken);

    /// <summary>다음 시퀀스를 할당받습니다. 취소 시 <see cref="OperationCanceledException"/>을 던집니다.</summary>
    public long Next(int n, CancellationToken cancellationToken) => _sequencer.Next(n, cancellationToken);

    /// <summary>Non-blocking 시퀀스 할당 시도.</summary>
    public bool TryNext(out long sequence, int n = 1) => _sequencer.TryNext(out sequence, n);

    /// <summary>타임아웃까지 시퀀스 할당을 시도합니다.</summary>
    public bool TryNext(out long sequence, TimeSpan timeout) => _sequencer.TryNext(out sequence, 1, timeout);

    /// <summary>타임아웃까지 시퀀스 할당을 시도합니다.</summary>
    public bool TryNext(out long sequence, int n, TimeSpan timeout) => _sequencer.TryNext(out sequence, n, timeout);

    /// <summary>시퀀스를 게시합니다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Publish(long sequence) => _sequencer.Publish(sequence);

    /// <summary>범위를 게시합니다.</summary>
    public void Publish(long lo, long hi) => _sequencer.Publish(lo, hi);

    /// <summary>이벤트를 게시합니다 (translator 패턴).</summary>
    public void PublishEvent(Action<T, long> translator)
    {
        long sequence = _sequencer.Next();
        try
        {
            translator(this[sequence], sequence);
        }
        finally
        {
            _sequencer.Publish(sequence);
        }
    }

    /// <summary>인자와 함께 이벤트를 게시합니다.</summary>
    public void PublishEvent<TArg>(Action<T, long, TArg> translator, TArg arg)
    {
        long sequence = _sequencer.Next();
        try
        {
            translator(this[sequence], sequence, arg);
        }
        finally
        {
            _sequencer.Publish(sequence);
        }
    }

    /// <summary>여러 이벤트를 한 번에 게시합니다. 게시 신호가 1회만 발생해 배치 효율이 높습니다.</summary>
    public void PublishEvents<TArg>(ReadOnlySpan<TArg> args, Action<T, long, TArg> translator)
    {
        if (args.Length == 0)
            return;

        long hi = _sequencer.Next(args.Length);
        long lo = hi - args.Length + 1;
        try
        {
            for (int i = 0; i < args.Length; i++)
                translator(this[lo + i], lo + i, args[i]);
        }
        finally
        {
            _sequencer.Publish(lo, hi);
        }
    }

    /// <summary>RAII 스타일 이벤트 게시 (using 패턴).</summary>
    public EventScope PublishEventScope()
    {
        long sequence = _sequencer.Next();
        return new EventScope(this, sequence);
    }

    /// <summary>RAII 스타일 이벤트 게시 시도. 실패하면 <see cref="EventScope.IsValid"/>가 false입니다.</summary>
    public EventScope TryPublishEventScope(TimeSpan timeout)
        => _sequencer.TryNext(out long sequence, 1, timeout)
            ? new EventScope(this, sequence)
            : default;

    public int BufferSize => _sequencer.BufferSize;

    /// <summary>
    /// 현재 커서. 다중 프로듀서에서는 "점유된" 최대치이므로 게시 여부와 다를 수 있습니다.
    /// </summary>
    public long Cursor => _sequencer.Cursor;

    /// <summary>지금까지 연속으로 게시된 최고 시퀀스.</summary>
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
        => _sequencer.GetHighestPublishedSequence(lowerBound, availableSequence);

    internal ISequencer Sequencer => _sequencer;

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

        public T Event() => _ringBuffer is null
            ? throw new InvalidOperationException("시퀀스 점유에 실패한 스코프입니다. IsValid를 먼저 확인하세요.")
            : _ringBuffer[_sequence];

        public long Sequence => _sequence;

        public void Dispose() => _ringBuffer?.Publish(_sequence);
    }
}
