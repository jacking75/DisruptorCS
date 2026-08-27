using System.Diagnostics;
using Disruptor;
using Disruptor.Game;
using Disruptor.WaitStrategy;

// ─────────────────────────────────────────────────────────────────────────────
// 이 샘플이 답하는 질문:
//   "이미 메인 루프가 있는데, 스레드 모델을 바꾸지 않고 Disruptor 를 쓸 수 있나?"
//
// 답: EventPoller 를 쓰면 됩니다. 소비 전용 스레드가 0개 추가됩니다 —
// Unity 의 Update(), 자체 서버의 프레임 루프, SDL 틱 어디에나 그대로 끼워 넣을 수 있습니다.
// ─────────────────────────────────────────────────────────────────────────────

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== 샘플: 기존 게임 루프에 EventPoller 통합 ===\n");

// 유휴 구간에도 틱 정밀도를 유지한다 (Windows 기본 타이머는 15.6ms 로 양자화됨).
using var timerScope = new HighResolutionTimerScope();
Console.WriteLine($"고분해능 타이머: {(timerScope.IsActive ? "활성" : "비활성 (Windows 아님)")}\n");

const int RingSize = 4096;
const int FrameBudgetEvents = 512;   // 한 프레임에 처리할 최대 커맨드 수

// 네트워크 I/O 스레드들이 커맨드를 게시한다 (MP).
var ring = RingBuffer<PlayerCommand>.CreateMultiProducer(
    () => new PlayerCommand(), RingSize, new YieldingWaitStrategy());

// 소비는 메인 루프가 직접 한다 — 전용 스레드 없음.
using var poller = ring.NewPoller();

var world = new World();
var gate = new PublishGate<PlayerCommand>(ring) { CongestionThreshold = 0.85 };

using var shutdown = new CancellationTokenSource();
var networkThreads = StartNetworkThreads(gate, shutdown.Token, threadCount: 4);

// ── 메인 게임 루프 ───────────────────────────────────────────────────────────
var driver = FixedTickDriver.At60Hz(tick => world.Simulate(tick));
var totalTimer = Stopwatch.StartNew();
var frameTimes = new List<double>(4096);
long consumed = 0;
long frames = 0;

while (totalTimer.Elapsed < TimeSpan.FromSeconds(3))
{
    long frameStart = Stopwatch.GetTimestamp();

    // (1) 이번 프레임에 처리할 커맨드를 당겨온다. 프레임 예산을 넘지 않는다.
    poller.Poll(static (cmd, seq, endOfBatch) =>
    {
        WorldRef.Current!.Apply(cmd);
        return true;
    }, maxEvents: FrameBudgetEvents);

    consumed = world.AppliedCommands;

    // (2) 고정 스텝 시뮬레이션
    driver.TryAdvance();

    // (3) 렌더/송신 (여기서는 생략)
    frames++;

    double frameMs = (Stopwatch.GetTimestamp() - frameStart) * 1000.0 / Stopwatch.Frequency;
    frameTimes.Add(frameMs);

    // 다음 프레임까지 대기 (실제 엔진이라면 VSync 나 자체 페이싱)
    TimeSpan remaining = driver.TimeUntilNextTick();
    if (remaining > TimeSpan.FromMilliseconds(1))
        Thread.Sleep(1);
}

shutdown.Cancel();
foreach (var t in networkThreads) t.Join(TimeSpan.FromSeconds(2));

// 남은 커맨드를 드레인
while (poller.Poll(static (cmd, seq, eob) => { WorldRef.Current!.Apply(cmd); return true; }, 4096)
       == PollState.Processing)
{
}

totalTimer.Stop();

frameTimes.Sort();
double P(double q) => frameTimes[(int)(q * (frameTimes.Count - 1))];

Console.WriteLine($"실행 시간       : {totalTimer.Elapsed.TotalSeconds:F1}초");
Console.WriteLine($"프레임          : {frames:N0} ({frames / totalTimer.Elapsed.TotalSeconds:F0} fps)");
Console.WriteLine($"틱              : {driver.CurrentTick:N0} (60Hz 기대 {totalTimer.Elapsed.TotalSeconds * 60:F0})");
Console.WriteLine($"건너뛴 틱       : {driver.SkippedTicks}");
Console.WriteLine($"게시 커맨드     : {gate.PublishedCount:N0} (드롭 {gate.DroppedCount:N0})");
Console.WriteLine($"적용 커맨드     : {world.AppliedCommands:N0}");
Console.WriteLine($"프레임 시간     : p50={P(0.50):F3}ms  p99={P(0.99):F3}ms  max={frameTimes[^1]:F3}ms");
Console.WriteLine();
Console.WriteLine("소비 전용 스레드 : 0개 (메인 루프가 직접 폴링)");
Console.WriteLine("→ 기존 엔진 루프에 스레드 모델 변경 없이 Disruptor 를 끼워 넣었습니다.");

static Thread[] StartNetworkThreads(PublishGate<PlayerCommand> gate, CancellationToken ct, int threadCount)
{
    var threads = new Thread[threadCount];

    for (int t = 0; t < threadCount; t++)
    {
        int sessionBase = t * 1000;

        threads[t] = new Thread(() =>
        {
            int n = 0;
            while (!ct.IsCancellationRequested)
            {
                // 이동 패킷: 최신값만 의미 있으므로 혼잡하면 버린다.
                gate.TryPublish(BackpressurePolicy.DropWhenCongested,
                                (cmd, seq) =>
                                {
                                    cmd.SessionId = sessionBase + (n % 100);
                                    cmd.OpCode = OpCodes.Move;
                                    cmd.X = n % 1000;
                                    cmd.Y = (n * 7) % 1000;
                                },
                                opCode: OpCodes.Move);
                n++;

                if ((n & 255) == 0)
                    Thread.Sleep(1); // 네트워크 대기 흉내
            }
        })
        { IsBackground = true, Name = $"net-io-{t}" };

        threads[t].Start();
    }

    return threads;
}

// ─────────────────────────────────────────────────────────────────────────────

internal static class OpCodes
{
    public const int Move = 10;
}

/// <summary>플레이어 커맨드. 사전 할당되어 슬롯이 재사용됩니다.</summary>
public sealed class PlayerCommand
{
    public long SessionId;
    public int OpCode;
    public int X;
    public int Y;
}

/// <summary>
/// 폴러 콜백을 <c>static</c> 람다로 유지하기 위한 앰비언트 참조.
/// </summary>
/// <remarks>
/// 캡처하는 람다를 쓰면 폴링마다 클로저가 할당됩니다(Analyzer DISR003 이 잡아냅니다).
/// 실제 코드에서는 폴러를 감싼 클래스의 인스턴스 메서드를 쓰는 편이 더 깔끔합니다.
/// </remarks>
internal static class WorldRef
{
    [ThreadStatic]
    private static World? _current;

    public static World? Current
    {
        get => _current;
        set => _current = value;
    }
}

public sealed class World
{
    private readonly Dictionary<long, (int X, int Y)> _positions = new();
    private long _applied;

    public World() => WorldRef.Current = this;

    public long AppliedCommands => _applied;

    public void Apply(PlayerCommand cmd)
    {
        _positions[cmd.SessionId] = (cmd.X, cmd.Y);
        _applied++;
    }

    public void Simulate(long tick)
    {
        // 실제라면 여기서 물리·AI·충돌을 고정 스텝으로 처리한다.
    }
}
