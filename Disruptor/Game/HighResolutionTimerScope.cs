using System.Runtime.InteropServices;

namespace Disruptor.Game;

/// <summary>
/// Windows 시스템 타이머 분해능을 1ms로 올립니다. 유휴 상태의 틱 정밀도가 필요할 때 사용합니다.
/// </summary>
/// <remarks>
/// <para>
/// <b>왜 필요한가</b>: <see cref="WaitStrategy.TimeoutBlockingWaitStrategy"/>(1ms) + <see cref="FixedTickDriver"/>
/// 조합은 이벤트가 흐르는 동안은 정확합니다(배치 끝마다 <c>TryAdvance</c>). 그러나 <b>완전 유휴</b> 구간에서는
/// <c>Monitor.Wait(gate, 1)</c>이 시스템 타이머 인터럽트(기본 15.625ms)에 양자화됩니다.
/// 실측 결과 1ms 를 요청해도 <c>OnTimeout</c> 실제 간격이 <b>15.40ms</b> 였습니다 —
/// 60Hz 틱(16.6ms)이 유휴 시 사실상 틱당 1회의 기회만 갖게 되고, 스케줄 편차가 겹치면 틱을 건너뜁니다.
/// 로비처럼 트래픽이 띄엄띄엄한 서버에서 실제로 문제가 됩니다.
/// </para>
/// <para><b>실측 (2초 관측, i7-12700KF / Windows 11)</b></para>
/// <list type="bullet">
///   <item>기본: <c>OnTimeout</c> 130회 → 간격 <b>15.40ms</b></item>
///   <item>적용: <c>OnTimeout</c> 1,314회 → 간격 <b>1.52ms</b></item>
/// </list>
/// <para>
/// <b>트레이드오프</b>: 시스템 전역 설정이므로 유휴 전력 소모가 늘어납니다. 상시 부하가 있는 서버에는
/// 무해하지만 노트북/클라이언트 빌드에는 넣지 마세요. Windows 11 은 포그라운드 부스트가 없는
/// 백그라운드 프로세스의 요청을 무시할 수 있으므로, 서비스로 배포할 때는 실측으로 확인하세요.
/// </para>
/// <para>
/// 참조 카운트 방식이라 중첩 사용이 안전합니다. Windows 가 아니면 아무 일도 하지 않습니다
/// (Linux 는 <c>CONFIG_HIGH_RES_TIMERS</c> 로 이미 고분해능입니다).
/// </para>
/// <example>
/// <code>
/// // 게임 서버 시작부에서 프로세스 수명 동안 유지
/// using var timerScope = new HighResolutionTimerScope();
/// </code>
/// </example>
/// </remarks>
public sealed partial class HighResolutionTimerScope : IDisposable
{
    private const uint TargetPeriodMs = 1;

    private static int _refCount;

    private bool _active;

    public HighResolutionTimerScope()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            if (Interlocked.Increment(ref _refCount) == 1)
            {
                _active = timeBeginPeriod(TargetPeriodMs) == 0;
                if (!_active)
                    Interlocked.Decrement(ref _refCount);
            }
            else
            {
                _active = true;
            }
        }
        catch (DllNotFoundException)
        {
            Interlocked.Decrement(ref _refCount);
        }
        catch (EntryPointNotFoundException)
        {
            Interlocked.Decrement(ref _refCount);
        }
    }

    /// <summary>고분해능 타이머가 실제로 적용되었는가.</summary>
    public bool IsActive => _active;

    /// <summary>현재 프로세스에서 활성화된 스코프 수.</summary>
    public static int ActiveScopeCount => Volatile.Read(ref _refCount);

    public void Dispose()
    {
        if (!_active)
            return;

        _active = false;

        if (Interlocked.Decrement(ref _refCount) == 0)
        {
            try
            {
                timeEndPeriod(TargetPeriodMs);
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }
    }

    [LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint uMilliseconds);

    [LibraryImport("winmm.dll")]
    private static partial uint timeEndPeriod(uint uMilliseconds);
}
