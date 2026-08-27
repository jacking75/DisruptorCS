using Disruptor.WaitStrategy;

namespace Disruptor;

public interface ISequencer
{
    int BufferSize { get; }

    /// <summary>
    /// 현재 커서.
    /// </summary>
    /// <remarks>
    /// <b>주의</b>: 다중 프로듀서에서 커서는 "게시된" 최대치가 아니라 "점유된" 최대치입니다.
    /// 실제로 연속 게시된 상한이 필요하면 <see cref="GetHighestPublishedSequence"/>를 사용하세요.
    /// </remarks>
    long Cursor { get; }

    /// <summary>이 시퀀서가 사용하는 대기 전략.</summary>
    IWaitStrategy WaitStrategy { get; }

    /// <summary>커서 시퀀스 객체. 배리어/폴러 구성에 사용합니다.</summary>
    Sequence CursorSequence { get; }

    /// <summary>다음 시퀀스를 할당받습니다. 용량이 없으면 대기합니다.</summary>
    long Next(int n = 1);

    /// <summary>다음 시퀀스를 할당받습니다. 취소 시 <see cref="OperationCanceledException"/>을 던집니다.</summary>
    long Next(int n, CancellationToken cancellationToken);

    /// <summary>시퀀스 할당을 시도합니다 (non-blocking).</summary>
    bool TryNext(out long sequence, int n = 1);

    /// <summary>타임아웃까지 시퀀스 할당을 시도합니다.</summary>
    bool TryNext(out long sequence, int n, TimeSpan timeout);

    /// <summary>시퀀스를 게시합니다.</summary>
    void Publish(long sequence);

    /// <summary>범위를 게시합니다.</summary>
    void Publish(long lo, long hi);

    /// <summary>해당 시퀀스가 사용 가능한지 확인합니다.</summary>
    bool IsAvailable(long sequence);

    /// <summary>lowerBound부터 연속으로 게시된 최고 시퀀스를 반환합니다.</summary>
    long GetHighestPublishedSequence(long lowerBound, long availableSequence);

    /// <summary>게이팅 시퀀스를 추가합니다 (컨슈머 추적).</summary>
    void AddGatingSequences(params Sequence[] gatingSequences);

    /// <summary>게이팅 시퀀스를 제거합니다.</summary>
    bool RemoveGatingSequence(Sequence sequence);

    /// <summary>현재 등록된 게이팅 시퀀스의 스냅샷.</summary>
    Sequence[] GetGatingSequences();

    /// <summary>진단 메시지에 사용할 게이팅 시퀀스 이름을 등록합니다.</summary>
    void SetGatingSequenceLabel(Sequence sequence, string label);

    /// <summary>시퀀스 배리어를 생성합니다.</summary>
    ISequenceBarrier NewBarrier(params Sequence[] sequencesToTrack);

    /// <summary>게이팅 시퀀스 중 최솟값을 반환합니다.</summary>
    long GetMinimumSequence();

    /// <summary>지금 게시 가능한 슬롯 수.</summary>
    long GetRemainingCapacity();

    /// <summary>n개의 슬롯을 지금 게시할 수 있는가.</summary>
    bool HasAvailableCapacity(int n);
}
