namespace Disruptor.WaitStrategy;

/// <summary>
/// 최고 성능 전략. 물리 코어 수보다 EventHandler 스레드가 적을 때만 사용하세요.
/// CPU를 100% 점유합니다.
/// </summary>
public sealed class BusySpinWaitStrategy : IWaitStrategy
{
    public long WaitFor(long sequence, Sequence cursor, Sequence dependentSequence,
                        ISequenceBarrier barrier)
    {
        long availableSequence;
        while ((availableSequence = dependentSequence.Value) < sequence)
        {
            barrier.CheckAlert();
            Thread.SpinWait(1);
        }

        return availableSequence;
    }

    public void SignalAllWhenBlocking()
    {
    }
}