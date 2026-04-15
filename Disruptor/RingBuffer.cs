using Disruptor.WaitStrategy;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace Disruptor;

public enum ProducerType
{
    Single,
    Multi
}

/// <summary>
/// 고성능 링 버퍼. 이벤트를 미리 할당하여 GC 압력을 최소화합니다.
/// </summary>
public sealed class RingBuffer<T> where T : class
{
    private readonly T[] _entries;
    private readonly int _indexMask;
    private readonly ISequencer _sequencer;

    private RingBuffer(ISequencer sequencer, Func<T> eventFactory)
    {
        _sequencer = sequencer;
        _indexMask = sequencer.BufferSize - 1;

        // 이벤트 미리 할당
        _entries = new T[sequencer.BufferSize];
        for (int i = 0; i < sequencer.BufferSize; i++)
        {
            _entries[i] = eventFactory();
        }
    }

    /// <summary>링 버퍼를 생성합니다.</summary>
    public static RingBuffer<T> Create(
        ProducerType producerType,
        Func<T> eventFactory,
        int bufferSize,
        IWaitStrategy waitStrategy)
    {
        ISequencer sequencer = producerType switch
        {
            ProducerType.Single => new SingleProducerSequencer(bufferSize, waitStrategy),
            ProducerType.Multi => new MultiProducerSequencer(bufferSize, waitStrategy),
            _ => throw new ArgumentOutOfRangeException(nameof(producerType))
        };

        return new RingBuffer<T>(sequencer, eventFactory);
    }

    /// <summary>단일 프로듀서 링 버퍼를 생성합니다.</summary>
    public static RingBuffer<T> CreateSingleProducer(
        Func<T> eventFactory, int bufferSize, IWaitStrategy waitStrategy)
    {
        return Create(ProducerType.Single, eventFactory, bufferSize, waitStrategy);
    }

    /// <summary>다중 프로듀서 링 버퍼를 생성합니다.</summary>
    public static RingBuffer<T> CreateMultiProducer(
        Func<T> eventFactory, int bufferSize, IWaitStrategy waitStrategy)
    {
        return Create(ProducerType.Multi, eventFactory, bufferSize, waitStrategy);
    }

    /// <summary>시퀀스 번호로 이벤트를 가져옵니다.</summary>
    public T this[long sequence]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _entries[(int)sequence & _indexMask];
    }

    /// <summary>다음 시퀀스를 할당받습니다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Next(int n = 1) => _sequencer.Next(n);

    /// <summary>Non-blocking 시퀀스 할당 시도.</summary>
    public bool TryNext(out long sequence, int n = 1) => _sequencer.TryNext(out sequence, n);

    /// <summary>시퀀스를 게시합니다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Publish(long sequence) => _sequencer.Publish(sequence);

    /// <summary>범위를 게시합니다.</summary>
    public void Publish(long lo, long hi) => _sequencer.Publish(lo, hi);

    /// <summary>
    /// 이벤트를 게시합니다 (translator 패턴).
    /// </summary>
    public void PublishEvent(Action<T, long> translator)
    {
        long sequence = _sequencer.Next();
        try
        {
            translator(_entries[(int)sequence & _indexMask], sequence);
        }
        finally
        {
            _sequencer.Publish(sequence);
        }
    }

    /// <summary>
    /// 인자와 함께 이벤트를 게시합니다.
    /// </summary>
    public void PublishEvent<TArg>(Action<T, long, TArg> translator, TArg arg)
    {
        long sequence = _sequencer.Next();
        try
        {
            translator(_entries[(int)sequence & _indexMask], sequence, arg);
        }
        finally
        {
            _sequencer.Publish(sequence);
        }
    }

    /// <summary>RAII 스타일 이벤트 게시 (using 패턴).</summary>
    public EventScope PublishEventScope()
    {
        long sequence = _sequencer.Next();
        return new EventScope(this, sequence);
    }

    public int BufferSize => _sequencer.BufferSize;
    public long Cursor => _sequencer.Cursor;

    public void AddGatingSequences(params Sequence[] gatingSequences)
        => _sequencer.AddGatingSequences(gatingSequences);

    public bool RemoveGatingSequence(Sequence sequence)
        => _sequencer.RemoveGatingSequence(sequence);

    public ISequenceBarrier NewBarrier(params Sequence[] sequencesToTrack)
        => _sequencer.NewBarrier(sequencesToTrack);

    public long GetMinimumGatingSequence()
        => _sequencer.GetMinimumSequence();

    public bool IsPublished(long sequence)
        => _sequencer.IsAvailable(sequence);

    public long GetHighestPublishedSequence(long lowerBound, long availableSequence)
        => _sequencer.GetHighestPublishedSequence(lowerBound, availableSequence);

    /// <summary>
    /// RAII 스타일 이벤트 게시를 위한 구조체. using과 함께 사용합니다.
    /// </summary>
    public readonly struct EventScope : IDisposable
    {
        private readonly RingBuffer<T> _ringBuffer;
        private readonly long _sequence;

        internal EventScope(RingBuffer<T> ringBuffer, long sequence)
        {
            _ringBuffer = ringBuffer;
            _sequence = sequence;
        }

        public T Event() => _ringBuffer[_sequence];
        public long Sequence => _sequence;

        public void Dispose()
        {
            _ringBuffer.Publish(_sequence);
        }
    }
}