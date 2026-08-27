using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Disruptor.WaitStrategy;

namespace Disruptor;

/// <summary>
/// 다중 프로듀서를 지원하는 시퀀서.
/// CAS 연산을 사용하여 thread-safe합니다.
/// </summary>
public sealed class MultiProducerSequencer : SequencerBase
{
    private readonly Sequence _gatingSequenceCache = new();
    private readonly int[] _availableBuffer;
    private readonly int _indexMask;
    private readonly int _indexShift;

    public MultiProducerSequencer(int bufferSize, IWaitStrategy waitStrategy, SequencerOptions? options = null)
        : base(bufferSize, waitStrategy, options)
    {
        _indexMask = bufferSize - 1;
        _indexShift = BitOperations.Log2((uint)bufferSize);

        _availableBuffer = new int[bufferSize];
        Array.Fill(_availableBuffer, -1); // 초기에는 모든 슬롯이 사용 불가
    }

    public override long Next(int n = 1) => Next(n, CancellationToken.None);

    /// <remarks>
    /// <b>이식 주의</b>: 이 루프를 <c>do…while</c>로 작성하면 안 됩니다.
    /// C#에서 <c>do…while</c> 안의 <c>continue</c>는 루프 본문의 처음이 아니라 <b>조건식으로 점프</b>하므로,
    /// "용량 없음" 판정 직후에도 CAS가 실행되어 백프레셔가 완전히 무력화됩니다
    /// (미소비 슬롯 덮어쓰기 → availableBuffer 라운드 플래그 갱신 → 컨슈머 영구 정지).
    /// </remarks>
    public override long Next(int n, CancellationToken cancellationToken)
    {
        ValidateClaimCount(n);

        var backoff = Options.Backoff;
        ProducerBackoffState state = default;
        long start = 0;
        bool stallReported = false;

        while (true)
        {
            long current = CursorSequence.Value;
            long next = current + n;
            long wrapPoint = next - BufferSizeValue;
            long cachedGatingSequence = _gatingSequenceCache.Value;

            if (wrapPoint > cachedGatingSequence || cachedGatingSequence > current)
            {
                long gatingSequence = SequenceGroups.GetMinimumSequence(GatingSequences, current, out var slowest);

                if (wrapPoint > gatingSequence)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (start == 0)
                        start = Stopwatch.GetTimestamp();

                    long elapsed = Stopwatch.GetTimestamp() - start;
                    ReportStallIfNeeded(elapsed, ref stallReported, gatingSequence, slowest);

                    if (!backoff.Wait(ref state, elapsed))
                        throw new TimeoutException(
                            $"Disruptor '{Options.PipelineName}': 백프레셔 대기 시간이 초과되었습니다 " +
                            $"(가장 느린 시퀀스 {gatingSequence}, 커서 {current}).");

                    continue; // CAS 를 건너뛰고 용량을 재확인한다
                }

                _gatingSequenceCache.SetValue(gatingSequence);
            }

            if (CursorSequence.CompareAndSet(current, next))
                return next;

            // CAS 실패 = 다른 프로듀서가 선점 -> 처음부터 재시도
        }
    }

    public override bool TryNext(out long sequence, int n = 1)
    {
        ValidateClaimCount(n);

        while (true)
        {
            long current = CursorSequence.Value;
            long next = current + n;

            if (!HasAvailableCapacity(current, next))
            {
                sequence = 0;
                return false;
            }

            if (CursorSequence.CompareAndSet(current, next))
            {
                sequence = next;
                return true;
            }
        }
    }

    public override bool TryNext(out long sequence, int n, TimeSpan timeout)
    {
        ValidateClaimCount(n);

        if (TryNext(out sequence, n))
            return true;

        long limitTicks = (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        long start = Stopwatch.GetTimestamp();
        ProducerBackoffState state = default;

        while (Stopwatch.GetTimestamp() - start < limitTicks)
        {
            state.SpinOnceWithoutSleep();
            if (TryNext(out sequence, n))
                return true;
        }

        sequence = 0;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HasAvailableCapacity(long current, long next)
    {
        long wrapPoint = next - BufferSizeValue;
        long cachedGatingSequence = _gatingSequenceCache.Value;

        if (wrapPoint > cachedGatingSequence || cachedGatingSequence > current)
        {
            long gatingSequence = SequenceGroups.GetMinimumSequence(GatingSequences, current);

            if (wrapPoint > gatingSequence)
                return false;

            _gatingSequenceCache.SetValue(gatingSequence);
        }

        return true;
    }

    public override bool HasAvailableCapacity(int n)
    {
        if (n < 1 || n > BufferSizeValue)
            return false;

        long current = CursorSequence.Value;
        return HasAvailableCapacity(current, current + n);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Publish(long sequence)
    {
        SetAvailable(sequence);
        SignalIfBlocking();
    }

    public override void Publish(long lo, long hi)
    {
        for (long l = lo; l <= hi; l++)
            SetAvailable(l);

        SignalIfBlocking();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetAvailable(long sequence)
    {
        int index = CalculateIndex(sequence);
        int flag = CalculateAvailabilityFlag(sequence);
        Volatile.Write(ref _availableBuffer[index], flag);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool IsAvailable(long sequence)
    {
        int index = CalculateIndex(sequence);
        int flag = CalculateAvailabilityFlag(sequence);
        return Volatile.Read(ref _availableBuffer[index]) == flag;
    }

    public override long GetHighestPublishedSequence(long lowerBound, long availableSequence)
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

    public override long GetMinimumSequence()
        => SequenceGroups.GetMinimumSequence(GatingSequences, CursorSequence.Value);

    public override long GetRemainingCapacity()
    {
        long produced = CursorSequence.Value;
        long consumed = SequenceGroups.GetMinimumSequence(GatingSequences, produced);
        return BufferSizeValue - (produced - consumed);
    }
}
