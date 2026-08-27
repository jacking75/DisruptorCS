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

    /// <summary>
    /// 백프레셔 대기 루프에서 시간을 샘플링해야 하는가.
    /// </summary>
    /// <remarks>
    /// 기본 백오프(<see cref="SpinOnlyBackoff"/>)는 경과 시간을 쓰지 않고, 스톨 콜백도 없을 수 있다.
    /// 그런 구성에서 반복마다 <c>Stopwatch.GetTimestamp()</c>(QPC, 시리얼라이징 성격)를 호출하면
    /// 몇 번의 스핀으로 풀리는 짧은 백프레셔의 해소 지연에 그 비용이 그대로 얹힌다.
    /// </remarks>
    protected readonly bool TimeSensitive;

    /// <summary>
    /// 기본 백오프(<see cref="SpinOnlyBackoff"/>)인가. 맞으면 백프레셔 루프에서 인터페이스 호출을 생략한다.
    /// </summary>
    /// <remarks>
    /// 백프레셔 대기는 작은 링에서 이벤트마다 발생할 수 있는 경로라, 반복당 인터페이스 디스패치 하나도
    /// 무시할 수 없습니다.
    /// </remarks>
    protected readonly bool UsesSpinOnlyBackoff;

    /// <summary>시간 샘플링 주기 (스핀 횟수). 이 값의 배수마다 한 번만 QPC 를 읽는다.</summary>
    protected const int TimeSampleMask = 127;

    private Sequence[] _gatingSequences = [];
    private readonly ConcurrentDictionary<Sequence, string> _labels = new();
    private readonly long _stallThresholdTicks;
    private readonly long _stallReportIntervalTicks;

    /// <summary>마지막 스톨 보고 시각. 로그 폭주를 막기 위한 시퀀서 수준의 rate-limit.</summary>
    private long _lastStallReportTicks;

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
        TimeSensitive = Options.Backoff.NeedsTime || Options.OnStall is not null;
        UsesSpinOnlyBackoff = Options.Backoff is SpinOnlyBackoff;
        _stallThresholdTicks = (long)(Options.StallThreshold.TotalSeconds * Stopwatch.Frequency);
        _stallReportIntervalTicks = (long)(Options.StallReportInterval.TotalSeconds * Stopwatch.Frequency);
    }

    public int BufferSize => BufferSizeValue;

    public long Cursor => CursorSequence.Value;

    public IWaitStrategy WaitStrategy => Strategy;

    Sequence ISequencer.CursorSequence => CursorSequence;


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

    /// <summary>
    /// 스톨 임계치를 넘겼는지 확인하고, 넘겼으면 콜백을 호출합니다.
    /// </summary>
    /// <remarks>
    /// <paramref name="reported"/>는 <b>이번 <c>Next()</c> 호출</b>에서 중복 보고를 막고,
    /// <see cref="SequencerOptions.StallReportInterval"/>은 <b>시퀀서 전체</b>에서 보고 빈도를 제한합니다.
    /// 후자가 없으면 모든 게시가 막히는 지속 정체에서 게시 건수만큼 콜백이 발동해
    /// 로그 폭주가 정체를 더 악화시킵니다.
    /// </remarks>
    protected void ReportStallIfNeeded(long elapsedTicks, ref bool reported, long minSequence, Sequence? slowest)
    {
        if (reported || Options.OnStall is null || elapsedTicks <= _stallThresholdTicks)
            return;

        reported = true;

        // 시퀀서 전체 rate-limit: 승자 1명만 보고한다.
        long now = Stopwatch.GetTimestamp();
        long last = Volatile.Read(ref _lastStallReportTicks);

        if (last != 0 && now - last < _stallReportIntervalTicks)
            return;

        if (Interlocked.CompareExchange(ref _lastStallReportTicks, now, last) != last)
            return; // 다른 스레드가 이미 보고했다

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
