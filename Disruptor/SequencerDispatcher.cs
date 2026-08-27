using System.Runtime.CompilerServices;

namespace Disruptor;

/// <summary>
/// 시퀀서 호출을 구체 타입으로 분기해 인터페이스 디스패치를 제거하는 구조체.
/// </summary>
/// <remarks>
/// <para>
/// <b>왜 필요한가</b>: <c>RingBuffer.Next()</c> / <c>Publish()</c> 는 이벤트마다 호출되는 가장 뜨거운
/// 경로인데, <see cref="ISequencer"/> 를 통하면 매번 인터페이스 디스패치를 지불합니다.
/// 게시 경로는 핸들러 호출 지점과 달리 <b>단형이 아닐 수 있어</b>(같은 프로세스에 SP/MP 링 버퍼가 공존)
/// Dynamic PGO 의 guarded devirtualization 이 빗나갈 수 있습니다.
/// </para>
/// <para>
/// <see cref="SingleProducerSequencer"/> 와 <see cref="MultiProducerSequencer"/> 가 모두 <c>sealed</c>
/// 이므로, 구체 타입 필드로 호출하면 JIT 가 <b>직접 호출로 바꾸고 인라인</b>할 수 있습니다.
/// </para>
/// <para>
/// 측정으로 확인된 뒤에 도입했습니다 — 공식 disruptor-net 대비 유니캐스트 처리량이 2.2배 뒤졌고,
/// 그 격차의 상당 부분이 이 디스패치였습니다.
/// </para>
/// </remarks>
internal readonly struct SequencerDispatcher
{
    private readonly SingleProducerSequencer? _single;
    private readonly MultiProducerSequencer? _multi;
    private readonly ISequencer _fallback;

    public SequencerDispatcher(ISequencer sequencer)
    {
        _fallback = sequencer;
        _single = sequencer as SingleProducerSequencer;
        _multi = _single is null ? sequencer as MultiProducerSequencer : null;
    }

    public ISequencer Sequencer => _fallback;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Next()
    {
        if (_single is not null)
            return _single.NextOne();

        if (_multi is not null)
            return _multi.Next(1);

        return _fallback.Next(1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Next(int n)
    {
        if (_single is not null)
            return _single.Next(n);

        if (_multi is not null)
            return _multi.Next(n);

        return _fallback.Next(n);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Next(int n, CancellationToken cancellationToken)
    {
        if (_single is not null)
            return _single.Next(n, cancellationToken);

        if (_multi is not null)
            return _multi.Next(n, cancellationToken);

        return _fallback.Next(n, cancellationToken);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryNext(out long sequence, int n)
    {
        if (_single is not null)
            return _single.TryNext(out sequence, n);

        if (_multi is not null)
            return _multi.TryNext(out sequence, n);

        return _fallback.TryNext(out sequence, n);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryNext(out long sequence, int n, TimeSpan timeout)
    {
        if (_single is not null)
            return _single.TryNext(out sequence, n, timeout);

        if (_multi is not null)
            return _multi.TryNext(out sequence, n, timeout);

        return _fallback.TryNext(out sequence, n, timeout);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Publish(long sequence)
    {
        if (_single is not null)
        {
            _single.Publish(sequence);
            return;
        }

        if (_multi is not null)
        {
            _multi.Publish(sequence);
            return;
        }

        _fallback.Publish(sequence);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Publish(long lo, long hi)
    {
        if (_single is not null)
        {
            _single.Publish(lo, hi);
            return;
        }

        if (_multi is not null)
        {
            _multi.Publish(lo, hi);
            return;
        }

        _fallback.Publish(lo, hi);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long GetHighestPublishedSequence(long lowerBound, long availableSequence)
    {
        if (_single is not null)
            return availableSequence; // SP 는 커서가 곧 게시 상한이다

        if (_multi is not null)
            return _multi.GetHighestPublishedSequence(lowerBound, availableSequence);

        return _fallback.GetHighestPublishedSequence(lowerBound, availableSequence);
    }
}
