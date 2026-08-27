using System.Text;
using System.Text.Json;
using AspNetCoreTelemetry;
using Disruptor;
using Disruptor.Dsl;
using Disruptor.WaitStrategy;
using Microsoft.Extensions.Diagnostics.HealthChecks;

// ─────────────────────────────────────────────────────────────────────────────
// 이 샘플이 답하는 질문:
//   "이걸 우리 ASP.NET Core 서비스에 넣으면, 운영에서 무슨 일이 벌어지는지 보이는가?"
//
// 답: 세 줄로 붙고, 세 가지가 즉시 보입니다.
//   1. services.AddDisruptor<T>(...)        — DI 등록 + 호스트 수명주기 결속
//   2. .AddDisruptorCheck<T>()              — /health 에 링 사용률·정지 프로세서 노출
//   3. options.EnableMetrics = true         — System.Diagnostics.Metrics 계측 방출
//
// 엔드포인트
//   POST /commands            커맨드 게시 (백프레셔 정책이 실제로 동작한다)
//   GET  /health              헬스체크 JSON
//   GET  /metrics             계측값 스냅샷 (MeterListener 로 직접 수집)
//   POST /throttle?spin=N     컨슈머를 느리게 만든다 (느린 DB 흉내)
//   POST /load?rps=&seconds=  부하를 걸어 Degraded/Unhealthy 전이를 눈으로 확인
//
// OpenTelemetry 를 쓴다면 이 샘플의 MeterListener 대신 한 줄이면 됩니다:
//   builder.Services.AddOpenTelemetry().WithMetrics(m => m.AddMeter(DisruptorMetrics.MeterName));
// 이 샘플은 오프라인에서도 그대로 돌도록 외부 패키지 없이 같은 계측을 읽습니다.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(o => o.SingleLine = true);

// ── 1) 파이프라인 등록 ───────────────────────────────────────────────────────
builder.Services.AddDisruptor<GameCommand>(d => d
    .WithEventFactory(() => new GameCommand())
    .WithRingSize(4096)                               // 작게 잡아야 백프레셔를 볼 수 있다
    .WithProducerType(ProducerType.Multi)             // 여러 요청 스레드가 동시에 게시
    .WithWaitStrategy(() => new TimeoutBlockingWaitStrategy(TimeSpan.FromMilliseconds(1)))
    .WithOptions(o => o with
    {
        Name = "game-commands",
        EnableMetrics = true,                          // ← 이 한 줄이 계측을 켠다
        StallThreshold = TimeSpan.FromMilliseconds(200),
        OnStall = info => Console.Error.WriteLine($"[스톨] {info}"),
    })
    .HandleEventsWith<AuditHandler>()                  // 감사 로그가 먼저
    .Then<GameLogicHandler>());                        // 그 다음 게임 로직

builder.Services.AddSingleton<GameWorld>();
builder.Services.AddSingleton<PipelineThrottle>();

// ── 2) 헬스체크 ──────────────────────────────────────────────────────────────
builder.Services
    .AddHealthChecks()
    .AddDisruptorCheck<GameCommand>(
        name: "disruptor",
        degradedUtilization: 0.50,     // 데모에서 전이가 보이도록 임계값을 낮춰 둔다
        unhealthyUtilization: 0.85,
        tags: ["ready"]);

// ── 3) 계측 수집 ─────────────────────────────────────────────────────────────
builder.Services.AddSingleton<MetricsCollector>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MetricsCollector>());

var app = builder.Build();

// ── 엔드포인트 ───────────────────────────────────────────────────────────────

app.MapGet("/", () => Results.Text("""
    DisruptorCS × ASP.NET Core

      POST /commands                  {"sessionId":1,"opCode":10,"value":7}
      POST /throttle?spin=2000        컨슈머를 느리게 만든다 (0 이면 해제)
      POST /load?rps=20000&seconds=3  부하 발생 (Degraded 전이 확인)
      GET  /health                    헬스체크
      GET  /metrics                   계측 스냅샷
      GET  /pipeline                  파이프라인 상태
    """, "text/plain", Encoding.UTF8));

// 게시 경로. PublishGate 가 백프레셔 정책을 담당한다 — 링이 꽉 찼을 때
// 요청 스레드를 무한정 붙잡지 않고 503 으로 되돌리는 것이 핵심이다.
app.MapPost("/commands", (CommandRequest request, PublishGate<GameCommand> gate) =>
{
    bool published = gate.TryPublish(
        BackpressurePolicy.FailAfterTimeout,
        (cmd, seq) =>
        {
            cmd.SessionId = request.SessionId;
            cmd.OpCode = request.OpCode;
            cmd.Value = request.Value;
        },
        opCode: request.OpCode,
        timeout: TimeSpan.FromMilliseconds(50));

    return published
        ? Results.Accepted()
        : Results.Json(new { error = "파이프라인이 포화 상태입니다.", retryAfterMs = 50 },
                       statusCode: StatusCodes.Status503ServiceUnavailable);
});

// 부하 발생기. 컨슈머보다 빠르게 밀어 넣어 헬스체크가 Degraded 로 넘어가는 것을 보여준다.
app.MapPost("/load", async (PublishGate<GameCommand> gate, int rps = 20_000, int seconds = 3) =>
{
    if (rps is < 1 or > 5_000_000 || seconds is < 1 or > 30)
        return Results.BadRequest(new { error = "rps 는 1~5,000,000, seconds 는 1~30 이어야 합니다." });

    long accepted = 0, rejected = 0;
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    int perTick = Math.Max(1, rps / 100);

    while (DateTime.UtcNow < deadline)
    {
        for (int i = 0; i < perTick; i++)
        {
            bool ok = gate.TryPublish(
                BackpressurePolicy.FailAfterTimeout,
                (cmd, seq) =>
                {
                    cmd.SessionId = i % 1000;
                    cmd.OpCode = 10;
                    cmd.Value = i;
                },
                opCode: 10,
                timeout: TimeSpan.FromMilliseconds(1));

            if (ok) accepted++; else rejected++;
        }

        await Task.Delay(10);
    }

    return Results.Ok(new
    {
        accepted,
        rejected,
        rejectRatio = rejected / (double)Math.Max(1, accepted + rejected),
        note = "거절이 0 이 아니라면 백프레셔가 제대로 동작한 것입니다 — 요청 스레드가 블록되지 않았습니다.",
    });
});

// 컨슈머를 느리게 만들어 백프레셔·헬스체크 전이를 재현한다.
app.MapPost("/throttle", (PipelineThrottle throttle, int spin = 0) =>
{
    throttle.SpinPerEvent = spin;

    return Results.Ok(new
    {
        spinPerEvent = throttle.SpinPerEvent,
        note = spin > 0
            ? "컨슈머가 느려졌습니다. 이제 POST /load 를 걸면 링이 차오르고 헬스가 Degraded 로 바뀝니다."
            : "정상 속도로 되돌렸습니다.",
    });
});

app.MapGet("/pipeline", (Disruptor.Dsl.Disruptor<GameCommand> disruptor, GameWorld world) =>
{
    var ring = disruptor.RingBuffer;

    return Results.Ok(new
    {
        name = disruptor.Name,
        started = disruptor.IsStarted,
        bufferSize = ring.BufferSize,
        cursor = ring.Cursor,
        utilization = Math.Round(ring.Utilization, 4),
        remainingCapacity = ring.RemainingCapacity,
        processors = disruptor.Processors.Select(p => new { p.Name, p.IsRunning, sequence = p.Sequence.Value }),
        applied = world.Applied,
        sessions = world.SessionCount,
    });
});

app.MapGet("/metrics", (MetricsCollector collector) => Results.Ok(collector.Snapshot()));

app.MapGet("/health", async (HealthCheckService health) =>
{
    var report = await health.CheckHealthAsync();

    var payload = new
    {
        status = report.Status.ToString(),
        totalDurationMs = report.TotalDuration.TotalMilliseconds,
        entries = report.Entries.ToDictionary(
            e => e.Key,
            e => new
            {
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                data = e.Value.Data.ToDictionary(d => d.Key, d => JsonSerializable(d.Value)),
            }),
    };

    int statusCode = report.Status switch
    {
        HealthStatus.Healthy => StatusCodes.Status200OK,
        HealthStatus.Degraded => StatusCodes.Status200OK,   // 살아 있지만 뒤처짐 — 트래픽은 계속 받는다
        _ => StatusCodes.Status503ServiceUnavailable,
    };

    return Results.Json(payload, statusCode: statusCode);
});

app.Run();

static object? JsonSerializable(object? value)
    => value is string[] array ? array : value?.ToString();

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>게시 요청 본문.</summary>
public sealed record CommandRequest(long SessionId, int OpCode, int Value);
