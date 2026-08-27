using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;
using Disruptor.Concurrency.Tests;

// ─────────────────────────────────────────────────────────────────────────────
// Coyote 체계적 동시성 탐색 러너 (IMPROVEMENTS.md §3.8)
//
// 스트레스 테스트는 "운이 좋으면" 인터리빙을 밟습니다. Coyote 는 스케줄러를 직접 쥐고
// 인터리빙을 <b>체계적으로</b> 밟습니다 — 낮은 확률의 경쟁 조건이 재현 가능한 형태로 나옵니다.
//
// 실행 (반드시 rewrite 후에):
//   dotnet build   Disruptor.Concurrency.Tests -c Release
//   dotnet coyote  rewrite Disruptor.Concurrency.Tests/rewrite.coyote.json
//   dotnet Disruptor.Concurrency.Tests/bin/Release/net8.0/rewritten/Disruptor.Concurrency.Tests.dll
//
// 한계: Coyote 는 스케줄(인터리빙)을 탐색할 뿐, `Interlocked.MemoryBarrier` 수준의
// 하드웨어 메모리 순서까지 모델링하지 않습니다. §2.3 의 메모리 순서 논증을 대체하지 않으며
// 병행 수단입니다.
// ─────────────────────────────────────────────────────────────────────────────

int iterations = 1_000;
string? only = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-i" or "--iterations" when i + 1 < args.Length:
            iterations = int.Parse(args[++i]);
            break;

        case "-m" or "--method" when i + 1 < args.Length:
            only = args[++i];
            break;

        case "-h" or "--help":
            Console.WriteLine("사용법: <exe> [-i <반복>] [-m <테스트 이름>]");
            return 0;
    }
}

var models = new (string Name, Action Body, bool ExpectBug)[]
{
    ("SignalNeverLosesWakeup",              SignalTests.SignalNeverLosesWakeup,                      false),
    ("SignalWakesEveryWaiter",              SignalTests.SignalWakesEveryWaiter,                      false),
    ("BuggyOrderLosesWakeup_ExpectedToFail", SignalTests.BuggyOrderLosesWakeup_ExpectedToFail,       true),
    ("TwoWorkersClaimEachSequenceExactlyOnce",   ClaimTests.TwoWorkersClaimEachSequenceExactlyOnce,  false),
    ("ThreeWorkersClaimEachSequenceExactlyOnce", ClaimTests.ThreeWorkersClaimEachSequenceExactlyOnce, false),
    ("HaltIsNeverLost",                     LifecycleTests.HaltIsNeverLost,                          false),
    ("DoubleRunStillReleasesTheGate",       LifecycleTests.DoubleRunStillReleasesTheGate,            false),
    ("HaltRacingDisposeDoesNotThrow",       LifecycleTests.HaltRacingDisposeDoesNotThrow,            false),
};

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"=== Coyote 체계적 동시성 탐색 ({iterations:N0} 반복/모델) ===\n");

int failures = 0;
int executed = 0;

foreach (var (name, body, expectBug) in models)
{
    if (only is not null && !name.Contains(only, StringComparison.OrdinalIgnoreCase))
        continue;

    executed++;

    var configuration = Configuration.Create()
        .WithTestingIterations((uint)iterations)
        .WithMaxSchedulingSteps(500)
        .WithDeadlockTimeout(2_000)
        .WithConsoleLoggingEnabled(false);

    using var engine = TestingEngine.Create(configuration, body);

    // 반복마다 나오는 진행 로그를 메모리로 돌린다 — 요약만 보여주기 위해서다.
    // 버그 리포트는 TestReport 에서 따로 꺼내므로 정보를 잃지 않는다.
    using var silent = new Microsoft.Coyote.Logging.MemoryLogger(
        Microsoft.Coyote.Logging.VerbosityLevel.None);
    engine.SetLogger(silent);

    engine.Run();

    int bugs = engine.TestReport.NumOfFoundBugs;
    bool ok = expectBug ? bugs > 0 : bugs == 0;

    string verdict = ok
        ? (expectBug ? "OK   (예상대로 버그 발견 — 탐색이 살아 있음)" : "OK")
        : (expectBug ? "FAIL (버그를 찾지 못함 — 탐색이 무력하다는 뜻)" : "FAIL");

    Console.WriteLine($"{verdict,-46} {name}");
    Console.WriteLine($"{"",-46} 반복 {engine.TestReport.NumOfExploredFairPaths + engine.TestReport.NumOfExploredUnfairPaths:N0} · 버그 {bugs}");

    if (!ok)
    {
        failures++;
        foreach (string trace in engine.TestReport.BugReports.Take(1))
            Console.Error.WriteLine($"    {trace}");

        if (!expectBug && bugs == 0)
            Console.Error.WriteLine("    (버그가 있어야 하는데 없습니다)");
    }
    else if (expectBug)
    {
        // 대조군이 어떤 성질을 깼는지 한 줄로 남긴다.
        foreach (string trace in engine.TestReport.BugReports.Take(1))
            Console.WriteLine($"{"",-46} {Truncate(trace, 100)}");
    }

    Console.WriteLine();
}

Console.WriteLine(new string('-', 78));
Console.WriteLine(failures == 0
    ? $"모델 {executed}개 전부 통과 (음성 대조군 포함)."
    : $"모델 {executed}개 중 {failures}개 실패.");

return failures == 0 ? 0 : 1;

static string Truncate(string text, int max)
{
    string flat = text.ReplaceLineEndings(" ");
    return flat.Length <= max ? flat : flat[..max] + "...";
}
