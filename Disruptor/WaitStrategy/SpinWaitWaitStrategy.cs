using System;
using System.Collections.Generic;
using System.Text;

namespace Disruptor.WaitStrategy;

/// <summary>
/// SpinWait을 사용하는 전략. SleepingWaitStrategy에 해당합니다.
/// </summary>
public sealed class SpinWaitWaitStrategy : IWaitStrategy
{
    public long WaitFor(long sequence, Sequence cursor, Sequence dependentSequence,
                        ISequenceBarrier barrier)
    {
        long availableSequence;
        var spinWait = new SpinWait();

        while ((availableSequence = dependentSequence.Value) < sequence)
        {
            barrier.CheckAlert();
            spinWait.SpinOnce();
        }

        return availableSequence;
    }

    public void SignalAllWhenBlocking()
    {
        // SpinWait 기반이므로 별도의 시그널이 필요 없음
    }
}