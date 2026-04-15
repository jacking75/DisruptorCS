using System;
using System.Collections.Generic;
using System.Text;

namespace Disruptor.WaitStrategy;

/// <summary>
/// Thread.Yield()를 사용하는 저지연 전략.
/// EventHandler 스레드 수 < 논리 코어 수일 때 권장됩니다.
/// </summary>
public sealed class YieldingWaitStrategy : IWaitStrategy
{
    private const int SpinTries = 100;

    public long WaitFor(long sequence, Sequence cursor, Sequence dependentSequence,
                        ISequenceBarrier barrier)
    {
        long availableSequence;
        int counter = SpinTries;

        while ((availableSequence = dependentSequence.Value) < sequence)
        {
            barrier.CheckAlert();
            if (counter == 0)
            {
                Thread.Yield();
            }
            else
            {
                counter--;
            }
        }

        return availableSequence;
    }

    public void SignalAllWhenBlocking()
    {
    }
}