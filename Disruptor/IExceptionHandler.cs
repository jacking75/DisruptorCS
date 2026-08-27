namespace Disruptor;

/// <summary>
/// 이벤트 처리 중 예외가 발생했을 때 이벤트 루프가 취할 동작.
/// </summary>
public enum EventExceptionAction
{
    /// <summary>이벤트를 버리고 다음 시퀀스로 진행합니다. (게임 서버 기본값)</summary>
    Skip,

    /// <summary>같은 이벤트를 다시 처리합니다. 핸들러가 멱등할 때만 사용하세요.</summary>
    Retry,

    /// <summary>이 프로세서만 정지합니다. 프로세스는 살려 둡니다.</summary>
    Halt,
}

/// <summary>
/// 예외 처리 핸들러.
/// </summary>
/// <remarks>
/// 반환값으로 동작을 지시하므로, "중단"을 표현하기 위해 예외를 다시 던질 필요가 없습니다.
/// 예외를 재throw하면 백그라운드 스레드의 미처리 예외가 되어
/// <b>프로세스 전체가 즉시 종료</b>됩니다(비정상 패킷 하나로 게임 서버가 다운됨).
/// </remarks>
public interface IExceptionHandler<in T>
{
    /// <summary>이벤트 처리 중 예외. 반환값으로 이벤트 루프의 다음 동작을 지시합니다.</summary>
    EventExceptionAction HandleEventException(Exception ex, long sequence, T? @event);

    /// <summary>이벤트가 정상 처리되었음을 알립니다. 연속 실패 카운터 초기화 등에 사용합니다.</summary>
    void OnEventProcessed() { }

    void HandleOnStartException(Exception ex);

    void HandleOnShutdownException(Exception ex);
}
