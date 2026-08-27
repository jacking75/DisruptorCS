using Microsoft.Coyote.Specifications;

namespace Disruptor.Concurrency.Tests;

/// <summary>
/// 프로세서 수명주기(<c>Run</c> / <c>Halt</c> / <c>Dispose</c>) 축소 모델 (IMPROVEMENTS.md §3.8).
/// </summary>
/// <remarks>
/// <para>
/// 원본: <c>Disruptor/BatchEventProcessor.cs</c> 의 <c>Run</c>. 검증하려는 성질은 두 가지입니다.
/// </para>
/// <list type="number">
///   <item>
///     <b>시작 게이트는 정확히 한 번 신호된다.</b> 놓치면 <c>Disruptor.Start()</c> 가 영원히
///     기다립니다. 두 번 신호되면 <c>CountdownEvent</c> 가 예외를 던집니다. 시작 직후
///     <c>Halt</c> 가 들어오는 경로에서 특히 위험합니다.
///   </item>
///   <item>
///     <b>Halt 가 어느 시점에 들어와도 루프는 반드시 끝난다.</b> 시작 <i>이전</i> 에 들어온
///     Halt 도 유실되면 안 됩니다 — 유실되면 종료가 걸립니다.
///   </item>
/// </list>
/// </remarks>
public sealed class LifecycleModel
{
    private readonly CancellationTokenSource _halt = new();

    private int _running;
    private int _disposed;
    private int _signalCount;
    private bool _loopFinished;

    public int SignalCount => Volatile.Read(ref _signalCount);

    public bool LoopFinished => Volatile.Read(ref _loopFinished);

    /// <summary>이 모델의 <c>CountdownEvent.Signal()</c> 대역 — 호출 횟수를 센다.</summary>
    private void SignalStarted() => Interlocked.Increment(ref _signalCount);

    /// <summary><c>BatchEventProcessor.Run</c> 축소판.</summary>
    public void Run()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            // 실제 코드는 여기서 ObjectDisposedException 을 던진다. 게이트는 아직 신호되지 않았다.
            SignalStarted();
            return;
        }

        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            SignalStarted();   // 중복 시작이어도 게이트는 반드시 풀어준다
            return;
        }

        bool signalled = false;

        try
        {
            // OnStart 훅. 여기서 던져도 게이트가 막히면 안 된다.
            signalled = false;

            SignalStarted();
            signalled = true;

            ProcessEvents();
        }
        finally
        {
            if (!signalled)
                SignalStarted();

            Volatile.Write(ref _running, 0);
        }
    }

    private void ProcessEvents()
    {
        // 실제 루프는 배리어 대기 + 핸들러 호출. 여기서는 halt 관측만 남긴다.
        while (!_halt.IsCancellationRequested)
            Thread.Yield();   // Coyote 가 여기서 다른 스레드로 스케줄을 넘긴다

        Volatile.Write(ref _loopFinished, true);
    }

    /// <summary><c>Halt</c> — 시작 전에 호출되어도 유실되면 안 된다.</summary>
    public void Halt()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        try
        {
            if (!_halt.IsCancellationRequested)
                _halt.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary><c>Dispose</c> — 몇 번 불려도 안전해야 한다.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _halt.Dispose();
    }
}

/// <summary>수명주기에 대한 Coyote 체계적 탐색 진입점.</summary>
public static class LifecycleTests
{
    /// <summary>
    /// <c>Run</c> 과 <c>Halt</c> 를 동시에 — 어떤 인터리빙에서도 루프가 끝나고
    /// 게이트가 정확히 한 번 신호되어야 합니다.
    /// </summary>
    [Microsoft.Coyote.SystematicTesting.Test]
    public static void HaltIsNeverLost()
    {
        var model = new LifecycleModel();

        var runner = Task.Run(model.Run);
        var halter = Task.Run(model.Halt);

        Task.WaitAll(runner, halter);

        Specification.Assert(model.LoopFinished, "Halt 가 유실되어 루프가 끝나지 않았습니다.");
        Specification.Assert(model.SignalCount == 1,
            "시작 게이트가 {0}회 신호되었습니다 (1회여야 합니다).", model.SignalCount);
    }

    /// <summary>
    /// 같은 프로세서를 두 스레드가 동시에 <c>Run</c> — 하나만 성공하고,
    /// 게이트는 <b>두 호출 모두</b>가 풀어줘야 합니다(<c>CountdownEvent</c> 카운트가 2 이므로).
    /// </summary>
    [Microsoft.Coyote.SystematicTesting.Test]
    public static void DoubleRunStillReleasesTheGate()
    {
        var model = new LifecycleModel();

        var a = Task.Run(model.Run);
        var b = Task.Run(model.Run);
        var halter = Task.Run(model.Halt);

        Task.WaitAll(a, b, halter);

        Specification.Assert(model.SignalCount == 2,
            "Run 을 2회 호출했는데 게이트가 {0}회만 신호되었습니다 — Start() 가 멈춥니다.",
            model.SignalCount);
    }

    /// <summary>
    /// <c>Halt</c> 와 <c>Dispose</c> 가 겹쳐도 예외로 죽지 않아야 합니다.
    /// </summary>
    /// <remarks>
    /// <c>CancellationTokenSource.Cancel()</c> 은 이미 Dispose 된 소스에서
    /// <c>ObjectDisposedException</c> 을 던집니다. 종료 경로에서 이 예외가 새어 나가면
    /// 정상 종료가 비정상 종료로 바뀝니다.
    /// </remarks>
    [Microsoft.Coyote.SystematicTesting.Test]
    public static void HaltRacingDisposeDoesNotThrow()
    {
        var model = new LifecycleModel();
        Exception? failure = null;

        var halter = Task.Run(() =>
        {
            try { model.Halt(); }
            catch (Exception ex) { failure = ex; }
        });

        var disposer = Task.Run(() =>
        {
            try { model.Dispose(); }
            catch (Exception ex) { failure = ex; }
        });

        Task.WaitAll(halter, disposer);

        Specification.Assert(failure is null,
            "종료 경로에서 예외가 새어 나왔습니다: {0}", failure?.GetType().Name ?? "none");
    }
}
