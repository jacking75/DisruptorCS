namespace Disruptor.WaitStrategy;

/// <summary>
/// 가장 보수적인 전략. lock + Monitor를 사용합니다.
/// CPU 사용량이 낮지만 wake-up 지연(실측 p50 약 12us)이 있습니다.
/// 핸들러 스레드 수가 논리 코어 수 이상이거나 컨테이너 환경일 때 권장됩니다.
/// </summary>
public sealed class BlockingWaitStrategy : IWaitStrategy
{
    /// <summary>
    /// lost-wakeup 자가 치유용 재확인 주기.
    /// </summary>
    /// <remarks>
    /// 게시 경로가 (A) 커서 volatile write → (B) 대기자 수 volatile read 순서인데,
    /// x86-64에서도 StoreLoad 재배치가 허용되므로 이론적으로 신호를 놓칠 수 있습니다.
    /// 무조건 PulseAll(아래 <see cref="SignalAllWhenBlocking"/>)로 1차 방어하고,
    /// 이 타임아웃으로 2차 방어합니다.
    /// </remarks>
    private const int RecheckIntervalMs = 100;

    private readonly object _gate = new();

    public bool IsBlockingStrategy => true;

    public SequenceWaitResult WaitFor(long sequence, DependentSequenceGroup dependents,
                                      CancellationToken cancellationToken)
    {
        long available = dependents.Value;
        if (available >= sequence)
            return available; // 빠른 경로: lock 진입 없음

        lock (_gate)
        {
            while ((available = dependents.Value) < sequence)
            {
                if (cancellationToken.IsCancellationRequested)
                    return SequenceWaitResult.Cancelled;

                // 의존 핸들러가 있는 경우에도 여기서 잠든다 -> hot-spin 제거
                Monitor.Wait(_gate, RecheckIntervalMs);
            }
        }

        return available;
    }

    public void SignalAllWhenBlocking()
    {
        // 대기자 수를 미리 확인하는 최적화는 StoreLoad 재배치로 인한
        // lost-wakeup 경쟁을 만들기 때문에 사용하지 않는다 (Java 원본과 동일한 동작).
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }
    }
}
