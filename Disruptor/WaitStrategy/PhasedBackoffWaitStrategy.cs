using System.Diagnostics;

namespace Disruptor.WaitStrategy;

/// <summary>
/// 스핀 → Yield → 폴백 전략 순으로 시간 기반 단계 전환을 하는 전략.
/// </summary>
/// <remarks>
/// 틱 정밀도가 필요하면서도 유휴 시 CPU를 놓아야 하는 게임 서버 루프에 적합합니다.
/// 스핀 예산을 먼저 소모하므로 짧은 대기는 나노초 단위로 끝나고,
/// 대기가 길어지면 블로킹 전략에 위임해 CPU를 반환합니다.
/// </remarks>
public sealed class PhasedBackoffWaitStrategy : IWaitStrategy
{
    private readonly long _spinTicks;
    private readonly long _yieldTicks;
    private readonly IWaitStrategy _fallback;

    public PhasedBackoffWaitStrategy(TimeSpan spinDuration, TimeSpan yieldDuration, IWaitStrategy fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);

        _spinTicks = ToStopwatchTicks(spinDuration);
        _yieldTicks = _spinTicks + ToStopwatchTicks(yieldDuration);
        _fallback = fallback;
    }

    /// <summary>스핀 1ms → Yield 1ms → 블로킹 대기로 전환하는 표준 구성.</summary>
    public static PhasedBackoffWaitStrategy WithBlocking(TimeSpan spinDuration, TimeSpan yieldDuration)
        => new(spinDuration, yieldDuration, new BlockingWaitStrategy());

    /// <summary>스핀 → Yield → 타임아웃 블로킹으로 전환하는 구성. OnTimeout 훅이 필요할 때 사용합니다.</summary>
    public static PhasedBackoffWaitStrategy WithTimeout(TimeSpan spinDuration, TimeSpan yieldDuration, TimeSpan timeout)
        => new(spinDuration, yieldDuration, new TimeoutBlockingWaitStrategy(timeout));

    private static long ToStopwatchTicks(TimeSpan duration)
        => (long)(duration.TotalSeconds * Stopwatch.Frequency);

    public bool IsBlockingStrategy => _fallback.IsBlockingStrategy;

    public SequenceWaitResult WaitFor(long sequence, DependentSequenceGroup dependents,
                                      CancellationToken cancellationToken)
    {
        long start = 0;

        while (true)
        {
            long available = dependents.Value;
            if (available >= sequence)
                return available;

            if (cancellationToken.IsCancellationRequested)
                return SequenceWaitResult.Cancelled;

            if (start == 0)
            {
                start = Stopwatch.GetTimestamp();
                Thread.SpinWait(1);
                continue;
            }

            long elapsed = Stopwatch.GetTimestamp() - start;

            if (elapsed < _spinTicks)
                Thread.SpinWait(64);
            else if (elapsed < _yieldTicks)
                Thread.Yield();
            else
                return _fallback.WaitFor(sequence, dependents, cancellationToken);
        }
    }

    public void SignalAllWhenBlocking() => _fallback.SignalAllWhenBlocking();
}
