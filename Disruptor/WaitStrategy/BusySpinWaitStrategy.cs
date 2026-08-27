namespace Disruptor.WaitStrategy;

/// <summary>
/// 최고 성능 전략. 물리 코어 수보다 EventHandler 스레드가 적을 때만 사용하세요.
/// CPU를 100% 점유합니다.
/// </summary>
/// <remarks>
/// <b>컨테이너 경고</b>: Kubernetes 등에서 CPU 제한(cgroup quota)을 건 상태로 이 전략을 쓰면
/// 핸들러 하나가 쿼터를 모두 소모해 CFS 스로틀링이 발생하고, 프로세스 전체가 100ms 단위로 정지합니다.
/// 컨테이너 환경에서는 <see cref="TimeoutBlockingWaitStrategy"/>를 사용하세요.
/// </remarks>
public sealed class BusySpinWaitStrategy : IWaitStrategy
{
    public bool IsBlockingStrategy => false;

    public SequenceWaitResult WaitFor(long sequence, DependentSequenceGroup dependents,
                                      CancellationToken cancellationToken)
    {
        long available;
        while ((available = dependents.Value) < sequence)
        {
            if (cancellationToken.IsCancellationRequested)
                return SequenceWaitResult.Cancelled;

            Thread.SpinWait(1); // pause 명령 -> 하이퍼스레드 형제 코어 배려
        }

        return available;
    }

    public void SignalAllWhenBlocking()
    {
    }
}
