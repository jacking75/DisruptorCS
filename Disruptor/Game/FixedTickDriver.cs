using System.Diagnostics;

namespace Disruptor.Game;

/// <summary>
/// 고정 스텝 틱을 진행시키는 드라이버.
/// 이벤트 소비 스레드에서 <see cref="TryAdvance"/>를 호출하면 경과 시간만큼 틱을 진행합니다.
/// </summary>
/// <remarks>
/// <para>
/// 별도 타이머 스레드를 두면 게임 로직 스레드와 다시 락으로 동기화해야 하므로
/// Disruptor를 쓰는 의미가 사라집니다. 이 드라이버는
/// <see cref="IEventHandler{T}.OnBatchStart"/> / <see cref="IEventHandler{T}.OnTimeout"/>
/// 두 지점에서 호출되어 <b>이벤트 소비와 틱 진행을 한 스레드에서</b> 처리합니다.
/// </para>
/// <para>
/// <b>death spiral 방지</b>: 틱이 크게 밀리면 따라잡기를 포기하고 현재 시각으로 리셋합니다.
/// 그렇게 하지 않으면 밀린 틱을 처리하느라 더 밀리는 악순환에 빠집니다.
/// </para>
/// </remarks>
public sealed class FixedTickDriver
{
    private readonly long _tickIntervalTicks;
    private readonly int _maxCatchUpTicks;
    private readonly Action<long> _onTick;

    private long _nextTickAt;
    private long _tickId;
    private long _skippedTicks;

    /// <param name="tickInterval">틱 간격 (예: 60Hz면 16.67ms)</param>
    /// <param name="onTick">틱 콜백. 인자는 틱 번호입니다.</param>
    /// <param name="maxCatchUpTicks">한 번에 따라잡을 최대 틱 수. 초과하면 리셋합니다.</param>
    public FixedTickDriver(TimeSpan tickInterval, Action<long> onTick, int maxCatchUpTicks = 5)
    {
        ArgumentNullException.ThrowIfNull(onTick);
        if (tickInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(tickInterval));

        _tickIntervalTicks = (long)(tickInterval.TotalSeconds * Stopwatch.Frequency);
        _maxCatchUpTicks = Math.Max(1, maxCatchUpTicks);
        _onTick = onTick;
        _nextTickAt = Stopwatch.GetTimestamp();
    }

    /// <summary>60Hz(16.67ms) 드라이버를 만듭니다.</summary>
    public static FixedTickDriver At60Hz(Action<long> onTick)
        => new(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60), onTick);

    /// <summary>30Hz(33.3ms) 드라이버를 만듭니다.</summary>
    public static FixedTickDriver At30Hz(Action<long> onTick)
        => new(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30), onTick);

    /// <summary>지금까지 진행한 틱 번호.</summary>
    public long CurrentTick => _tickId;

    /// <summary>따라잡기를 포기하고 건너뛴 틱 수. 서버 과부하 지표입니다.</summary>
    public long SkippedTicks => _skippedTicks;

    /// <summary>진행해야 할 틱이 있으면 진행합니다. 실제로 진행한 틱 수를 반환합니다.</summary>
    public int TryAdvance()
    {
        long now = Stopwatch.GetTimestamp();
        int advanced = 0;

        while (now >= _nextTickAt)
        {
            _onTick(_tickId++);
            _nextTickAt += _tickIntervalTicks;
            advanced++;

            if (advanced < _maxCatchUpTicks)
                continue;

            // 여전히 밀려 있으면 따라잡기를 포기하고 현재 시각으로 리셋한다 (death spiral 방지).
            if (now >= _nextTickAt)
            {
                long behind = (now - _nextTickAt) / _tickIntervalTicks + 1;
                _skippedTicks += behind;
                _tickId += behind;
                _nextTickAt = now + _tickIntervalTicks;
            }

            break;
        }

        return advanced;
    }

    /// <summary>다음 틱까지 남은 시간.</summary>
    public TimeSpan TimeUntilNextTick()
    {
        long remaining = _nextTickAt - Stopwatch.GetTimestamp();
        return remaining <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency);
    }

    /// <summary>틱 기준 시각을 현재로 리셋합니다.</summary>
    public void Reset()
    {
        _nextTickAt = Stopwatch.GetTimestamp();
    }
}
