namespace Disruptor.WaitStrategy;

/// <summary>
/// 가장 보수적인 전략. lock + Monitor를 사용합니다.
/// CPU 사용량이 낮지만 레이턴시가 가장 높습니다.
/// </summary>
public sealed class BlockingWaitStrategy : IWaitStrategy
{
    private readonly object _gate = new();
    private int _numWaiters;

    public long WaitFor(long sequence, Sequence cursor, Sequence dependentSequence,
                        ISequenceBarrier barrier)
    {
        if (cursor.Value < sequence)
        {
            lock (_gate)
            {
                Interlocked.Increment(ref _numWaiters);
                try
                {
                    while (cursor.Value < sequence)
                    {
                        barrier.CheckAlert();
                        Monitor.Wait(_gate);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _numWaiters);
                }
            }
        }

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
        if (Volatile.Read(ref _numWaiters) > 0)
        {
            lock (_gate)
            {
                Monitor.PulseAll(_gate);
            }
        }
    }
}
