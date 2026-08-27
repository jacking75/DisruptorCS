namespace Disruptor.Concurrency.Tests;

/// <summary>
/// <c>WorkProcessor</c> 의 클레임 루프 축소 모델 (IMPROVEMENTS.md §3.8).
/// </summary>
/// <remarks>
/// <para>
/// 원본: <c>Disruptor/Work/WorkProcessor.cs</c> / <c>ValueWorkProcessor.cs</c>.
/// 워커들이 공유 <c>workSequence</c> 를 CAS 로 경쟁 점유하는 부분만 남겼습니다.
/// </para>
/// <para>
/// 워커 풀은 순서를 보장하지 않지만 <b>정확히 한 번</b>은 보장해야 합니다.
/// 같은 시퀀스를 두 워커가 처리하면 결제가 두 번 나가고, 아무도 처리하지 않으면 이벤트가 사라집니다.
/// </para>
/// </remarks>
public sealed class ClaimModel
{
    private readonly long[] _processedBy;
    private readonly int _available;

    private long _workSequence = -1;

    public ClaimModel(int available)
    {
        _available = available;
        _processedBy = new long[available];
        Array.Fill(_processedBy, -1);
    }

    /// <summary>워커 하나가 소진될 때까지 클레임하고 처리합니다.</summary>
    public void RunWorker(int workerId)
    {
        while (true)
        {
            long claimed;

            // 원본과 동일한 형태의 CAS 점유 루프.
            while (true)
            {
                claimed = Volatile.Read(ref _workSequence);
                long next = claimed + 1L;

                if (next >= _available)
                    return;   // 더 가져갈 것이 없다

                if (Interlocked.CompareExchange(ref _workSequence, next, claimed) == claimed)
                {
                    claimed = next;
                    break;
                }
            }

            // 중복 클레임 검출: 이 슬롯은 아직 아무도 처리하지 않았어야 한다.
            Microsoft.Coyote.Specifications.Specification.Assert(
                Volatile.Read(ref _processedBy[claimed]) == -1,
                "시퀀스 {0} 을 워커 {1} 과 {2} 가 중복 처리했습니다.",
                claimed, _processedBy[claimed], workerId);

            Volatile.Write(ref _processedBy[claimed], workerId);
        }
    }

    /// <summary>모든 시퀀스가 정확히 한 번씩 처리되었는가.</summary>
    public void AssertAllProcessedExactlyOnce()
    {
        for (int i = 0; i < _available; i++)
        {
            Microsoft.Coyote.Specifications.Specification.Assert(
                Volatile.Read(ref _processedBy[i]) != -1,
                "시퀀스 {0} 을 아무도 처리하지 않았습니다 (이벤트 유실).", i);
        }
    }
}

/// <summary>클레임 루프에 대한 Coyote 체계적 탐색 진입점.</summary>
public static class ClaimTests
{
    /// <summary>워커 2개 · 시퀀스 3개 — 중복도 유실도 없어야 합니다.</summary>
    [Microsoft.Coyote.SystematicTesting.Test]
    public static void TwoWorkersClaimEachSequenceExactlyOnce()
    {
        var model = new ClaimModel(available: 3);

        var w0 = Task.Run(() => model.RunWorker(0));
        var w1 = Task.Run(() => model.RunWorker(1));

        Task.WaitAll(w0, w1);
        model.AssertAllProcessedExactlyOnce();
    }

    /// <summary>워커 3개 · 시퀀스 4개 — 경쟁자를 늘려도 같은 불변식이 성립해야 합니다.</summary>
    [Microsoft.Coyote.SystematicTesting.Test]
    public static void ThreeWorkersClaimEachSequenceExactlyOnce()
    {
        var model = new ClaimModel(available: 4);

        var workers = new[]
        {
            Task.Run(() => model.RunWorker(0)),
            Task.Run(() => model.RunWorker(1)),
            Task.Run(() => model.RunWorker(2)),
        };

        Task.WaitAll(workers);
        model.AssertAllProcessedExactlyOnce();
    }
}
