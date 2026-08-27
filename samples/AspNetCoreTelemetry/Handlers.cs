using Disruptor;
using Microsoft.Extensions.Logging;

namespace AspNetCoreTelemetry;

/// <summary>게임 커맨드. 슬롯이 재사용되므로 참조 필드를 두지 않았습니다.</summary>
public sealed class GameCommand
{
    public long SessionId;
    public int OpCode;
    public int Value;
}

/// <summary>
/// 세션별 상태. 컨슈머 스레드 <b>하나</b>만 쓰기 때문에 락이 없습니다.
/// </summary>
/// <remarks>
/// 읽기(HTTP 요청 스레드)는 <see cref="Volatile"/> 로만 스냅샷을 봅니다.
/// 정확한 일관성이 필요하면 이 조회도 파이프라인에 이벤트로 태우세요.
/// </remarks>
public sealed class GameWorld
{
    private readonly Dictionary<long, long> _scores = new();
    private long _applied;
    private long _sessionCount;

    public long Applied => Volatile.Read(ref _applied);

    public long SessionCount => Volatile.Read(ref _sessionCount);

    /// <summary>컨슈머 스레드에서만 호출됩니다.</summary>
    internal void Apply(GameCommand command)
    {
        ref long score = ref System.Runtime.InteropServices.CollectionsMarshal
                               .GetValueRefOrAddDefault(_scores, command.SessionId, out bool existed);

        if (!existed)
            Volatile.Write(ref _sessionCount, _scores.Count);

        score += command.Value;
        Volatile.Write(ref _applied, _applied + 1);
    }
}

/// <summary>
/// 감사 핸들러. 로직보다 <b>먼저</b> 실행되어야 "기록된 것만 적용된다"가 성립합니다.
/// </summary>
public sealed class AuditHandler : IEventHandler<GameCommand>
{
    private readonly ILogger<AuditHandler> _logger;
    private long _audited;
    private long _batches;

    // DI 컨테이너가 생성자 주입으로 만들어 줍니다 — 핸들러도 평범한 서비스입니다.
    public AuditHandler(ILogger<AuditHandler> logger) => _logger = logger;

    public long Audited => Volatile.Read(ref _audited);

    public void OnStart() => _logger.LogInformation("감사 핸들러 시작");

    public void OnBatchStart(long batchSize) => _batches++;

    public void OnEvent(GameCommand data, long sequence, bool endOfBatch)
    {
        // 실제라면 여기서 저널에 append 한다 (samples/MatchSimulation 참고).
        Volatile.Write(ref _audited, _audited + 1);
    }

    public void OnShutdown()
        => _logger.LogInformation("감사 핸들러 종료: {Audited}건 / 배치 {Batches}회", Audited, _batches);
}

/// <summary>
/// 컨슈머를 일부러 느리게 만드는 손잡이.
/// </summary>
/// <remarks>
/// 헬스체크의 Degraded / Unhealthy 전이는 <b>컨슈머가 프로듀서를 못 따라갈 때</b> 일어납니다.
/// 이 샘플의 핸들러는 너무 빨라서 그 상황이 재현되지 않으므로, 운영에서 흔한
/// "느려진 다운스트림(DB·외부 API)"을 이 값으로 흉내 냅니다.
/// </remarks>
public sealed class PipelineThrottle
{
    private int _spinPerEvent;

    public int SpinPerEvent
    {
        get => Volatile.Read(ref _spinPerEvent);
        set => Volatile.Write(ref _spinPerEvent, Math.Clamp(value, 0, 1_000_000));
    }
}

/// <summary>게임 로직 핸들러. 상태 변경은 이 스레드에서만 일어납니다.</summary>
public sealed class GameLogicHandler : IEventHandler<GameCommand>
{
    private readonly GameWorld _world;
    private readonly PipelineThrottle _throttle;

    public GameLogicHandler(GameWorld world, PipelineThrottle throttle)
    {
        _world = world;
        _throttle = throttle;
    }

    public void OnEvent(GameCommand data, long sequence, bool endOfBatch)
    {
        int spin = _throttle.SpinPerEvent;
        if (spin > 0)
            Thread.SpinWait(spin);   // 느린 다운스트림 흉내

        _world.Apply(data);
    }
}
