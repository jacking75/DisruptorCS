using Disruptor.WaitStrategy;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace Disruptor;

/// <summary>
/// 단일 프로듀서 최적화 시퀀서.
/// CAS 연산 없이 동작하여 최고 성능을 제공합니다.
/// </summary>
public sealed class SingleProducerSequencer : ISequencer
{
    private readonly int _bufferSize;
    private readonly IWaitStrategy _waitStrategy;
    private readonly Sequence _cursor = new();

    // 게이팅 시퀀스 (컨슈머들의 시퀀스)
    private volatile Sequence[] _gatingSequences = Array.Empty<Sequence>();

    // 단일 프로듀서 최적화: 캐시된 값
    private long _nextValue = Sequence.InitialCursorValue;
    private long _cachedGatingValue = Sequence.InitialCursorValue;

    public SingleProducerSequencer(int bufferSize, IWaitStrategy waitStrategy)
    {
        if (bufferSize < 1)
            throw new ArgumentException("bufferSize must be positive");
        if ((bufferSize & (bufferSize - 1)) != 0)
            throw new ArgumentException("bufferSize must be a power of 2");

        _bufferSize = bufferSize;
        _waitStrategy = waitStrategy;
    }

    public int BufferSize => _bufferSize;
    public long Cursor => _cursor.Value;

    public long Next(int n = 1)
    {
        if (n < 1)
            throw new ArgumentException("n must be positive");

        long nextValue = _nextValue;
        long nextSequence = nextValue + n;
        long wrapPoint = nextSequence - _bufferSize;
        long cachedGatingSequence = _cachedGatingValue;

        if (wrapPoint > cachedGatingSequence || cachedGatingSequence > nextValue)
        {
            // 컨슈머를 기다려야 함
            long minSequence;
            var spinWait = new SpinWait();
            while (wrapPoint > (minSequence = GetMinimumGatingSequence()))
            {
                spinWait.SpinOnce();
            }

            _cachedGatingValue = minSequence;
        }

        _nextValue = nextSequence;
        return nextSequence;
    }

    public bool TryNext(out long sequence, int n = 1)
    {
        if (n < 1)
            throw new ArgumentException("n must be positive");

        long nextValue = _nextValue;
        long nextSequence = nextValue + n;
        long wrapPoint = nextSequence - _bufferSize;
        long cachedGatingSequence = _cachedGatingValue;

        if (wrapPoint > cachedGatingSequence || cachedGatingSequence > nextValue)
        {
            long minSequence = GetMinimumGatingSequence();
            _cachedGatingValue = minSequence;

            if (wrapPoint > minSequence)
            {
                sequence = 0;
                return false;
            }
        }

        _nextValue = nextSequence;
        sequence = nextSequence;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Publish(long sequence)
    {
        _cursor.SetValue(sequence);
        _waitStrategy.SignalAllWhenBlocking();
    }

    public void Publish(long lo, long hi)
    {
        Publish(hi);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsAvailable(long sequence)
    {
        return sequence <= _cursor.Value;
    }

    public long GetHighestPublishedSequence(long lowerBound, long availableSequence)
    {
        return availableSequence;
    }

    public void AddGatingSequences(params Sequence[] gatingSequences)
    {
#pragma warning disable CS0420 // volatile 필드를 ref로 전달 — CAS 패턴에서 의도적 사용
        SequenceGroups.AddSequences(ref _gatingSequences, gatingSequences);
#pragma warning restore CS0420
    }

    public bool RemoveGatingSequence(Sequence sequence)
    {
#pragma warning disable CS0420
        return SequenceGroups.RemoveSequence(ref _gatingSequences, sequence);
#pragma warning restore CS0420
    }

    public ISequenceBarrier NewBarrier(params Sequence[] sequencesToTrack)
    {
        return new SequenceBarrier(this, _waitStrategy, _cursor, sequencesToTrack);
    }

    public long GetMinimumSequence()
    {
        return GetMinimumGatingSequence();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long GetMinimumGatingSequence()
    {
        return SequenceGroups.GetMinimumSequence(_gatingSequences, _cursor.Value);
    }
}