using System.Diagnostics;
using System.Runtime.CompilerServices;
using Disruptor.WaitStrategy;

namespace Disruptor;

/// <summary>
/// 단일 프로듀서 최적화 시퀀서.
/// CAS 연산 없이 동작하여 최고 성능을 제공합니다.
/// </summary>
/// <remarks>
/// <b>반드시 하나의 스레드만</b> <see cref="Next(int)"/>/<see cref="Publish(long)"/>를 호출해야 합니다.
/// DEBUG 빌드에서는 여러 스레드가 게시하면 즉시 예외를 던져 오용을 잡아냅니다
/// (Release에서는 조용히 데이터가 손상되므로 매우 찾기 어려운 버그가 됩니다).
/// </remarks>
public sealed class SingleProducerSequencer : SequencerBase
{
    // 단일 프로듀서 최적화: 캐시된 값 (프로듀서 스레드만 접근)
    private long _nextValue = Sequence.InitialCursorValue;
    private long _cachedGatingValue = Sequence.InitialCursorValue;

    private int _producerThreadId;

    public SingleProducerSequencer(int bufferSize, IWaitStrategy waitStrategy, SequencerOptions? options = null)
        : base(bufferSize, waitStrategy, options)
    {
    }

    public override long Next(int n = 1) => Next(n, CancellationToken.None);

    public override long Next(int n, CancellationToken cancellationToken)
    {
        ValidateClaimCount(n);
        AssertSingleProducerThread();

        long nextValue = _nextValue;
        long nextSequence = nextValue + n;
        long wrapPoint = nextSequence - BufferSizeValue;
        long cachedGatingSequence = _cachedGatingValue;

        if (wrapPoint > cachedGatingSequence || cachedGatingSequence > nextValue)
        {
            long minSequence = WaitForCapacity(wrapPoint, nextValue, cancellationToken);
            _cachedGatingValue = minSequence;
        }

        _nextValue = nextSequence;
        return nextSequence;
    }

    private long WaitForCapacity(long wrapPoint, long nextValue, CancellationToken cancellationToken)
    {
        var backoff = Options.Backoff;
        ProducerBackoffState state = default;
        long start = Stopwatch.GetTimestamp();
        bool stallReported = false;

        long minSequence;
        Sequence? slowest;

        while (wrapPoint > (minSequence = SequenceGroups.GetMinimumSequence(GatingSequences, nextValue, out slowest)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            long elapsed = Stopwatch.GetTimestamp() - start;
            ReportStallIfNeeded(elapsed, ref stallReported, minSequence, slowest);

            if (!backoff.Wait(ref state, elapsed))
                throw new TimeoutException(
                    $"Disruptor '{Options.PipelineName}': 백프레셔 대기 시간이 초과되었습니다 " +
                    $"(가장 느린 시퀀스 {minSequence}, 커서 {Cursor}).");
        }

        return minSequence;
    }

    public override bool TryNext(out long sequence, int n = 1)
    {
        ValidateClaimCount(n);
        AssertSingleProducerThread();

        long nextValue = _nextValue;
        long nextSequence = nextValue + n;
        long wrapPoint = nextSequence - BufferSizeValue;
        long cachedGatingSequence = _cachedGatingValue;

        if (wrapPoint > cachedGatingSequence || cachedGatingSequence > nextValue)
        {
            long minSequence = SequenceGroups.GetMinimumSequence(GatingSequences, nextValue);
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
    public override void Publish(long sequence)
    {
        CursorSequence.SetValue(sequence);
        SignalIfBlocking();
    }

    public override void Publish(long lo, long hi) => Publish(hi);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool IsAvailable(long sequence) => sequence <= CursorSequence.Value;

    public override long GetHighestPublishedSequence(long lowerBound, long availableSequence)
        => availableSequence;

    public override long GetMinimumSequence()
        => SequenceGroups.GetMinimumSequence(GatingSequences, _nextValue);

    public override long GetRemainingCapacity()
    {
        long consumed = SequenceGroups.GetMinimumSequence(GatingSequences, _nextValue);
        long produced = _nextValue;
        return BufferSizeValue - (produced - consumed);
    }

    public override bool HasAvailableCapacity(int n)
    {
        if (n < 1 || n > BufferSizeValue)
            return false;

        long wrapPoint = _nextValue + n - BufferSizeValue;
        long cachedGatingSequence = _cachedGatingValue;

        if (wrapPoint > cachedGatingSequence || cachedGatingSequence > _nextValue)
        {
            long minSequence = SequenceGroups.GetMinimumSequence(GatingSequences, _nextValue);
            _cachedGatingValue = minSequence;
            return wrapPoint <= minSequence;
        }

        return true;
    }

    /// <summary>
    /// ProducerType.Single 오용 검사. Release 빌드에서는 완전히 제거됩니다.
    /// </summary>
    [Conditional("DEBUG")]
    private void AssertSingleProducerThread()
    {
        if (!Options.ValidateSingleProducerThread)
            return;

        int tid = Environment.CurrentManagedThreadId;
        int prev = Interlocked.CompareExchange(ref _producerThreadId, tid, 0);

        if (prev != 0 && prev != tid)
            throw new InvalidOperationException(
                $"ProducerType.Single 인데 여러 스레드가 게시했습니다 " +
                $"(최초 스레드 {prev}, 현재 {tid}). ProducerType.Multi 를 사용하세요.");
    }
}
