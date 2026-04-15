using System;
using System.Collections.Generic;
using System.Text;

namespace Disruptor;

public interface ISequencer
{
    int BufferSize { get; }
    long Cursor { get; }

    /// <summary>다음 시퀀스를 할당받습니다.</summary>
    long Next(int n = 1);

    /// <summary>시퀀스 범위를 시도합니다 (non-blocking).</summary>
    bool TryNext(out long sequence, int n = 1);

    /// <summary>시퀀스를 게시합니다.</summary>
    void Publish(long sequence);

    /// <summary>범위를 게시합니다.</summary>
    void Publish(long lo, long hi);

    /// <summary>해당 시퀀스가 사용 가능한지 확인합니다.</summary>
    bool IsAvailable(long sequence);

    /// <summary>가장 높은 게시된 시퀀스를 반환합니다.</summary>
    long GetHighestPublishedSequence(long lowerBound, long availableSequence);

    /// <summary>게이팅 시퀀스를 추가합니다 (컨슈머 추적).</summary>
    void AddGatingSequences(params Sequence[] gatingSequences);

    /// <summary>게이팅 시퀀스를 제거합니다.</summary>
    bool RemoveGatingSequence(Sequence sequence);

    /// <summary>시퀀스 배리어를 생성합니다.</summary>
    ISequenceBarrier NewBarrier(params Sequence[] sequencesToTrack);

    /// <summary>게이팅 시퀀스 중 최솟값을 반환합니다.</summary>
    long GetMinimumSequence();
}