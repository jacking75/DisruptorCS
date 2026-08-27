namespace Disruptor.WaitStrategy;

/// <summary>
/// 스핀 → Yield → Sleep 순으로 단계적으로 물러나는 전략.
/// CPU 사용량과 지연 시간의 절충안이며, 지연에 크게 민감하지 않은 배경 작업에 적합합니다.
/// </summary>
public sealed class SleepingWaitStrategy : IWaitStrategy
{
    private const int DefaultRetries = 200;

    private readonly int _retries;
    private readonly TimeSpan _sleepDuration;

    public SleepingWaitStrategy(int retries = DefaultRetries, TimeSpan? sleepDuration = null)
    {
        if (retries < 1)
            throw new ArgumentOutOfRangeException(nameof(retries));

        _retries = retries;
        _sleepDuration = sleepDuration ?? TimeSpan.FromMilliseconds(1);
    }

    public bool IsBlockingStrategy => false;

    public SequenceWaitResult WaitFor(long sequence, DependentSequenceGroup dependents,
                                      CancellationToken cancellationToken)
    {
        long available;
        int counter = _retries;

        while ((available = dependents.Value) < sequence)
        {
            if (cancellationToken.IsCancellationRequested)
                return SequenceWaitResult.Cancelled;

            counter = ApplyWaitMethod(counter);
        }

        return available;
    }

    private int ApplyWaitMethod(int counter)
    {
        if (counter > 100)
        {
            counter--;
            Thread.SpinWait(1);
        }
        else if (counter > 0)
        {
            counter--;
            Thread.Yield();
        }
        else
        {
            Thread.Sleep(_sleepDuration);
        }

        return counter;
    }

    public void SignalAllWhenBlocking()
    {
    }
}
