namespace Disruptor.WaitStrategy;

/// <summary>
/// 지정한 시간 동안 이벤트가 없으면 <see cref="SequenceWaitResult.Timeout"/>을 반환하는 블로킹 전략.
/// </summary>
/// <remarks>
/// 게임 서버에서 가장 실용적인 기본 전략입니다. 타임아웃이 발생하면
/// <see cref="IEventHandler{T}.OnTimeout"/>이 호출되므로,
/// 이벤트 유무와 무관하게 다음 작업을 수행할 수 있습니다.
/// <list type="bullet">
///   <item>송신 버퍼 플러시 (Nagle 대신 직접 배칭)</item>
///   <item>고정 틱 시뮬레이션 진행</item>
///   <item>세션 타임아웃 검사 / 하트비트</item>
///   <item>배치로 모아둔 DB 쓰기 커밋</item>
/// </list>
/// 이것이 없으면 별도 타이머 스레드를 만들고 다시 락으로 동기화해야 하므로
/// Disruptor를 쓰는 의미가 사라집니다.
/// </remarks>
public sealed class TimeoutBlockingWaitStrategy : IWaitStrategy
{
    private readonly object _gate = new();
    private readonly int _timeoutMs;

    public TimeoutBlockingWaitStrategy(TimeSpan timeout)
    {
        double ms = timeout.TotalMilliseconds;
        if (ms < 1 || ms > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout), "timeout must be between 1ms and int.MaxValue ms");

        _timeoutMs = (int)ms;
    }

    public TimeSpan Timeout => TimeSpan.FromMilliseconds(_timeoutMs);

    public bool IsBlockingStrategy => true;

    public SequenceWaitResult WaitFor(long sequence, DependentSequenceGroup dependents,
                                      CancellationToken cancellationToken)
    {
        long available = dependents.Value;
        if (available >= sequence)
            return available;

        lock (_gate)
        {
            while ((available = dependents.Value) < sequence)
            {
                if (cancellationToken.IsCancellationRequested)
                    return SequenceWaitResult.Cancelled;

                if (!Monitor.Wait(_gate, _timeoutMs))
                    return SequenceWaitResult.Timeout;
            }
        }

        return available;
    }

    public void SignalAllWhenBlocking()
    {
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }
    }
}
