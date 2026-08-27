using Microsoft.Coyote.Random;

namespace Disruptor.Concurrency.Tests;

/// <summary>
/// <c>BlockingWaitStrategy</c> 의 신호 경로 축소 모델 (IMPROVEMENTS.md §2.3 / §3.8 1순위).
/// </summary>
/// <remarks>
/// <para>
/// 원본: <c>Disruptor/WaitStrategy/BlockingWaitStrategy.cs</c>. 커서·대기자 수·게이트만 남기고
/// 나머지를 전부 걷어냈습니다. <b>이것은 실제 코드가 아니라 손으로 옮긴 모델입니다</b> —
/// 원본을 고치면 이 파일도 함께 고쳐야 합니다.
/// </para>
/// <para>
/// 검증 대상은 <b>lost wakeup</b> 입니다. 게시자가 커서를 올린 뒤 "대기자가 없다"고 판단해
/// 펄스를 생략했는데, 바로 그 순간 컨슈머가 대기에 들어가면 아무도 깨워주지 않습니다.
/// </para>
/// <para>
/// 이 모델의 <c>Monitor.Wait</c> 에는 <b>타임아웃이 없습니다</b>. 실제 코드에는
/// 100ms 재확인이 백스톱으로 있지만, 백스톱을 켠 채로는 lost wakeup 이 "조금 느려짐"으로
/// 숨어버려 탐색에 걸리지 않습니다. 백스톱을 걷어내야 버그가 교착으로 드러납니다.
/// </para>
/// </remarks>
public sealed class SignalModel
{
    private readonly object _gate = new();
    private readonly bool _buggyOrder;

    private int _numWaiters;
    private long _cursor = -1;

    /// <param name="buggyOrder">
    /// true 면 게시자가 <b>대기자 수를 먼저 읽고</b> 커서를 나중에 씁니다.
    /// 이 순서가 바로 lost wakeup 을 만드는 순서입니다 — 탐색이 실제로 버그를 잡는지
    /// 확인하는 <b>음성 대조군</b>으로 씁니다.
    /// </param>
    public SignalModel(bool buggyOrder = false) => _buggyOrder = buggyOrder;

    /// <summary><c>BlockingWaitStrategy.WaitFor</c> 축소판.</summary>
    public long WaitFor(long sequence)
    {
        long available = Volatile.Read(ref _cursor);
        if (available >= sequence)
            return available;   // 빠른 경로 — lock 진입 없음

        lock (_gate)
        {
            // lock 진입이 전체 펜스다. 아래 재확인은 그 펜스 뒤에서 일어난다.
            _numWaiters++;
            try
            {
                while ((available = Volatile.Read(ref _cursor)) < sequence)
                    Monitor.Wait(_gate);   // 백스톱 없음 — lost wakeup 이면 여기서 영원히 멈춘다
            }
            finally
            {
                _numWaiters--;
            }
        }

        return available;
    }

    /// <summary><c>Sequencer.Publish</c> + <c>SignalAllWhenBlocking</c> 축소판.</summary>
    public void Publish(long sequence)
    {
        if (_buggyOrder)
        {
            // 음성 대조군: 대기자 수를 먼저 읽는다 -> 컨슈머가 그 사이에 대기에 들어가면 유실된다.
            bool hasWaiters = Volatile.Read(ref _numWaiters) > 0;
            Volatile.Write(ref _cursor, sequence);

            if (hasWaiters)
            {
                lock (_gate)
                    Monitor.PulseAll(_gate);
            }

            return;
        }

        // 실제 순서: 커서를 먼저 쓰고, 전체 펜스 뒤에 대기자 수를 읽는다.
        Volatile.Write(ref _cursor, sequence);
        Interlocked.MemoryBarrier();

        if (Volatile.Read(ref _numWaiters) > 0)
        {
            lock (_gate)
                Monitor.PulseAll(_gate);
        }
    }
}

/// <summary>신호 모델에 대한 Coyote 체계적 탐색 진입점.</summary>
public static class SignalTests
{
    /// <summary>
    /// 게시자 1 + 컨슈머 1. 컨슈머가 기다리던 시퀀스는 <b>반드시</b> 도달해야 합니다.
    /// </summary>
    /// <remarks>
    /// lost wakeup 이 발생하면 두 태스크가 모두 진행 불가가 되고 Coyote 가 교착으로 보고합니다.
    /// </remarks>
    [Microsoft.Coyote.SystematicTesting.Test]
    public static void SignalNeverLosesWakeup()
    {
        var model = new SignalModel();
        long observed = -1;

        var consumer = Task.Run(() => observed = model.WaitFor(0));
        var producer = Task.Run(() => model.Publish(0));

        Task.WaitAll(consumer, producer);

        Microsoft.Coyote.Specifications.Specification.Assert(
            observed >= 0, "컨슈머가 게시된 시퀀스를 관측하지 못했습니다: {0}", observed);
    }

    /// <summary>
    /// 게시자 1 + 컨슈머 2. 두 컨슈머가 서로 다른 시퀀스를 기다립니다.
    /// </summary>
    /// <remarks>
    /// <c>PulseAll</c> 이 아니라 <c>Pulse</c> 였다면 여기서 깨어나지 못한 컨슈머가 남습니다.
    /// </remarks>
    [Microsoft.Coyote.SystematicTesting.Test]
    public static void SignalWakesEveryWaiter()
    {
        var model = new SignalModel();
        long first = -1, second = -1;

        var c1 = Task.Run(() => first = model.WaitFor(0));
        var c2 = Task.Run(() => second = model.WaitFor(1));

        var producer = Task.Run(() =>
        {
            model.Publish(0);
            model.Publish(1);
        });

        Task.WaitAll(c1, c2, producer);

        Microsoft.Coyote.Specifications.Specification.Assert(
            first >= 0 && second >= 1, "대기자 하나가 깨어나지 못했습니다: {0}, {1}", first, second);
    }

    /// <summary>
    /// <b>음성 대조군</b> — 일부러 순서를 뒤집은 모델. 탐색이 이 버그를 <b>찾아내야</b> 합니다.
    /// </summary>
    /// <remarks>
    /// 이 테스트가 "버그 없음"으로 통과하면 탐색이 무력하다는 뜻이므로, 위 두 테스트의
    /// 통과 결과도 신뢰할 수 없습니다. 그래서 함께 돌립니다.
    /// </remarks>
    [Microsoft.Coyote.SystematicTesting.Test]
    public static void BuggyOrderLosesWakeup_ExpectedToFail()
    {
        var model = new SignalModel(buggyOrder: true);
        long observed = -1;

        var consumer = Task.Run(() => observed = model.WaitFor(0));
        var producer = Task.Run(() => model.Publish(0));

        Task.WaitAll(consumer, producer);

        Microsoft.Coyote.Specifications.Specification.Assert(
            observed >= 0, "컨슈머가 게시된 시퀀스를 관측하지 못했습니다: {0}", observed);
    }

    /// <summary>난수를 쓰는 모델을 위한 Coyote 제어 생성기 (스케줄과 함께 탐색됩니다).</summary>
    internal static Generator CreateGenerator() => Generator.Create();
}
