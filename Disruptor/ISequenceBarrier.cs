using Disruptor.WaitStrategy;

namespace Disruptor;

/// <summary>
/// 시퀀스 배리어 인터페이스.
/// 컨슈머가 프로듀서나 다른 컨슈머를 기다리는 데 사용됩니다.
/// </summary>
public interface ISequenceBarrier
{
    /// <summary>
    /// 주어진 시퀀스가 소비 가능해질 때까지 대기합니다.
    /// </summary>
    /// <remarks>
    /// 종료/타임아웃은 예외가 아니라 <see cref="SequenceWaitResult"/>로 표현됩니다.
    /// </remarks>
    SequenceWaitResult WaitFor(long sequence, CancellationToken cancellationToken);

    /// <summary>현재 커서 위치를 반환합니다.</summary>
    long Cursor { get; }

    /// <summary>이 배리어가 추적하는 의존 대상.</summary>
    DependentSequenceGroup Dependents { get; }

    /// <summary>이 배리어가 사용하는 대기 전략.</summary>
    IWaitStrategy WaitStrategy { get; }
}
