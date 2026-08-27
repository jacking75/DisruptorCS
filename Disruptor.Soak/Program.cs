using System.Diagnostics;
using Disruptor.Soak;

// ─────────────────────────────────────────────────────────────────────────────
// 장기 실행 Soak 하니스
//
// 단위 테스트는 회당 수 초입니다. 다음은 그 방식으로 잡히지 않습니다:
//   · 수 시간 후에야 드러나는 미세 누수 (스레드, 핸들, 메모리)
//   · 낮은 확률의 인터리빙
//   · 누적 상태가 커졌을 때의 거동
//
// 사용법:
//   dotnet run -c Release -- --minutes 60 --seed 12345
//   dotnet run -c Release -- --hours 8
// ─────────────────────────────────────────────────────────────────────────────

var options = SoakOptions.Parse(args);

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== DisruptorCS Soak 하니스 ===");
Console.WriteLine($"실행 시간 : {options.Duration}");
Console.WriteLine($"시드      : {options.Seed}  (같은 시드로 재현 가능)");
Console.WriteLine($"보고 주기 : {options.ReportInterval}");
Console.WriteLine();

var scenarios = new List<SoakScenario>
{
    new MultiProducerBackpressureScenario(options.Seed),
    new MulticastSlowConsumerScenario(options.Seed + 1),
    new ChaosExceptionScenario(options.Seed + 2),
    new ShardedRoomsScenario(options.Seed + 3),
};

int exitCode = 0;

try
{
    exitCode = RunSoak(scenarios, options);
}
finally
{
    foreach (var scenario in scenarios)
        scenario.Dispose();
}

Environment.Exit(exitCode);

static int RunSoak(List<SoakScenario> scenarios, SoakOptions options)
{
    var overall = Stopwatch.StartNew();
    var lastReport = Stopwatch.StartNew();

    // 누수 감지를 위한 기준선. 워밍업 후에 잡는다.
    long baselineAllocated = 0;
    int baselineThreads = 0;
    bool baselineTaken = false;

    long rounds = 0;

    while (overall.Elapsed < options.Duration)
    {
        foreach (SoakScenario scenario in scenarios)
        {
            try
            {
                scenario.RunRound();
                scenario.CheckInvariants();
            }
            catch (InvariantViolationException ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"### 불변식 위반: {scenario.Name}");
                Console.Error.WriteLine($"### {ex.Message}");
                Console.Error.WriteLine($"### 라운드 {rounds:N0}, 경과 {overall.Elapsed}");
                Console.Error.WriteLine($"### 재현: --seed {options.Seed}");
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"### 예기치 못한 예외: {scenario.Name}");
                Console.Error.WriteLine(ex);
                Console.Error.WriteLine($"### 재현: --seed {options.Seed}");
                return 2;
            }
        }

        rounds++;

        // 3라운드 후에 기준선을 잡는다 (JIT/풀 워밍업 이후).
        if (!baselineTaken && rounds == 3)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);

            baselineAllocated = GC.GetTotalMemory(forceFullCollection: true);
            baselineThreads = Process.GetCurrentProcess().Threads.Count;
            baselineTaken = true;

            Console.WriteLine($"[기준선] 힙 {baselineAllocated / 1024.0 / 1024:F1} MB, " +
                              $"스레드 {baselineThreads}개");
            Console.WriteLine();
        }

        if (lastReport.Elapsed >= options.ReportInterval)
        {
            Report(scenarios, overall, rounds, baselineAllocated, baselineThreads, baselineTaken);
            lastReport.Restart();

            if (baselineTaken && !CheckResourceLeaks(baselineAllocated, baselineThreads, out string? leak))
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"### 자원 누수 의심: {leak}");
                Console.Error.WriteLine($"### 재현: --seed {options.Seed}");
                return 3;
            }
        }
    }

    Console.WriteLine();
    Report(scenarios, overall, rounds, baselineAllocated, baselineThreads, baselineTaken);
    Console.WriteLine();
    Console.WriteLine($"완료: {rounds:N0} 라운드 / {overall.Elapsed} — 불변식 위반 없음");
    return 0;
}

static void Report(List<SoakScenario> scenarios, Stopwatch overall, long rounds,
                   long baselineHeap, int baselineThreads, bool baselineTaken)
{
    Console.WriteLine($"[{overall.Elapsed:hh\\:mm\\:ss}] 라운드 {rounds:N0}");

    foreach (SoakScenario scenario in scenarios)
        Console.WriteLine($"    {scenario.Name,-34} {scenario.Describe()}");

    long heap = GC.GetTotalMemory(forceFullCollection: false);
    int threads = Process.GetCurrentProcess().Threads.Count;

    string heapDelta = baselineTaken
        ? $" (기준선 대비 {(heap - baselineHeap) / 1024.0 / 1024:+0.0;-0.0} MB)"
        : string.Empty;
    string threadDelta = baselineTaken
        ? $" (기준선 대비 {threads - baselineThreads:+0;-0})"
        : string.Empty;

    Console.WriteLine($"    {"자원",-34} 힙 {heap / 1024.0 / 1024:F1} MB{heapDelta}, " +
                      $"스레드 {threads}개{threadDelta}, " +
                      $"GC gen0/1/2 {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
    Console.WriteLine();
}

static bool CheckResourceLeaks(long baselineHeap, int baselineThreads, out string? reason)
{
    reason = null;

    int threads = Process.GetCurrentProcess().Threads.Count;
    if (threads > baselineThreads + 32)
    {
        reason = $"스레드 {baselineThreads} → {threads} (32개 초과 증가). 프로세서가 정리되지 않고 있습니다.";
        return false;
    }

    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
    GC.WaitForPendingFinalizers();
    long heap = GC.GetTotalMemory(forceFullCollection: true);

    // 링 버퍼는 사전 할당이므로 안정 상태 힙은 거의 변하지 않아야 한다.
    if (heap > baselineHeap * 3 && heap - baselineHeap > 256L * 1024 * 1024)
    {
        reason = $"힙 {baselineHeap / 1024.0 / 1024:F1} MB → {heap / 1024.0 / 1024:F1} MB " +
                 "(3배 초과 + 256MB 초과 증가).";
        return false;
    }

    return true;
}

namespace Disruptor.Soak
{
    internal sealed record SoakOptions(TimeSpan Duration, int Seed, TimeSpan ReportInterval)
    {
        public static SoakOptions Parse(string[] args)
        {
            TimeSpan duration = TimeSpan.FromMinutes(1);
            int seed = 12345;
            TimeSpan report = TimeSpan.FromSeconds(15);

            for (int i = 0; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case "--minutes" when double.TryParse(args[i + 1], out double m):
                        duration = TimeSpan.FromMinutes(m);
                        break;
                    case "--hours" when double.TryParse(args[i + 1], out double h):
                        duration = TimeSpan.FromHours(h);
                        break;
                    case "--seconds" when double.TryParse(args[i + 1], out double s):
                        duration = TimeSpan.FromSeconds(s);
                        break;
                    case "--seed" when int.TryParse(args[i + 1], out int sd):
                        seed = sd;
                        break;
                    case "--report-seconds" when double.TryParse(args[i + 1], out double r):
                        report = TimeSpan.FromSeconds(r);
                        break;
                }
            }

            return new SoakOptions(duration, seed, report);
        }
    }
}
