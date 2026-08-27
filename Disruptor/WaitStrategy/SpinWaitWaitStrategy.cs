namespace Disruptor.WaitStrategy;

/// <summary>
/// .NET의 <see cref="SpinWait"/>에 위임하는 전략.
/// 스핀 → Yield → Sleep(0) 순으로 자동 에스컬레이션합니다.
/// </summary>
/// <remarks>
/// <see cref="SpinWait.SpinOnce(int)"/>에 <c>sleep1Threshold: -1</c>을 넘겨
/// <c>Thread.Sleep(1)</c> 단계를 비활성화합니다. 이것을 하지 않으면 Windows의
/// 기본 타이머 분해능(15.6ms) 때문에 대기 한 번이 15ms 이상 걸립니다
/// (60Hz 게임 서버의 틱 예산 16.6ms를 통째로 소모).
/// </remarks>
public sealed class SpinWaitWaitStrategy : IWaitStrategy
{
    public bool IsBlockingStrategy => false;

    public SequenceWaitResult WaitFor(long sequence, DependentSequenceGroup dependents,
                                      CancellationToken cancellationToken)
    {
        long available;
        var spinWait = new SpinWait();

        while ((available = dependents.Value) < sequence)
        {
            if (cancellationToken.IsCancellationRequested)
                return SequenceWaitResult.Cancelled;

            spinWait.SpinOnce(sleep1Threshold: -1);
        }

        return available;
    }

    public void SignalAllWhenBlocking()
    {
        // SpinWait 기반이므로 별도의 시그널이 필요 없음
    }
}
