using Disruptor.WaitStrategy;
using System.Runtime.CompilerServices;

namespace Disruptor;

public sealed class SequenceBarrier : ISequenceBarrier
{
    private readonly ISequencer _sequencer;
    private readonly IWaitStrategy _waitStrategy;
    private readonly Sequence _cursorSequence;
    private readonly Sequence _waitSequence;          // WaitStrategy에 전달할 시퀀스
    private readonly Sequence[]? _compositeSequences;  // 2개 이상 의존 시 사용
    private volatile bool _alerted;

    public SequenceBarrier(ISequencer sequencer, IWaitStrategy waitStrategy,
                           Sequence cursorSequence, params Sequence[] dependentSequences)
    {
        _sequencer = sequencer;
        _waitStrategy = waitStrategy;
        _cursorSequence = cursorSequence;

        if (dependentSequences.Length == 0)
        {
            // 의존 없음 — 커서만 추적
            _waitSequence = cursorSequence;
            _compositeSequences = null;
        }
        else if (dependentSequences.Length == 1)
        {
            // 단일 의존 — 해당 시퀀스를 직접 전달 (virtual dispatch 없음)
            _waitSequence = dependentSequences[0];
            _compositeSequences = null;
        }
        else
        {
            // 다중 의존 — 커서로 대기 후 별도로 최솟값 체크
            _waitSequence = cursorSequence;
            _compositeSequences = dependentSequences;
        }
    }

    public long WaitFor(long sequence)
    {
        CheckAlert();
        long availableSequence = _waitStrategy.WaitFor(
            sequence, _cursorSequence, _waitSequence, this);

        if (availableSequence < sequence)
            return availableSequence;

        // Multi-producer 안전: 연속적으로 게시된 최고 시퀀스만 반환
        // (Single-producer에서는 availableSequence를 그대로 반환하므로 오버헤드 없음)
        long highestPublished = _sequencer.GetHighestPublishedSequence(
            sequence, availableSequence);

        if (_compositeSequences != null)
        {
            // 모든 의존 시퀀스가 요청 시퀀스 이상이 될 때까지 대기
            // SpinWait를 사용하여 spin → yield → sleep 자동 에스컬레이션
            long min;
            var spinWait = new SpinWait();
            while ((min = GetMinimumSequence(_compositeSequences)) < sequence)
            {
                CheckAlert();
                spinWait.SpinOnce();
            }
            if (min < highestPublished)
                highestPublished = min;
        }

        return highestPublished;
    }

    public long Cursor => _cursorSequence.Value;

    public bool IsAlerted => _alerted;

    public void Alert()
    {
        _alerted = true;
        _waitStrategy.SignalAllWhenBlocking();
    }

    public void ClearAlert()
    {
        _alerted = false;
    }

    public void CheckAlert()
    {
        if (_alerted)
            throw AlertException.Instance;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long GetMinimumSequence(Sequence[] sequences)
    {
        long min = long.MaxValue;
        for (int i = 0; i < sequences.Length; i++)
        {
            long val = sequences[i].Value;
            if (val < min) min = val;
        }
        return min;
    }
}
