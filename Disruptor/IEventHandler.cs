namespace Disruptor;

/// <summary>
/// 이벤트를 처리하는 핸들러.
/// </summary>
/// <remarks>
/// <b>핸들러 안에서 절대 <c>await</c>를 사용하지 마세요.</b> Disruptor 컨슈머는 동기 블로킹
/// 스레드이고, 핸들러 안에서 await하면 이벤트 순서 보장이 깨집니다.
/// 느린 I/O는 <see cref="Work.IWorkHandler{T}"/> 기반 워커 풀로 분리하세요.
/// (DEBUG 빌드에서는 <see cref="BatchEventProcessor{T}"/>가 이를 자동 검사합니다.)
/// </remarks>
public interface IEventHandler<in T>
{
    /// <summary>
    /// 이벤트가 사용 가능해지면 호출됩니다.
    /// </summary>
    /// <param name="data">이벤트 데이터</param>
    /// <param name="sequence">이벤트의 시퀀스 번호</param>
    /// <param name="endOfBatch">현재 배치의 마지막인지 여부</param>
    void OnEvent(T data, long sequence, bool endOfBatch);

    /// <summary>
    /// 배치 처리 시작 시 1회 호출됩니다. batchSize만큼 이벤트가 연속으로 도착했음을 알립니다.
    /// </summary>
    /// <remarks>
    /// 버퍼를 미리 확보하거나 트랜잭션을 시작하는 지점입니다.
    /// <c>endOfBatch == true</c>일 때 flush하면 1,000건 처리 시 syscall이 1,000회에서 1회로 줄어듭니다.
    /// </remarks>
    void OnBatchStart(long batchSize) { }

    /// <summary>
    /// 대기 타임아웃 시 호출됩니다. 틱 진행 / 송신 플러시 / 하트비트 지점입니다.
    /// <see cref="WaitStrategy.TimeoutBlockingWaitStrategy"/> 계열을 사용할 때만 호출됩니다.
    /// </summary>
    /// <param name="lastProcessedSequence">마지막으로 처리한 시퀀스</param>
    void OnTimeout(long lastProcessedSequence) { }

    /// <summary>핸들러 시작 시 호출됩니다.</summary>
    void OnStart() { }

    /// <summary>핸들러 종료 시 호출됩니다.</summary>
    void OnShutdown() { }
}
