namespace Disruptor;

/// <summary>
/// 게임 서버 기본 예외 핸들러.
/// 예외를 로깅/집계하고 이벤트를 건너뛰며, 연속 실패가 임계치를 넘으면 해당 프로세서만 정지합니다.
/// </summary>
/// <remarks>
/// <b>프로세스를 절대 종료시키지 않습니다.</b> 유저 한 명의 비정상 입력으로 서버 전체가
/// 다운되는 것을 구조적으로 막습니다.
/// </remarks>
public sealed class ResilientExceptionHandler<T> : IExceptionHandler<T>
{
    private readonly Action<Exception, long, T?> _onError;
    private readonly int _maxConsecutiveFailures;
    private int _consecutive;

    public ResilientExceptionHandler(Action<Exception, long, T?> onError,
                                     int maxConsecutiveFailures = 100)
    {
        ArgumentNullException.ThrowIfNull(onError);
        if (maxConsecutiveFailures < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConsecutiveFailures));

        _onError = onError;
        _maxConsecutiveFailures = maxConsecutiveFailures;
    }

    /// <summary>
    /// 표준 오류(stderr)에 기록하는 프로세서 전용 기본 핸들러를 만듭니다.
    /// </summary>
    /// <remarks>
    /// <b>인스턴스를 공유하면 안 됩니다.</b> 연속 실패 스트릭(<c>_consecutive</c>)이 인스턴스 상태이므로,
    /// 여러 프로세서가 하나를 공유하면 (a) 다른 프로세서의 성공이 실패 스트릭을 리셋해
    /// 무한 오류 루프 감지가 영영 발동하지 않거나, (b) 서로 다른 파이프라인의 실패가 합산되어
    /// 엉뚱한 프로세서에서 Halt 가 반환됩니다. 그래서 싱글턴 프로퍼티가 아니라 팩토리입니다.
    /// </remarks>
    public static ResilientExceptionHandler<T> CreateDefault()
        => new(static (ex, seq, evt) => Console.Error.WriteLine(
            $"[Disruptor][ERROR] seq={seq} event={evt}: {ex}"));

    /// <summary>지금까지 관측된 연속 실패 횟수.</summary>
    public int ConsecutiveFailures => Volatile.Read(ref _consecutive);

    public EventExceptionAction HandleEventException(Exception ex, long sequence, T? @event)
    {
        _onError(ex, sequence, @event); // 로깅 / 메트릭 / 데드레터 큐

        // 연속 실패가 임계치를 넘으면 무한 오류 루프로 보고 이 프로세서만 정지
        if (Interlocked.Increment(ref _consecutive) >= _maxConsecutiveFailures)
            return EventExceptionAction.Halt;

        return EventExceptionAction.Skip;
    }

    public void OnEventProcessed() => Volatile.Write(ref _consecutive, 0);

    public void HandleOnStartException(Exception ex) => _onError(ex, -1, default);

    public void HandleOnShutdownException(Exception ex) => _onError(ex, -1, default);
}

/// <summary>
/// 예외를 로깅만 하고 항상 건너뛰는 핸들러. 프로세서를 절대 정지시키지 않습니다.
/// </summary>
public sealed class IgnoreExceptionHandler<T> : IExceptionHandler<T>
{
    private readonly Action<Exception, long, T?>? _onError;

    public IgnoreExceptionHandler(Action<Exception, long, T?>? onError = null) => _onError = onError;

    public EventExceptionAction HandleEventException(Exception ex, long sequence, T? @event)
    {
        if (_onError is not null)
            _onError(ex, sequence, @event);
        else
            Console.Error.WriteLine($"[Disruptor][WARN] seq={sequence}: {ex.Message}");

        return EventExceptionAction.Skip;
    }

    public void HandleOnStartException(Exception ex)
        => Console.Error.WriteLine($"[Disruptor][WARN] OnStart: {ex.Message}");

    public void HandleOnShutdownException(Exception ex)
        => Console.Error.WriteLine($"[Disruptor][WARN] OnShutdown: {ex.Message}");
}

/// <summary>
/// 첫 예외에서 해당 프로세서를 정지시키는 핸들러.
/// </summary>
/// <remarks>
/// <b>프로세스를 종료시키지는 않습니다.</b> 다만 프로세서가 멈추면 그 시퀀스가 게이팅에 남아
/// 프로듀서가 곧 영구 블록되므로, <see cref="Dsl.DisruptorBase.ProcessorHalted"/> 이벤트를 구독해
/// 서버를 계획적으로 드레인해야 합니다.
/// </remarks>
public sealed class HaltOnExceptionHandler<T> : IExceptionHandler<T>
{
    private readonly Action<Exception, long, T?>? _onError;

    public HaltOnExceptionHandler(Action<Exception, long, T?>? onError = null) => _onError = onError;

    public EventExceptionAction HandleEventException(Exception ex, long sequence, T? @event)
    {
        if (_onError is not null)
            _onError(ex, sequence, @event);
        else
            Console.Error.WriteLine($"[Disruptor][FATAL] seq={sequence} event={@event}: {ex}");

        return EventExceptionAction.Halt;
    }

    public void HandleOnStartException(Exception ex)
        => Console.Error.WriteLine($"[Disruptor][FATAL] OnStart: {ex}");

    public void HandleOnShutdownException(Exception ex)
        => Console.Error.WriteLine($"[Disruptor][FATAL] OnShutdown: {ex}");
}
