namespace Disruptor.WaitStrategy;

/// <summary>
/// 일정 횟수 스핀한 뒤 Thread.Yield()로 전환하는 저지연 전략.
/// EventHandler 스레드 수 &lt; 논리 코어 수일 때 권장됩니다.
/// </summary>
public sealed class YieldingWaitStrategy : IWaitStrategy
{
    private const int SpinTries = 100;

    public bool IsBlockingStrategy => false;

    public SequenceWaitResult WaitFor(long sequence, DependentSequenceGroup dependents,
                                      CancellationToken cancellationToken)
    {
        long available;
        int counter = SpinTries;

        while ((available = dependents.Value) < sequence)
        {
            if (cancellationToken.IsCancellationRequested)
                return SequenceWaitResult.Cancelled;

            if (counter == 0)
            {
                Thread.Yield();
            }
            else
            {
                counter--;
                // pause 힌트 없이 순수 루프를 돌면 하이퍼스레드 형제 코어의 성능을 빼앗고
                // 전력을 낭비한다. SpinWait(1)은 x86의 PAUSE 명령으로 컴파일된다.
                Thread.SpinWait(1);
            }
        }

        return available;
    }

    public void SignalAllWhenBlocking()
    {
    }
}
