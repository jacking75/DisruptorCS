using System;
using System.Collections.Generic;
using System.Text;

namespace Disruptor.WaitStrategy;

/// <summary>
/// 컨슈머가 새 이벤트를 기다리는 전략을 정의합니다.
/// </summary>
public interface IWaitStrategy
{
    /// <summary>
    /// 주어진 시퀀스가 사용 가능해질 때까지 대기합니다.
    /// </summary>
    /// <param name="sequence">기다리는 시퀀스 번호</param>
    /// <param name="cursor">링 버퍼의 현재 커서</param>
    /// <param name="dependentSequence">의존하는 시퀀스</param>
    /// <param name="barrier">시퀀스 배리어</param>
    /// <returns>사용 가능한 시퀀스 번호</returns>
    long WaitFor(long sequence, Sequence cursor, Sequence dependentSequence,
                 ISequenceBarrier barrier);

    /// <summary>
    /// 대기 중인 스레드에게 시퀀스가 변경되었음을 알립니다.
    /// </summary>
    void SignalAllWhenBlocking();
}