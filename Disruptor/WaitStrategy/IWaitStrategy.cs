namespace Disruptor.WaitStrategy;

/// <summary>
/// 컨슈머가 새 이벤트를 기다리는 전략을 정의합니다.
/// </summary>
public interface IWaitStrategy
{
    /// <summary>
    /// 대기 시 블로킹(Monitor 등)을 사용하는가.
    /// Sequencer와 BatchEventProcessor가 "신호를 보낼 필요가 있는지"를 판단하는 데 사용합니다.
    /// </summary>
    /// <remarks>
    /// 스핀 계열 전략에서는 false이며, 이 경우 게시 경로에서 불필요한 lock 진입 비용을 내지 않습니다.
    /// </remarks>
    bool IsBlockingStrategy { get; }

    /// <summary>
    /// 주어진 시퀀스가 소비 가능해질 때까지 대기합니다.
    /// </summary>
    /// <param name="sequence">기다리는 시퀀스 번호</param>
    /// <param name="dependents">의존 대상(커서 또는 상위 핸들러 시퀀스들)</param>
    /// <param name="cancellationToken">종료 신호</param>
    SequenceWaitResult WaitFor(long sequence, DependentSequenceGroup dependents,
                               CancellationToken cancellationToken);

    /// <summary>
    /// 블로킹 대기 중인 스레드에게 시퀀스가 변경되었음을 알립니다.
    /// </summary>
    void SignalAllWhenBlocking();
}
