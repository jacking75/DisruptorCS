using Disruptor.WaitStrategy;

namespace Disruptor;

public sealed class SequenceBarrier : ISequenceBarrier
{
    private readonly SequencerDispatcher _dispatcher;
    private readonly IWaitStrategy _waitStrategy;
    private readonly Sequence _cursorSequence;
    private readonly DependentSequenceGroup _dependents;

    public SequenceBarrier(ISequencer sequencer, IWaitStrategy waitStrategy,
                           Sequence cursorSequence, params Sequence[] dependentSequences)
    {
        ArgumentNullException.ThrowIfNull(sequencer);
        ArgumentNullException.ThrowIfNull(waitStrategy);
        ArgumentNullException.ThrowIfNull(cursorSequence);

        _dispatcher = new SequencerDispatcher(sequencer);
        _waitStrategy = waitStrategy;
        _cursorSequence = cursorSequence;
        _dependents = new DependentSequenceGroup(cursorSequence, dependentSequences ?? []);
    }

    public long Cursor => _cursorSequence.Value;

    public DependentSequenceGroup Dependents => _dependents;

    public IWaitStrategy WaitStrategy => _waitStrategy;

    public SequenceWaitResult WaitFor(long sequence, CancellationToken cancellationToken)
    {
        var result = _waitStrategy.WaitFor(sequence, _dependents, cancellationToken);

        if (result.IsCancelled || result.IsTimeout)
            return result;

        long available = result.AvailableSequence;
        if (available < sequence)
            return available;

        // 의존 핸들러가 있다면 available 은 이미 "상위 핸들러가 처리를 끝낸" 시퀀스이고,
        // 처리되었다는 것은 게시되었다는 뜻이므로 게시 여부를 다시 확인할 필요가 없다.
        // (Multi 프로듀서에서 O(n) 스캔을 건너뛰는 최적화)
        if (!_dependents.DependsOnCursor)
            return available;

        // Multi-producer 안전: 연속적으로 게시된 최고 시퀀스만 반환
        // (Single-producer에서는 availableSequence를 그대로 반환하므로 오버헤드 없음)
        return _dispatcher.GetHighestPublishedSequence(sequence, available);
    }
}
