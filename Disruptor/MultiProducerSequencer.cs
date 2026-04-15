using Disruptor.WaitStrategy;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace Disruptor;

/// <summary>
/// 다중 프로듀서를 지원하는 시퀀서.
/// CAS 연산을 사용하여 thread-safe합니다.
/// </summary>
public sealed class MultiProducerSequencer : ISequencer
{
    private readonly int _bufferSize;
    private readonly IWaitStrategy _waitStrategy;
    private readonly Sequence _cursor = new();
    private readonly Sequence _gatingSequenceCache = new();
    private readonly int[] _availableBuffer;
    private readonly int _indexMask;
    private readonly int _indexShift;

    private volatile Sequence[] _gatingSequences = Array.Empty<Sequence>();

    public MultiProducerSequencer(int bufferSize, IWaitStrategy waitStrategy)
    {
        if (bufferSize < 1)
            throw new ArgumentException("bufferSize must be positive");
        if ((bufferSize & (bufferSize - 1)) != 0)
            throw new ArgumentException("bufferSize must be a power of 2");

        _bufferSize = bufferSize;
        _waitStrategy = waitStrategy;
        _indexMask = bufferSize - 1;
        _indexShift = Log2(bufferSize);

        _availableBuffer = new int[bufferSize];
        for (int i = 0; i < bufferSize; i++)
        {
            _availableBuffer[i] = -1; // 초기에는 모든 슬롯이 사용 불가
        }
    }

    private static int Log2(int i)
    {
        int r = 0;
        while ((1 << r) < i) r++;
        return r;
    }

    public int BufferSize => _bufferSize;
    public long Cursor => _cursor.Value;

    public long Next(int n = 1)
    {
        if (n < 1)
            throw new ArgumentException("n must be positive");

        long current;
        long next;
        var spinWait = new SpinWait();

        do
        {
            current = _cursor.Value;
            next = current + n;
            long wrapPoint = next - _bufferSize;
            long cachedGatingSequence = _gatingSequenceCache.Value;

            if (wrapPoint > cachedGatingSequence || cachedGatingSequence > current)
            {
                long gatingSequence = SequenceGroups.GetMinimumSequence(
                    _gatingSequences, current);

                if (wrapPoint > gatingSequence)
                {
                    spinWait.SpinOnce();
                    continue;
                }

                _gatingSequenceCache.SetValue(gatingSequence);
            }
        }
        while (!_cursor.CompareAndSet(current, next));

        return next;
    }

    public bool TryNext(out long sequence, int n = 1)
    {
        if (n < 1)
            throw new ArgumentException("n must be positive");

        long current;
        long next;

        do
        {
            current = _cursor.Value;
            next = current + n;

            if (!HasAvailableCapacity(current, next))
            {
                sequence = 0;
                return false;
            }
        }
        while (!_cursor.CompareAndSet(current, next));

        sequence = next;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HasAvailableCapacity(long current, long next)
    {
        long wrapPoint = next - _bufferSize;
        long cachedGatingSequence = _gatingSequenceCache.Value;

        if (wrapPoint > cachedGatingSequence || cachedGatingSequence > current)
        {
            long gatingSequence = SequenceGroups.GetMinimumSequence(
                _gatingSequences, current);

            if (wrapPoint > gatingSequence)
                return false;

            _gatingSequenceCache.SetValue(gatingSequence);
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Publish(long sequence)
    {
        SetAvailable(sequence);
        _waitStrategy.SignalAllWhenBlocking();
    }

    public void Publish(long lo, long hi)
    {
        for (long l = lo; l <= hi; l++)
        {
            SetAvailable(l);
        }
        _waitStrategy.SignalAllWhenBlocking();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetAvailable(long sequence)
    {
        int index = CalculateIndex(sequence);
        int flag = CalculateAvailabilityFlag(sequence);
        Volatile.Write(ref _availableBuffer[index], flag);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsAvailable(long sequence)
    {
        int index = CalculateIndex(sequence);
        int flag = CalculateAvailabilityFlag(sequence);
        return Volatile.Read(ref _availableBuffer[index]) == flag;
    }

    public long GetHighestPublishedSequence(long lowerBound, long availableSequence)
    {
        for (long sequence = lowerBound; sequence <= availableSequence; sequence++)
        {
            if (!IsAvailable(sequence))
                return sequence - 1;
        }
        return availableSequence;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CalculateIndex(long sequence) => (int)sequence & _indexMask;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CalculateAvailabilityFlag(long sequence) => (int)((ulong)sequence >> _indexShift);

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
        return SequenceGroups.GetMinimumSequence(_gatingSequences, _cursor.Value);
    }
}