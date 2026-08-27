using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Disruptor.WaitStrategy;

namespace Disruptor;

/// <summary>
/// 단일/다중 프로듀서 시퀀서가 공유하는 기반 구현
/// (게이팅 시퀀스 관리, 배리어 생성, 스톨 감지).
/// </summary>
public abstract class SequencerBase : ISequencer
{
    protected readonly int BufferSizeValue;
    protected readonly IWaitStrategy Strategy;
    protected readonly SequencerOptions Options;
    protected readonly Sequence CursorSequence = new();

    /// <summary>블로킹 대기 전략일 때만 게시 경로에서 신호를 보낸다 (가상 호출 회피용 캐시).</summary>
    protected readonly bool SignalNeeded;

    private Sequence[] _gatingSequences = [];
    private readonly ConcurrentDictionary<Sequence, string> _labels = new();
    private readonly long _stallThresholdTicks;

    protected SequencerBase(int bufferSize, IWaitStrategy waitStrategy, SequencerOptions? options)
    {
        ArgumentNullException.ThrowIfNull(waitStrategy);

        if (bufferSize < 1)
            throw new ArgumentException("bufferSize must be positive", nameof(bufferSize));
        if ((bufferSize & (bufferSize - 1)) != 0)
            throw new ArgumentException("bufferSize must be a power of 2", nameof(bufferSize));

        BufferSizeValue = bufferSize;
        Strategy = waitStrategy;
        Options = options ?? SequencerOptions.Default;
        SignalNeeded = waitStrategy.IsBlockingStrategy;
        _stallThresholdTicks = (long)(Options.StallThreshold.TotalSeconds * Stopwatch.Frequency);
    }

    public int BufferSize => BufferSizeValue;

    public long Cursor => CursorSequence.Value;

    public IWaitStrategy WaitStrategy => Strategy;

    protected Sequence[] GatingSequences
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _gatingSequences);
    }

    public Sequence[] GetGatingSequences() => (Sequence[])GatingSequences.Clone();

    public void AddGatingSequences(params Sequence[] gatingSequences)
    {
        ArgumentNullException.ThrowIfNull(gatingSequences);
        if (gatingSequences.Length == 0)
            return;

        SequenceGroups.AddSequences(ref _gatingSequences, gatingSequences);
    }

    public bool RemoveGatingSequence(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        _labels.TryRemove(sequence, out _);
        return SequenceGroups.RemoveSequence(ref _gatingSequences, sequence);
    }

    public void SetGatingSequenceLabel(Sequence sequence, string label)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        _labels[sequence] = label;
    }

    public ISequenceBarrier NewBarrier(params Sequence[] sequencesToTrack)
        => new SequenceBarrier(this, Strategy, CursorSequence, sequencesToTrack);

    public abstract long Next(int n = 1);
    public abstract long Next(int n, CancellationToken cancellationToken);
    public abstract bool TryNext(out long sequence, int n = 1);
    public abstract bool TryNext(out long sequence, int n, TimeSpan timeout);
    public abstract void Publish(long sequence);
    public abstract void Publish(long lo, long hi);
    public abstract bool IsAvailable(long sequence);
    public abstract long GetHighestPublishedSequence(long lowerBound, long availableSequence);
    public abstract long GetMinimumSequence();
    public abstract long GetRemainingCapacity();
    public abstract bool HasAvailableCapacity(int n);

    /// <summary>n이 유효한지 검사합니다. n &gt; bufferSize이면 영원히 대기하게 되므로 즉시 실패시킵니다.</summary>
    protected void ValidateClaimCount(int n)
    {
        if (n < 1)
            throw new ArgumentOutOfRangeException(nameof(n), "n must be positive");
        if (n > BufferSizeValue)
            throw new ArgumentOutOfRangeException(nameof(n), $"n must not exceed bufferSize ({BufferSizeValue})");
    }

    /// <summary>스톨 임계치를 넘겼는지 확인하고, 넘겼으면 1회만 콜백을 호출합니다.</summary>
    protected void ReportStallIfNeeded(long elapsedTicks, ref bool reported, long minSequence, Sequence? slowest)
    {
        if (reported || Options.OnStall is null || elapsedTicks <= _stallThresholdTicks)
            return;

        reported = true;

        string handler = slowest is not null && _labels.TryGetValue(slowest, out string? label)
            ? label
            : "<unknown>";

        try
        {
            Options.OnStall(new StallInfo(
                PipelineName: Options.PipelineName,
                ElapsedMs: elapsedTicks * 1000.0 / Stopwatch.Frequency,
                Cursor: CursorSequence.Value,
                SlowestSequence: minSequence,
                SlowestHandler: handler));
        }
        catch
        {
            // 진단 콜백의 예외가 게시 경로를 죽이면 안 된다.
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void SignalIfBlocking()
    {
        if (SignalNeeded)
            Strategy.SignalAllWhenBlocking();
    }
}
