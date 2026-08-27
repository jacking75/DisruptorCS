using System.Runtime.CompilerServices;

namespace Disruptor;

/// <summary>
/// 백프레셔 대기 상태. 호출자의 스택에 두어 대기 1회당 힙 할당이 발생하지 않게 합니다.
/// </summary>
public struct ProducerBackoffState
{
    /// <summary>
    /// 커널에 양보하기 전에 순수 스핀할 횟수.
    /// </summary>
    /// <remarks>
    /// <see cref="SpinWait"/>는 약 10회 만에 <c>Thread.Yield()</c>로 넘어갑니다. 그런데 링 버퍼의
    /// 백프레셔는 대개 <b>컨슈머가 수백 나노초 안에 슬롯을 비우는</b> 짧은 대기입니다.
    /// 그 상황에서 양보하면 커널 진입(수백 ns~us)이 대기 자체보다 비싸집니다 —
    /// 작은 링에서 프로듀서와 컨슈머가 핑퐁하면 이 비용이 전체 처리량을 지배합니다.
    /// 그래서 먼저 충분히 스핀하고, 그래도 안 풀릴 때만 <see cref="SpinWait"/>의 에스컬레이션에 넘깁니다.
    /// </remarks>
    private const int AggressiveSpinIterations = 256;

    /// <summary>
    /// 순수 스핀 1회의 pause 횟수.
    /// </summary>
    /// <remarks>
    /// <b>작아야 합니다.</b> Skylake 이후 인텔 CPU 에서 <c>PAUSE</c> 한 번은 약 140 사이클이므로,
    /// <c>Thread.SpinWait(256)</c> 은 약 10us 동안 <b>조건을 재확인하지 않고</b> 멈춰 있는 것과 같습니다.
    /// 컨슈머가 수백 나노초 안에 슬롯을 비우는 핑퐁 상황에서는 그 자체가 지연이 됩니다.
    /// 짧게 멈추고 자주 확인하는 편이 낫습니다.
    /// </remarks>
    private const int SpinCountPerIteration = 1;

    private SpinWait _spin;

    /// <summary>이번 대기에서 반복한 횟수.</summary>
    public long IterationCount { get; private set; }

    public void Reset()
    {
        _spin = default;
        IterationCount = 0;
    }

    /// <summary>
    /// <c>Thread.Sleep(1)</c> 없이 1회 대기합니다. 처음에는 순수 스핀하고, 길어지면 양보합니다.
    /// </summary>
    /// <remarks>
    /// 어떤 경로에서도 <c>Thread.Sleep(1)</c>을 호출하지 않습니다. Windows 기본 타이머 분해능(15.6ms)
    /// 때문에 60Hz 게임 서버의 틱 예산(16.6ms)을 통째로 소모하기 때문입니다.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SpinOnceWithoutSleep()
    {
        long iteration = IterationCount++;

        if (iteration < AggressiveSpinIterations)
        {
            // 짧게 멈추고(PAUSE) 곧바로 조건을 재확인한다.
            Thread.SpinWait(SpinCountPerIteration);
            return;
        }

        _spin.SpinOnce(sleep1Threshold: -1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Pause()
    {
        Thread.SpinWait(1);
        IterationCount++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Yield()
    {
        Thread.Yield();
        IterationCount++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sleep(int milliseconds)
    {
        Thread.Sleep(milliseconds);
        IterationCount++;
    }
}

/// <summary>
/// 링 버퍼가 가득 찼을 때 프로듀서가 대기하는 방식.
/// </summary>
/// <remarks>
/// 게임 서버는 "절대 잠들면 안 되는 틱 스레드"와 "CPU를 양보해야 하는 배경 로깅"이 공존하므로
/// 대기 정책을 주입할 수 있어야 합니다. 구현체는 상태를 갖지 않으며(상태는
/// <see cref="ProducerBackoffState"/>에 있음) 인스턴스를 공유해도 안전합니다.
/// </remarks>
public interface IProducerBackoff
{
    /// <summary>
    /// <c>elapsedTicks</c> 를 실제로 사용하는가.
    /// </summary>
    /// <remarks>
    /// false 면 시퀀서가 대기 루프에서 <c>Stopwatch.GetTimestamp()</c> 호출을 생략합니다.
    /// QPC 는 시리얼라이징 성격이라, 몇 번의 스핀으로 풀리는 짧은 백프레셔에서는
    /// 이 호출 자체가 해소 지연의 상당 부분을 차지합니다.
    /// </remarks>
    bool NeedsTime => false;

    /// <summary>
    /// 백프레셔 대기 1회.
    /// </summary>
    /// <param name="state">호출자 스택의 대기 상태</param>
    /// <param name="elapsedTicks">대기를 시작한 뒤 경과한 Stopwatch 틱</param>
    /// <returns>계속 대기하려면 true. false를 반환하면 <c>Next()</c>가 <see cref="TimeoutException"/>을 던집니다.</returns>
    bool Wait(ref ProducerBackoffState state, long elapsedTicks);
}

/// <summary>
/// 틱 스레드용 — 절대 <c>Thread.Sleep(1)</c>을 하지 않고 무한히 대기합니다. 기본 전략입니다.
/// </summary>
public sealed class SpinOnlyBackoff : IProducerBackoff
{
    public static SpinOnlyBackoff Instance { get; } = new();

    public bool Wait(ref ProducerBackoffState state, long elapsedTicks)
    {
        state.SpinOnceWithoutSleep();
        return true;
    }
}

/// <summary>
/// 배경 작업용 — 지정 시간을 넘기면 실패시켜 호출자가 드롭 여부를 판단하게 합니다.
/// </summary>
public sealed class TimeBoundedBackoff : IProducerBackoff
{
    private readonly long _limitTicks;

    /// <inheritdoc/>
    public bool NeedsTime => true;

    public TimeBoundedBackoff(TimeSpan limit)
    {
        if (limit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(limit));

        _limitTicks = (long)(limit.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
    }

    public bool Wait(ref ProducerBackoffState state, long elapsedTicks)
    {
        if (elapsedTicks > _limitTicks)
            return false;

        state.SpinOnceWithoutSleep();
        return true;
    }
}

/// <summary>
/// CPU 사용량을 낮추는 전략 — 스핀 → Yield → Sleep 순으로 물러납니다.
/// 지연에 민감하지 않은 배경 파이프라인(로그, 통계 적재)에 적합합니다.
/// </summary>
/// <remarks>
/// <b>틱 스레드에는 절대 사용하지 마세요.</b> <c>Thread.Sleep(1)</c> 단계가 있으므로
/// Windows 기본 타이머 분해능(15.6ms) 때문에 tail latency 절벽이 생깁니다.
/// <para>
/// 실측(링 1024, 300,000건, 느린 컨슈머):
/// <list type="bullet">
///   <item><see cref="SpinOnlyBackoff"/>: &gt;15ms <b>0건</b>, max 11.05ms, 96,915 ops/s</item>
///   <item><see cref="SleepingBackoff"/>: &gt;15ms <b>271건</b>, max 30.39ms, 64,518 ops/s</item>
/// </list>
/// CPU를 아끼는 대신 60Hz 틱(16.6ms) 예산을 통째로 잃는 거래입니다. 의도적으로 선택하세요.
/// </para>
/// </remarks>
public sealed class SleepingBackoff : IProducerBackoff
{
    private readonly int _spinLimit;
    private readonly int _yieldLimit;
    private readonly int _sleepMs;

    public SleepingBackoff(int spinLimit = 200, int yieldLimit = 400, int sleepMs = 1)
    {
        _spinLimit = spinLimit;
        _yieldLimit = yieldLimit;
        _sleepMs = sleepMs;
    }

    public static SleepingBackoff Instance { get; } = new();

    public bool Wait(ref ProducerBackoffState state, long elapsedTicks)
    {
        if (state.IterationCount < _spinLimit)
            state.Pause();
        else if (state.IterationCount < _yieldLimit)
            state.Yield();
        else
            state.Sleep(_sleepMs);

        return true;
    }
}
