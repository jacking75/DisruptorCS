namespace Disruptor.WaitStrategy;

/// <summary>
/// 지정한 시간 동안 이벤트가 없으면 <see cref="SequenceWaitResult.Timeout"/>을 반환하는 블로킹 전략.
/// </summary>
/// <remarks>
/// <para>
/// 게임 서버에서 가장 실용적인 기본 전략입니다. 타임아웃이 발생하면
/// <see cref="IEventHandler{T}.OnTimeout"/>이 호출되므로,
/// 이벤트 유무와 무관하게 다음 작업을 수행할 수 있습니다.
/// </para>
/// <list type="bullet">
///   <item>송신 버퍼 플러시 (Nagle 대신 직접 배칭)</item>
///   <item>고정 틱 시뮬레이션 진행</item>
///   <item>세션 타임아웃 검사 / 하트비트</item>
///   <item>배치로 모아둔 DB 쓰기 커밋</item>
/// </list>
/// <para>
/// 이것이 없으면 별도 타이머 스레드를 만들고 다시 락으로 동기화해야 하므로
/// Disruptor를 쓰는 의미가 사라집니다.
/// </para>
/// <para>
/// <b>Windows 타이머 분해능 주의</b>: 완전 유휴 상태에서 <c>Monitor.Wait(gate, 1)</c>은
/// 시스템 타이머 인터럽트(기본 15.625ms)에 양자화됩니다. 실측 결과 1ms 를 요청해도
/// 실제 <c>OnTimeout</c> 간격은 <b>15.40ms</b> 였습니다. 60Hz 틱(16.6ms)이 필요하면
/// <see cref="Game.HighResolutionTimerScope"/>를 켜세요(실측 1.52ms 로 개선).
/// </para>
/// </remarks>
public sealed class TimeoutBlockingWaitStrategy : IWaitStrategy
{
    private readonly object _gate = new();
    private readonly int _timeoutMs;

    /// <summary>대기 중인 컨슈머 수. <see cref="_gate"/> 안에서만 증감합니다.</summary>
    private int _numWaiters;

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
            _numWaiters++;
            try
            {
                while ((available = dependents.Value) < sequence)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return SequenceWaitResult.Cancelled;

                    if (!Monitor.Wait(_gate, _timeoutMs))
                        return SequenceWaitResult.Timeout;
                }
            }
            finally
            {
                _numWaiters--;
            }
        }

        return available;
    }

    /// <inheritdoc cref="BlockingWaitStrategy.SignalAllWhenBlocking"/>
    public void SignalAllWhenBlocking()
    {
        // 메모리 순서 논증은 BlockingWaitStrategy.SignalAllWhenBlocking 참조.
        // 이 전략은 Wait 타임아웃이 있으므로 2차 방어가 내장되어 있다.
        Interlocked.MemoryBarrier();

        if (Volatile.Read(ref _numWaiters) > 0)
        {
            lock (_gate)
            {
                Monitor.PulseAll(_gate);
            }
        }
    }
}
