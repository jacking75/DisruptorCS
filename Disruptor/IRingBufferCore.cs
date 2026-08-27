namespace Disruptor;

/// <summary>
/// 참조 타입 링 버퍼와 값 타입 링 버퍼가 공유하는, 이벤트 타입에 무관한 조정 인터페이스.
/// DSL의 수명 주기 관리 코드를 양쪽에서 재사용하기 위해 존재합니다.
/// </summary>
public interface IRingBufferCore
{
    int BufferSize { get; }

    long Cursor { get; }

    double Utilization { get; }

    long RemainingCapacity { get; }

    ISequenceBarrier NewBarrier(params Sequence[] sequencesToTrack);

    void AddGatingSequences(params Sequence[] gatingSequences);

    bool RemoveGatingSequence(Sequence sequence);

    Sequence[] GetGatingSequences();

    void SetGatingSequenceLabel(Sequence sequence, string label);

    long GetHighestPublishedSequence(long lowerBound, long availableSequence);

    long GetMinimumGatingSequence();
}
