namespace Disruptor;

/// <summary>폴링 시도의 결과.</summary>
public enum PollState
{
    /// <summary>이벤트를 1건 이상 처리했습니다.</summary>
    Processing,

    /// <summary>처리할 이벤트가 없습니다.</summary>
    Idle,
}

/// <summary>
/// 슬롯을 채우는 동작을 값 타입으로 표현합니다. 델리게이트/클로저 할당이 없습니다.
/// </summary>
public interface IEventTranslator<in T, in TArg>
{
    void Translate(T evt, long sequence, TArg arg);
}

/// <summary>
/// 전용 스레드 없이 호출자의 스레드에서 이벤트를 당겨 처리하는 폴러.
/// </summary>
/// <remarks>
/// <para>
/// 기본 모델("핸들러당 전용 스레드")은 게임 개발의 현실과 어긋날 때가 있습니다 —
/// Unity 의 <c>Update()</c>, 자체 서버의 프레임 루프처럼 <b>이미 메인 루프가 있는</b> 경우입니다.
/// 이 폴러는 "내 루프의 이 지점에서, 지금까지 쌓인 이벤트를 내 스레드로 당겨오기"를 제공해
/// 스레드 모델을 바꾸지 않고도 Disruptor 를 쓸 수 있게 합니다.
/// </para>
/// <para>
/// <b>스레드 안전성</b>: 하나의 폴러는 하나의 스레드에서만 <see cref="Poll"/> 해야 합니다
/// (폴러의 시퀀스가 곧 소비 진행 상태입니다). 여러 스레드가 나눠 처리하려면
/// <see cref="Work.WorkerPool{T}"/>를 사용하세요.
/// </para>
/// <para>
/// <b>반드시 Dispose 하세요</b>: 폴러도 게이팅에 등록되므로, 폴링을 멈춘 채 방치하면
/// 링 버퍼가 차고 프로듀서가 영구히 막힙니다.
/// </para>
/// </remarks>
public sealed class EventPoller<T> : IDisposable where T : class
{
    /// <summary>
    /// 이벤트 1건을 처리합니다.
    /// </summary>
    /// <returns>
    /// 계속 처리하려면 true. false 를 반환하면 이번 <see cref="Poll"/>을 중단합니다
    /// (이 이벤트까지는 처리된 것으로 기록되고, 나머지는 다음 Poll 에서 처리됩니다).
    /// </returns>
    public delegate bool Handler(T evt, long sequence, bool endOfBatch);

    private readonly RingBuffer<T> _ringBuffer;
    private readonly DependentSequenceGroup _dependents;
    private readonly Sequence _sequence = new();
    private int _disposed;

    internal EventPoller(RingBuffer<T> ringBuffer, Sequence cursor, Sequence[] dependentSequences)
    {
        _ringBuffer = ringBuffer;
        _dependents = new DependentSequenceGroup(cursor, dependentSequences);

        // 생성 시점 이후에 게시된 이벤트만 소비한다.
        _sequence.SetValue(ringBuffer.Cursor);
        _ringBuffer.AddGatingSequences(_sequence);
    }

    /// <summary>이 폴러의 진행 시퀀스. 게이팅에 등록되어 있습니다.</summary>
    public Sequence Sequence => _sequence;

    /// <summary>아직 처리하지 않고 남은 이벤트 수 (모니터링용 추정치).</summary>
    public long Backlog => Math.Max(0, _dependents.Value - _sequence.Value);

    /// <summary>
    /// 지금 소비 가능한 이벤트를 처리합니다. <b>대기하지 않습니다.</b>
    /// </summary>
    /// <param name="onEvent">이벤트 콜백</param>
    /// <param name="maxEvents">
    /// 이번 호출에서 처리할 최대 건수. 프레임 예산을 지키는 장치입니다 —
    /// 밀린 10만 건을 한 프레임에 다 처리해 스파이크를 만들지 않도록 기본값이 제한되어 있습니다.
    /// </param>
    public PollState Poll(Handler onEvent, int maxEvents = 256)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(onEvent);
        if (maxEvents < 1)
            throw new ArgumentOutOfRangeException(nameof(maxEvents));

        long next = _sequence.Value + 1L;
        long limit = _dependents.Value; // 커서 또는 상위 핸들러 시퀀스

        if (limit < next)
            return PollState.Idle;

        // Multi 프로듀서: 연속으로 게시된 상한까지만 소비한다.
        long available = _dependents.DependsOnCursor
            ? _ringBuffer.GetHighestPublishedSequence(next, limit)
            : limit;

        if (available < next)
            return PollState.Idle;

        long end = Math.Min(available, next + maxEvents - 1);
        long current = next;

        try
        {
            while (current <= end)
            {
                bool keepGoing = onEvent(_ringBuffer[current], current, current == end);
                current++;

                if (!keepGoing)
                    break;
            }
        }
        finally
        {
            // 예외가 나도 처리 완료분까지는 반드시 반영한다 — 게이팅이 멈추면 프로듀서가 막힌다.
            _sequence.SetValue(current - 1);
        }

        return PollState.Processing;
    }

    /// <summary>게이팅에서 제거합니다. 폴링을 그만둘 때 반드시 호출하세요.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _ringBuffer.RemoveGatingSequence(_sequence);
    }
}

/// <summary>
/// 값 타입 이벤트용 폴러. 슬롯을 <c>ref</c>로 전달합니다.
/// </summary>
public sealed class ValueEventPoller<T> : IDisposable where T : struct
{
    /// <inheritdoc cref="EventPoller{T}.Handler"/>
    public delegate bool Handler(ref T evt, long sequence, bool endOfBatch);

    private readonly ValueRingBuffer<T> _ringBuffer;
    private readonly DependentSequenceGroup _dependents;
    private readonly Sequence _sequence = new();
    private int _disposed;

    internal ValueEventPoller(ValueRingBuffer<T> ringBuffer, Sequence cursor, Sequence[] dependentSequences)
    {
        _ringBuffer = ringBuffer;
        _dependents = new DependentSequenceGroup(cursor, dependentSequences);
        _sequence.SetValue(ringBuffer.Cursor);
        _ringBuffer.AddGatingSequences(_sequence);
    }

    public Sequence Sequence => _sequence;

    public long Backlog => Math.Max(0, _dependents.Value - _sequence.Value);

    /// <inheritdoc cref="EventPoller{T}.Poll"/>
    public PollState Poll(Handler onEvent, int maxEvents = 256)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(onEvent);
        if (maxEvents < 1)
            throw new ArgumentOutOfRangeException(nameof(maxEvents));

        long next = _sequence.Value + 1L;
        long limit = _dependents.Value;

        if (limit < next)
            return PollState.Idle;

        long available = _dependents.DependsOnCursor
            ? _ringBuffer.GetHighestPublishedSequence(next, limit)
            : limit;

        if (available < next)
            return PollState.Idle;

        long end = Math.Min(available, next + maxEvents - 1);
        long current = next;

        try
        {
            while (current <= end)
            {
                bool keepGoing = onEvent(ref _ringBuffer[current], current, current == end);
                current++;

                if (!keepGoing)
                    break;
            }
        }
        finally
        {
            _sequence.SetValue(current - 1);
        }

        return PollState.Processing;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _ringBuffer.RemoveGatingSequence(_sequence);
    }
}
